using System.Text.RegularExpressions;
using Ali.Modules.Coding.Infrastructure;
using Ali.Modules.Coding.Languages;
using Ali.Modules.Coding.Toolchains;

namespace Ali.Modules.Coding.Embedded.RaspberryPi;

public sealed record AliRaspberryPiLibrary(string Name, string Language, string Purpose, string InstallHint);

public sealed record AliRaspberryPiOperationResult(
    bool Success,
    string Operation,
    string Summary,
    int? ExitCode,
    long DurationMilliseconds,
    string Output,
    IReadOnlyList<string> Artifacts);

internal sealed class AliRaspberryPiTools(AliLanguageProjectResolver resolver)
{
    private static readonly Regex SafeHost = new("^[A-Za-z0-9](?:[A-Za-z0-9.-]{0,251}[A-Za-z0-9])?$", RegexOptions.CultureInvariant);
    private static readonly Regex SafeUser = new("^[A-Za-z_][A-Za-z0-9_-]{0,31}$", RegexOptions.CultureInvariant);
    private static readonly Regex SafeQuery = new("^[A-Za-z0-9_.+:-]{1,80}$", RegexOptions.CultureInvariant);
    private static readonly Regex SafeRemotePath = new("^/[A-Za-z0-9_./-]{1,240}$", RegexOptions.CultureInvariant);

    public IReadOnlyList<AliRaspberryPiLibrary> GetLibraryCatalog() =>
    [
        new("GPIO Zero", "Python", "Friendly buttons, LEDs, motors, sensors, and recipes", "sudo apt install python3-gpiozero"),
        new("lgpio / rpi-lgpio", "Python/C", "Modern GPIO access compatible with current Raspberry Pi kernels", "sudo apt install python3-lgpio"),
        new("libgpiod", "C/C++", "Linux GPIO character-device API and command-line diagnostics", "sudo apt install gpiod libgpiod-dev"),
        new("spidev", "Python", "SPI peripherals", "sudo apt install python3-spidev"),
        new("smbus2", "Python", "I2C/SMBus peripherals", "python3 -m pip install smbus2"),
        new("pigpio", "Python/C", "Timed GPIO waveforms and remote GPIO daemon", "sudo apt install pigpio python3-pigpio"),
        new("Picamera2", "Python", "Modern libcamera-based camera control", "sudo apt install python3-picamera2"),
        new("rpicam-apps / libcamera", "C/C++", "Official Raspberry Pi camera applications and APIs", "sudo apt install rpicam-apps libcamera-dev"),
        new("Pico SDK", "C/C++", "RP2040/RP2350 firmware, CMake, PIO, and hardware APIs", "Use the official pico-sdk with CMake and ARM GCC"),
        new("Arduino Mbed RP2040", "Arduino C++", "Arduino library ecosystem for Pico-class boards", "arduino-cli core install arduino:mbed_rp2040")
    ];

    public Task<AliRaspberryPiOperationResult> ProbeAsync(string host, string user, int port, CancellationToken cancellationToken) =>
        RunSshAsync(host, user, port,
            "printf 'OS='; . /etc/os-release 2>/dev/null; printf '%s\\n' \"${PRETTY_NAME:-unknown}\"; uname -a; python3 --version 2>&1; gcc --version 2>&1 | head -n 1; cmake --version 2>&1 | head -n 1; id; command -v gpioinfo || true; command -v rpicam-hello || true",
            "probe", "Raspberry Pi/Linux development capabilities were probed through SSH.", cancellationToken);

    public Task<AliRaspberryPiOperationResult> InspectLibrariesAsync(string host, string user, int port, CancellationToken cancellationToken) =>
        RunSshAsync(host, user, port,
            "for p in python3-gpiozero python3-lgpio gpiod libgpiod-dev python3-spidev python3-picamera2 rpicam-apps libcamera-dev; do dpkg-query -W -f='${Package} ${Version}\\n' \"$p\" 2>/dev/null || true; done; python3 - <<'PY'\nimport importlib.util\nfor n in ('gpiozero','lgpio','spidev','smbus2','pigpio','picamera2'):\n print(n, 'available' if importlib.util.find_spec(n) else 'missing')\nPY",
            "inspect_libraries", "Raspberry Pi GPIO, bus, and camera libraries were inspected without changing the device.", cancellationToken);

    public Task<AliRaspberryPiOperationResult> SearchPackagesAsync(string host, string user, int port, string query, CancellationToken cancellationToken)
    {
        ValidateConnection(host, user, port);
        if (!SafeQuery.IsMatch(query)) throw new ArgumentException("Use a simple package or library search term.", nameof(query));
        return RunSshAsync(host, user, port, $"apt-cache search --names-only '^{query}' | head -n 100", "search_packages",
            "The Raspberry Pi OS package catalog returned matching development packages.", cancellationToken);
    }

    public async Task<AliRaspberryPiOperationResult> DeployAsync(
        string targetPath, string host, string user, int port, string remotePath, CancellationToken cancellationToken)
    {
        ValidateConnection(host, user, port);
        if (!SafeRemotePath.IsMatch(remotePath) || remotePath.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("Use an absolute Raspberry Pi destination without traversal or shell syntax.", nameof(remotePath));
        var project = resolver.Resolve(targetPath);
        if (!File.Exists(AliDeveloperToolPaths.Scp))
            return Missing("deploy", "Windows OpenSSH scp is unavailable.");
        var copy = await AliBoundedProcessRunner.RunAsync(
            AliDeveloperToolPaths.Scp,
            project.ProjectDirectory,
            ["-P", port.ToString(), "-r", project.ProjectDirectory, $"{user}@{host}:{remotePath}"],
            TimeSpan.FromMinutes(20), cancellationToken).ConfigureAwait(false);
        if (!copy.Success)
            return From(copy, "deploy", "Project transfer to the Raspberry Pi failed; no remote build is claimed.");
        var folder = Path.GetFileName(Path.TrimEndingDirectorySeparator(project.ProjectDirectory));
        var remoteProject = remotePath.TrimEnd('/') + "/" + folder;
        var buildCommand = project.Language switch
        {
            AliProgrammingLanguage.Python => $"cd '{remoteProject}' && python3 -m compileall -q .",
            AliProgrammingLanguage.Cpp => $"cd '{remoteProject}' && if [ -f CMakeLists.txt ]; then cmake -S . -B .ali-build -DCMAKE_BUILD_TYPE=Release && cmake --build .ali-build --parallel; else printf 'Transferred C/C++ source; no CMakeLists.txt found.\\n'; fi",
            _ => $"cd '{remoteProject}' && printf 'Transferred project; use its registered build provider on the Pi.\\n'"
        };
        var build = await RunSshRawAsync(host, user, port, buildCommand, cancellationToken).ConfigureAwait(false);
        return new(build.Success, "deploy", build.Success ? "Project was transferred and its safe Raspberry Pi build check completed." : "Project transferred, but the Raspberry Pi build check failed.", build.ExitCode,
            copy.DurationMilliseconds + build.DurationMilliseconds, copy.Output + Environment.NewLine + build.Output, []);
    }

    private static async Task<AliRaspberryPiOperationResult> RunSshAsync(
        string host, string user, int port, string remoteCommand, string operation, string successSummary, CancellationToken cancellationToken)
    {
        ValidateConnection(host, user, port);
        if (!File.Exists(AliDeveloperToolPaths.Ssh)) return Missing(operation, "Windows OpenSSH is unavailable.");
        var result = await RunSshRawAsync(host, user, port, remoteCommand, cancellationToken).ConfigureAwait(false);
        return From(result, operation, result.Success ? successSummary : "The Raspberry Pi SSH operation failed; no remote capability is claimed.");
    }

    private static Task<BoundedProcessResult> RunSshRawAsync(string host, string user, int port, string command, CancellationToken cancellationToken) =>
        AliBoundedProcessRunner.RunAsync(AliDeveloperToolPaths.Ssh, Environment.CurrentDirectory,
            ["-p", port.ToString(), "-o", "BatchMode=yes", "-o", "ConnectTimeout=10", $"{user}@{host}", command],
            TimeSpan.FromMinutes(10), cancellationToken);

    private static void ValidateConnection(string host, string user, int port)
    {
        if (!SafeHost.IsMatch(host)) throw new ArgumentException("Use a DNS name or IP-like host without shell syntax.", nameof(host));
        if (!SafeUser.IsMatch(user)) throw new ArgumentException("Use a valid Linux user name.", nameof(user));
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
    }

    private static AliRaspberryPiOperationResult From(BoundedProcessResult result, string operation, string summary) =>
        new(result.Success, operation, summary, result.ExitCode, result.DurationMilliseconds, result.Output, []);

    private static AliRaspberryPiOperationResult Missing(string operation, string summary) =>
        new(false, operation, summary, null, 0, string.Empty, []);
}
