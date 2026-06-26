using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Ali.Infrastructure.Runtime;
using Microsoft.Win32;

namespace Ali.App.Wpf.ViewModels;

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
