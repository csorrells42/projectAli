using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Ali.Modules.Runtime;
using Microsoft.Win32;

namespace Ali.UI.ViewModels;

internal sealed record SystemHardwareInfo(
    string? CpuName,
    int LogicalProcessorCount,
    ulong? TotalRamBytes,
    IReadOnlyList<RuntimeGpuHardwareInfo> Gpus);

internal static class SystemHardwareInfoReader
{
    public static SystemHardwareInfo Read()
    {
        var memory = ReadMemory();
        var gpus = ReadNvidiaSmiGpus();
        if (gpus.Count == 0)
        {
            gpus = ReadDxgiGpus();
        }

        if (gpus.Count == 0)
        {
            gpus = ReadWindowsVideoControllers();
        }

        return new SystemHardwareInfo(
            ReadCpuName(),
            Environment.ProcessorCount,
            memory?.TotalBytes,
            gpus);
    }

    private static string? ReadCpuName()
    {
        try
        {
            return Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0",
                "ProcessorNameString",
                null) as string;
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<RuntimeGpuHardwareInfo> ReadNvidiaSmiGpus()
    {
        var output = RunProcess(
            "nvidia-smi.exe",
            "--query-gpu=name,memory.total --format=csv,noheader,nounits",
            TimeSpan.FromSeconds(3));
        if (string.IsNullOrWhiteSpace(output))
        {
            return Array.Empty<RuntimeGpuHardwareInfo>();
        }

        var gpus = new List<RuntimeGpuHardwareInfo>();
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(',', 2);
            var name = parts[0].Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            ulong? bytes = null;
            if (parts.Length > 1
                && double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var mib)
                && mib > 0)
            {
                bytes = (ulong)(mib * 1024d * 1024d);
            }

            gpus.Add(new RuntimeGpuHardwareInfo(name, bytes));
        }

        return gpus;
    }

    private static IReadOnlyList<RuntimeGpuHardwareInfo> ReadDxgiGpus()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Array.Empty<RuntimeGpuHardwareInfo>();
        }

        var factory = IntPtr.Zero;
        try
        {
            var factoryId = DxgiFactory1Id;
            if (CreateDXGIFactory1(ref factoryId, out factory) < 0 || factory == IntPtr.Zero)
            {
                return Array.Empty<RuntimeGpuHardwareInfo>();
            }

            var factoryVTable = Marshal.ReadIntPtr(factory);
            var enumAdaptersPointer = Marshal.ReadIntPtr(factoryVTable, IntPtr.Size * 12);
            var enumAdapters = Marshal.GetDelegateForFunctionPointer<EnumAdapters1Delegate>(enumAdaptersPointer);
            var gpus = new List<RuntimeGpuHardwareInfo>();
            for (var index = 0u; index < 16; index++)
            {
                var result = enumAdapters(factory, index, out var adapter);
                if (result == DxgiErrorNotFound)
                {
                    break;
                }

                if (result < 0 || adapter == IntPtr.Zero)
                {
                    continue;
                }

                try
                {
                    var adapterVTable = Marshal.ReadIntPtr(adapter);
                    var getDescPointer = Marshal.ReadIntPtr(adapterVTable, IntPtr.Size * 10);
                    var getDesc = Marshal.GetDelegateForFunctionPointer<GetDesc1Delegate>(getDescPointer);
                    if (getDesc(adapter, out var desc) < 0)
                    {
                        continue;
                    }

                    if ((desc.Flags & DxgiAdapterFlagSoftware) != 0 || string.IsNullOrWhiteSpace(desc.Description))
                    {
                        continue;
                    }

                    var dedicatedBytes = desc.DedicatedVideoMemory.ToUInt64();
                    gpus.Add(new RuntimeGpuHardwareInfo(
                        desc.Description.Trim(),
                        dedicatedBytes > 0 ? dedicatedBytes : null));
                }
                finally
                {
                    Marshal.Release(adapter);
                }
            }

            return gpus;
        }
        catch
        {
            return Array.Empty<RuntimeGpuHardwareInfo>();
        }
        finally
        {
            if (factory != IntPtr.Zero)
            {
                Marshal.Release(factory);
            }
        }
    }

    private static IReadOnlyList<RuntimeGpuHardwareInfo> ReadWindowsVideoControllers()
    {
        var output = RunProcess(
            "powershell.exe",
            "-NoProfile -ExecutionPolicy Bypass -Command \"Get-CimInstance Win32_VideoController | Select-Object Name,AdapterRAM | ConvertTo-Json -Compress\"",
            TimeSpan.FromSeconds(4));
        if (string.IsNullOrWhiteSpace(output))
        {
            return Array.Empty<RuntimeGpuHardwareInfo>();
        }

        try
        {
            using var document = JsonDocument.Parse(output);
            return document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().Select(ReadWindowsVideoController).Where(gpu => gpu is not null).Cast<RuntimeGpuHardwareInfo>().ToList()
                : ReadWindowsVideoController(document.RootElement) is { } gpu
                    ? [gpu]
                    : Array.Empty<RuntimeGpuHardwareInfo>();
        }
        catch
        {
            return Array.Empty<RuntimeGpuHardwareInfo>();
        }
    }

    private static RuntimeGpuHardwareInfo? ReadWindowsVideoController(JsonElement item)
    {
        var name = item.TryGetProperty("Name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
            ? nameElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        ulong? adapterBytes = null;
        if (item.TryGetProperty("AdapterRAM", out var memoryElement)
            && memoryElement.ValueKind == JsonValueKind.Number
            && memoryElement.TryGetUInt64(out var rawBytes)
            && rawBytes > 0)
        {
            adapterBytes = rawBytes;
        }

        return new RuntimeGpuHardwareInfo(name, adapterBytes);
    }

    private static MemorySnapshot? ReadMemory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var memoryStatus = new MemoryStatusEx();
        memoryStatus.dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>();
        return GlobalMemoryStatusEx(ref memoryStatus)
            ? new MemorySnapshot(memoryStatus.ullTotalPhys, memoryStatus.ullAvailPhys)
            : null;
    }

    private static string? RunProcess(string fileName, string arguments, TimeSpan timeout)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                }
            };

            process.Start();
            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Hardware detection is best-effort only.
                }

                return null;
            }

            return process.ExitCode == 0 ? process.StandardOutput.ReadToEnd() : null;
        }
        catch
        {
            return null;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

    private static readonly Guid DxgiFactory1Id = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);
    private const uint DxgiAdapterFlagSoftware = 2;

    private delegate int EnumAdapters1Delegate(IntPtr factory, uint adapterIndex, out IntPtr adapter);

    private delegate int GetDesc1Delegate(IntPtr adapter, out DxgiAdapterDesc1 desc);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DxgiAdapterDesc1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public UIntPtr DedicatedVideoMemory;
        public UIntPtr DedicatedSystemMemory;
        public UIntPtr SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
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

    private readonly record struct MemorySnapshot(ulong TotalBytes, ulong AvailableBytes);
}

