using System.Diagnostics;
using System.Reflection;
using System.Globalization;
using System.Runtime.InteropServices;
using Ali.Infrastructure.Runtime;

namespace Ali.App.Wpf.ViewModels;

internal sealed class SystemResourceMonitor
{
    private CpuTimes? _previousCpuTimes;
    private GpuCounterReader? _gpuCounters;
    private Task<GpuCounterReader?>? _gpuCounterLoadTask;
    private readonly NvidiaSmiVramReader _nvidiaSmiVramReader = new();
    private SystemHardwareInfo? _hardwareInfo;

    public SystemResourceSnapshot Sample()
    {
        var gpuCounters = GetGpuCounters();
        var memory = SampleMemory();
        var hardwareVramLimit = GetHardwareVramLimit();
        var vram = _nvidiaSmiVramReader.Sample()
                   ?? gpuCounters?.SampleVram(hardwareVramLimit);
        return new SystemResourceSnapshot(
            CpuPercent: SampleCpuPercent(),
            RamPercent: memory?.Percent,
            GpuPercent: gpuCounters?.SampleGpuPercent(),
            VramPercent: vram?.Percent,
            TotalRamBytes: memory?.TotalBytes,
            AvailableRamBytes: memory?.AvailableBytes,
            VramUsageBytes: vram?.UsageBytes,
            VramLimitBytes: vram?.LimitBytes);
    }

    public RuntimeMachineResourceSnapshot CaptureRuntimeMachineSnapshot()
    {
        var snapshot = Sample();
        var hardware = GetHardwareInfo();
        var hardwareVramLimit = hardware.Gpus
            .Where(gpu => gpu.DedicatedMemoryBytes is > 0)
            .Select(gpu => (double)gpu.DedicatedMemoryBytes!.Value)
            .DefaultIfEmpty()
            .Max();

        return new RuntimeMachineResourceSnapshot(
            snapshot.CpuPercent,
            snapshot.RamPercent,
            snapshot.GpuPercent,
            snapshot.VramPercent,
            TotalRamBytes: snapshot.TotalRamBytes ?? hardware.TotalRamBytes,
            AvailableRamBytes: snapshot.AvailableRamBytes,
            VramUsageBytes: snapshot.VramUsageBytes,
            VramLimitBytes: snapshot.VramLimitBytes ?? (hardwareVramLimit > 0 ? hardwareVramLimit : null),
            CpuName: hardware.CpuName,
            LogicalProcessorCount: hardware.LogicalProcessorCount,
            Gpus: hardware.Gpus);
    }

    private GpuCounterReader? GetGpuCounters()
    {
        if (_gpuCounters is not null)
        {
            return _gpuCounters;
        }

        if (_gpuCounterLoadTask is null)
        {
            _gpuCounterLoadTask = Task.Run(GpuCounterReader.TryCreate);
            return null;
        }

        if (!_gpuCounterLoadTask.IsCompletedSuccessfully)
        {
            return null;
        }

        _gpuCounters = _gpuCounterLoadTask.Result;
        return _gpuCounters;
    }

    private SystemHardwareInfo GetHardwareInfo()
    {
        if (_hardwareInfo is not null)
        {
            return _hardwareInfo;
        }

        _hardwareInfo = SystemHardwareInfoReader.Read();
        return _hardwareInfo;
    }

    private double? GetHardwareVramLimit()
    {
        var hardware = GetHardwareInfo();
        var limit = hardware.Gpus
            .Where(gpu => gpu.DedicatedMemoryBytes is > 0)
            .Select(gpu => (double)gpu.DedicatedMemoryBytes!.Value)
            .DefaultIfEmpty()
            .Max();

        return limit > 0 ? limit : null;
    }

    private double? SampleCpuPercent()
    {
        if (!OperatingSystem.IsWindows() || !GetSystemTimes(out var idle, out var kernel, out var user))
        {
            return null;
        }

        var current = new CpuTimes(idle.ToUInt64(), kernel.ToUInt64(), user.ToUInt64());
        if (_previousCpuTimes is null)
        {
            _previousCpuTimes = current;
            return 0;
        }

        var previous = _previousCpuTimes.Value;
        _previousCpuTimes = current;

        var idleDelta = current.Idle - previous.Idle;
        var kernelDelta = current.Kernel - previous.Kernel;
        var userDelta = current.User - previous.User;
        var total = kernelDelta + userDelta;
        if (total == 0)
        {
            return 0;
        }

        return (1d - (idleDelta / (double)total)) * 100d;
    }

    private static MemorySnapshot? SampleMemory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var memoryStatus = new MemoryStatusEx();
        memoryStatus.dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>();
        return GlobalMemoryStatusEx(ref memoryStatus)
            ? new MemorySnapshot(memoryStatus.dwMemoryLoad, memoryStatus.ullTotalPhys, memoryStatus.ullAvailPhys)
            : null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct FileTime
    {
        private readonly uint dwLowDateTime;
        private readonly uint dwHighDateTime;

        public ulong ToUInt64() => ((ulong)dwHighDateTime << 32) | dwLowDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    private readonly record struct CpuTimes(ulong Idle, ulong Kernel, ulong User);

    private readonly record struct MemorySnapshot(double Percent, ulong TotalBytes, ulong AvailableBytes);

    private readonly record struct VramSnapshot(double Percent, double UsageBytes, double LimitBytes);


    private sealed class GpuCounterReader
    {
        private readonly Type _counterType;
        private readonly List<object> _gpuUtilizationCounters;
        private readonly List<object> _vramUsageCounters;
        private readonly List<object> _vramLimitCounters;

        private GpuCounterReader(
            Type counterType,
            List<object> gpuUtilizationCounters,
            List<object> vramUsageCounters,
            List<object> vramLimitCounters)
        {
            _counterType = counterType;
            _gpuUtilizationCounters = gpuUtilizationCounters;
            _vramUsageCounters = vramUsageCounters;
            _vramLimitCounters = vramLimitCounters;
        }

        public static GpuCounterReader? TryCreate()
        {
            if (!OperatingSystem.IsWindows())
            {
                return null;
            }

            try
            {
                var assembly = Assembly.Load("System.Diagnostics.PerformanceCounter");
                var categoryType = assembly.GetType("System.Diagnostics.PerformanceCounterCategory");
                var counterType = assembly.GetType("System.Diagnostics.PerformanceCounter");
                if (categoryType is null || counterType is null)
                {
                    return null;
                }

                var gpuCounters = CreateCounters(
                    categoryType,
                    counterType,
                    "GPU Engine",
                    "Utilization Percentage",
                    instance => instance.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase));
                var vramUsageCounters = CreateCounters(
                    categoryType,
                    counterType,
                    "GPU Adapter Memory",
                    "Dedicated Usage",
                    _ => true);
                var vramLimitCounters = CreateCounters(
                    categoryType,
                    counterType,
                    "GPU Adapter Memory",
                    "Dedicated Limit",
                    _ => true);

                if (gpuCounters.Count == 0 && (vramUsageCounters.Count == 0 || vramLimitCounters.Count == 0))
                {
                    return null;
                }

                return new GpuCounterReader(counterType, gpuCounters, vramUsageCounters, vramLimitCounters);
            }
            catch
            {
                return null;
            }
        }

        public double? SampleGpuPercent()
        {
            var value = SumCounters(_gpuUtilizationCounters);
            return value is null ? null : Math.Min(100d, value.Value);
        }

        public VramSnapshot? SampleVram(double? fallbackLimitBytes)
        {
            var usage = SumCounters(_vramUsageCounters);
            var limit = SumCounters(_vramLimitCounters);
            if (usage is null)
            {
                return null;
            }

            var effectiveLimit = limit is > 0
                ? limit.Value
                : fallbackLimitBytes is > 0
                    ? fallbackLimitBytes.Value
                    : 0;
            if (effectiveLimit <= 0)
            {
                return null;
            }

            return new VramSnapshot((usage.Value / effectiveLimit) * 100d, usage.Value, effectiveLimit);
        }

        private double? SumCounters(IReadOnlyList<object> counters)
        {
            if (counters.Count == 0)
            {
                return null;
            }

            var nextValue = _counterType.GetMethod("NextValue", BindingFlags.Instance | BindingFlags.Public);
            if (nextValue is null)
            {
                return null;
            }

            double total = 0;
            var readCount = 0;
            foreach (var counter in counters)
            {
                try
                {
                    total += Convert.ToDouble(nextValue.Invoke(counter, null), CultureInfo.InvariantCulture);
                    readCount++;
                }
                catch
                {
                    // A disappearing GPU counter should not break the cockpit.
                }
            }

            return readCount == 0 ? null : total;
        }

        private static List<object> CreateCounters(
            Type categoryType,
            Type counterType,
            string categoryName,
            string counterName,
            Func<string, bool> includeInstance)
        {
            var counters = new List<object>();
            if (!CategoryExists(categoryType, categoryName))
            {
                return counters;
            }

            var category = Activator.CreateInstance(categoryType, categoryName);
            var getInstanceNames = categoryType.GetMethod("GetInstanceNames", BindingFlags.Instance | BindingFlags.Public);
            var instanceNames = getInstanceNames?.Invoke(category, null) as string[] ?? Array.Empty<string>();

            foreach (var instanceName in instanceNames.Where(includeInstance))
            {
                try
                {
                    var counter = Activator.CreateInstance(counterType, categoryName, counterName, instanceName, true);
                    var nextValue = counterType.GetMethod("NextValue", BindingFlags.Instance | BindingFlags.Public);
                    nextValue?.Invoke(counter, null);
                    if (counter is not null)
                    {
                        counters.Add(counter);
                    }
                }
                catch
                {
                    // Some driver/counter combinations are missing individual counter names.
                }
            }

            return counters;
        }

        private static bool CategoryExists(Type categoryType, string categoryName)
        {
            try
            {
                var exists = categoryType.GetMethod(
                    "Exists",
                    BindingFlags.Static | BindingFlags.Public,
                    binder: null,
                    types: [typeof(string)],
                    modifiers: null);
                return exists?.Invoke(null, [categoryName]) is true;
            }
            catch
            {
                return false;
            }
        }
    }

    private sealed class NvidiaSmiVramReader
    {
        private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(2);
        private DateTimeOffset _lastSampleAt = DateTimeOffset.MinValue;
        private VramSnapshot? _lastSample;
        private bool _unavailable;

        public VramSnapshot? Sample()
        {
            if (!OperatingSystem.IsWindows() || _unavailable)
            {
                return null;
            }

            var now = DateTimeOffset.UtcNow;
            if (now - _lastSampleAt < CacheDuration)
            {
                return _lastSample;
            }

            _lastSampleAt = now;
            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "nvidia-smi.exe",
                        Arguments = "--query-gpu=memory.used,memory.total --format=csv,noheader,nounits",
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false
                    }
                };

                process.Start();
                if (!process.WaitForExit(1200))
                {
                    TryKillProcess(process);
                    return _lastSample;
                }

                if (process.ExitCode != 0)
                {
                    _unavailable = true;
                    return null;
                }

                var output = process.StandardOutput.ReadToEnd();
                var usedMib = 0d;
                var totalMib = 0d;
                foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = line.Split(',', 2, StringSplitOptions.TrimEntries);
                    if (parts.Length < 2)
                    {
                        continue;
                    }

                    if (double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var used)
                        && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var total)
                        && used >= 0
                        && total > 0)
                    {
                        usedMib += used;
                        totalMib += total;
                    }
                }

                _lastSample = totalMib > 0
                    ? new VramSnapshot(
                        usedMib / totalMib * 100d,
                        usedMib * 1024d * 1024d,
                        totalMib * 1024d * 1024d)
                    : null;
                return _lastSample;
            }
            catch
            {
                _unavailable = true;
                return null;
            }
        }

        private static void TryKillProcess(Process process)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best-effort fallback sampling should never disturb the cockpit.
            }
        }
    }
}

internal sealed record SystemResourceSnapshot(
    double? CpuPercent,
    double? RamPercent,
    double? GpuPercent,
    double? VramPercent,
    ulong? TotalRamBytes = null,
    ulong? AvailableRamBytes = null,
    double? VramUsageBytes = null,
    double? VramLimitBytes = null);
