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
    private SystemHardwareInfo? _hardwareInfo;

    public SystemResourceSnapshot Sample()
    {
        var gpuCounters = GetGpuCounters();
        var memory = SampleMemory();
        var vram = gpuCounters?.SampleVram();
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

        public VramSnapshot? SampleVram()
        {
            var usage = SumCounters(_vramUsageCounters);
            var limit = SumCounters(_vramLimitCounters);
            if (usage is null || limit is null || limit <= 0)
            {
                return null;
            }

            return new VramSnapshot((usage.Value / limit.Value) * 100d, usage.Value, limit.Value);
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
