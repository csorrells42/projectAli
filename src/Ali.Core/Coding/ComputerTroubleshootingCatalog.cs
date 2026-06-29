namespace Ali.Core.Coding;

public static class ComputerTroubleshootingCatalog
{
    public static string BuildCommandIndex()
    {
        var lines = new List<string>
        {
            "Ali computer troubleshooting command index:",
            "No files, settings, drivers, services, devices, or apps were changed.",
            "20 lunch-sprint entries:"
        };

        for (var index = 0; index < Commands.Count; index++)
        {
            lines.Add($"{index + 1}. {Commands[index]}");
        }

        lines.Add("Rules:");
        lines.Add("- These are read-only planning commands.");
        lines.Add("- Any fix that changes files, installs software, changes drivers, edits services/startup, clears browser data, changes network settings, or stops processes needs a narrower confirmed command.");
        lines.Add("- If Ali cannot identify the cause from evidence, she should list options and ask for the next approved diagnostic step.");
        return string.Join(Environment.NewLine, lines);
    }

    public static IReadOnlyList<string> Commands { get; } =
    [
        "show computer troubleshooting commands",
        "plan slow computer troubleshooting",
        "plan network troubleshooting",
        "plan wifi troubleshooting",
        "plan printer troubleshooting",
        "plan audio troubleshooting",
        "plan microphone troubleshooting",
        "plan camera troubleshooting",
        "plan bluetooth troubleshooting",
        "plan usb device troubleshooting",
        "plan display troubleshooting",
        "plan windows update troubleshooting",
        "plan app crash troubleshooting",
        "plan startup cleanup",
        "plan browser troubleshooting",
        "plan onedrive sync troubleshooting",
        "plan backup strategy",
        "plan driver troubleshooting",
        "plan suspicious activity check",
        "plan remote support handoff"
    ];

    public static IEnumerable<string> BuildScenarioChecklist(string scenario)
    {
        return scenario.ToLowerInvariant() switch
        {
            "slow computer" => [
                "- Check Task Manager for CPU, memory, disk, GPU, startup impact, and recently installed apps.",
                "- Compare performance after a reboot before clearing caches or disabling startup entries.",
                "- Look for update/install/indexing/model-download work that may be temporary.",
                "- Use process evidence before stopping anything."
            ],
            "network" => [
                "- Identify whether the problem is all sites, one site, one app, wired, wireless, or VPN-only.",
                "- Check adapter state, IP address, gateway, DNS, and `Test-NetConnection` results.",
                "- Compare another device on the same network before changing router or Windows settings.",
                "- Do not reset adapters, DNS, firewall, or VPN settings without approval."
            ],
            "wi-fi" => [
                "- Check signal strength, correct SSID, airplane mode, saved network, and whether wired works.",
                "- Compare 2.4 GHz vs 5/6 GHz behavior if the router exposes separate names.",
                "- Review driver and power-management clues, but do not update drivers without approval.",
                "- Avoid forgetting networks or resetting adapters until evidence supports it."
            ],
            "printer" => [
                "- Check power, cable/Wi-Fi, paper, ink/toner, queue state, default printer, and exact error.",
                "- Print a test page from Windows before troubleshooting one app.",
                "- Inspect the queue and spooler status read-only first.",
                "- Driver reinstall, queue purge, and spooler restart need approval."
            ],
            "audio" => [
                "- Check selected output/input device, app device selection, volume mixer, mute buttons, and exclusive mode.",
                "- Test Windows Sound Recorder or Settings before troubleshooting the advanced app.",
                "- For interfaces, confirm USB connection, driver/control app, sample rate, and direct-monitor state.",
                "- Driver/control-app installs and default-device changes need approval."
            ],
            "microphone" => [
                "- Check Windows input device, app input device, privacy permission, gain, mute, cable, and interface LEDs.",
                "- For XLR mics, verify the interface input, gain, phantom-power needs for inline preamps, and clipping.",
                "- Record a short local sample before changing drivers.",
                "- Do not change driver, exclusive mode, or app permissions without approval."
            ],
            "camera" => [
                "- Check Windows camera privacy, app permission, selected camera, USB connection, and whether another app is using it.",
                "- Test the Camera app before troubleshooting conferencing software.",
                "- Inspect Device Manager status if the camera does not appear.",
                "- Driver reinstall and privacy-setting changes need approval."
            ],
            "bluetooth" => [
                "- Check Bluetooth toggle, battery, pairing mode, distance, interference, and whether the device is paired elsewhere.",
                "- Remove/re-pair only after confirming the device is discoverable and the owner approves.",
                "- Check vendor utility only after built-in pairing state is understood.",
                "- Driver/service restarts need approval."
            ],
            "usb device" => [
                "- Try a known-good cable and port, avoid hubs for first diagnosis, and check whether Windows chimes or Device Manager changes.",
                "- Identify whether the device needs data-capable USB, power, drivers, or vendor firmware.",
                "- Check Event Viewer and Device Manager status before reinstalling drivers.",
                "- Firmware and driver changes need approval."
            ],
            "display" => [
                "- Check cable, input source, Windows display mode, refresh rate, scaling, GPU driver status, and whether safe mode works.",
                "- Compare another cable/port/monitor before changing drivers.",
                "- Note exact symptoms: no signal, flicker, wrong resolution, color, HDR, or sleep/wake.",
                "- Driver reinstall and refresh-rate changes need approval."
            ],
            "windows update" => [
                "- Capture exact update KB/error code, pending reboot state, free disk space, and recent failed update time.",
                "- Check Settings -> Windows Update history before running repair commands.",
                "- Prefer official Windows troubleshooter/repair guidance after evidence.",
                "- Component store repair, cache reset, and service changes need approval."
            ],
            "app crash" => [
                "- Record app name/version, crash time, exact error, recent update/plugin/file, and whether it crashes on launch or action.",
                "- Check Event Viewer Application errors around the crash time.",
                "- Try a safe sample/new file before blaming existing user data.",
                "- Repair/reinstall/plugin disable/reset settings need approval."
            ],
            "startup cleanup" => [
                "- List startup entries and services first; identify owner, path, publisher, and impact.",
                "- Disable only one approved item at a time and keep rollback notes.",
                "- Do not remove security, driver, sync, backup, audio, GPU, or model-runtime entries casually.",
                "- Prefer disable over delete."
            ],
            "browser" => [
                "- Identify browser, profile, extension, site, error, and whether another browser works.",
                "- Test private/incognito and extension-disabled mode before clearing data.",
                "- Check proxy/VPN/DNS only after site/app scope is clear.",
                "- Clearing cookies/cache/passwords, resetting profile, or removing extensions needs approval."
            ],
            "onedrive sync" => [
                "- Check signed-in account, sync status, paused state, storage quota, file path length, invalid characters, and conflict icons.",
                "- Identify whether the issue is one file/folder or all sync.",
                "- Preserve local-only files before unlink/reset operations.",
                "- Unlink, reset, delete local copies, or move cloud-backed folders only with approval."
            ],
            "backup" => [
                "- Identify what must be protected: documents, photos, projects, Ali data, installers, licenses, and settings.",
                "- Choose backup target, retention, encryption needs, and test-restore sample.",
                "- Prefer copy/verify reports before deleting originals.",
                "- Do not format drives, enable cloud sync, or change backup jobs without approval."
            ],
            "driver" => [
                "- Identify exact device model, current driver version/provider/date, and symptom.",
                "- Prefer vendor/Windows Update/source-backed guidance over random driver sites.",
                "- Create restore/rollback notes before changing drivers.",
                "- Install, rollback, remove, or firmware-update drivers only with approval."
            ],
            "suspicious activity" => [
                "- Collect process evidence, startup entries, installed apps, browser extensions, recent downloads, and event log clues.",
                "- Disconnect from sensitive accounts if active compromise is suspected, then compare options.",
                "- Prefer Microsoft Security/Defender scans and official tools.",
                "- Do not delete files or terminate unknown security processes without understanding owner/path."
            ],
            "remote support handoff" => [
                "- Summarize symptom, timeline, device/app versions, screenshots, recent changes, and steps already tried.",
                "- Gather logs and screenshots without exposing passwords, tokens, recovery keys, or private files.",
                "- Decide what the remote helper may view/control before starting a session.",
                "- Never share credentials or approve remote control without owner awareness."
            ],
            _ => [
                "- Define the symptom, scope, recent changes, exact error, and safest read-only evidence to gather.",
                "- Use a scenario-specific planner when possible.",
                "- Avoid generic resets until the cause is narrowed.",
                "- Keep rollback notes for every approved change."
            ]
        };
    }
}
