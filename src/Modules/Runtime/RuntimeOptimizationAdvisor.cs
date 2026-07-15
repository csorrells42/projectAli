using System.Globalization;
using System.Text;

namespace Ali.Modules.Runtime;

public sealed record RuntimeMachineResourceSnapshot(
    double? CpuPercent,
    double? RamPercent,
    double? GpuPercent,
    double? VramPercent,
    ulong? TotalRamBytes = null,
    ulong? AvailableRamBytes = null,
    double? VramUsageBytes = null,
    double? VramLimitBytes = null,
    string? CpuName = null,
    int? LogicalProcessorCount = null,
    IReadOnlyList<RuntimeGpuHardwareInfo>? Gpus = null);

public sealed record RuntimeGpuHardwareInfo(string Name, ulong? DedicatedMemoryBytes);

public sealed record RuntimeMemoryEstimate(
    double ModelParametersBillion,
    double EstimatedModelMemoryGb,
    double EstimatedContextMemoryGb,
    double EstimatedTotalMemoryGb,
    string FitSummary);

public sealed record RuntimeOptimizationStrategy(
    string Name,
    string Goal,
    int ContextTokens,
    int OutputTokenLimit,
    string TemperatureText,
    string TopPText,
    bool StreamingEnabled,
    bool VisionEnabled,
    RuntimeMemoryEstimate Estimate,
    string Consequence);

public sealed record RuntimeOptimizationReport(
    OpenAiCompatibleRuntimeOptions CurrentOptions,
    RuntimeMachineResourceSnapshot Machine,
    RuntimeMemoryEstimate CurrentEstimate,
    IReadOnlyList<RuntimeOptimizationStrategy> Strategies)
{
    public string ToDisplayText()
    {
        var builder = new StringBuilder();
        builder.AppendLine("Ali Runtime Optimization Estimate");
        builder.AppendLine();
        builder.AppendLine("This is a local planning estimate. It does not benchmark the model, change settings, or install anything.");
        builder.AppendLine("Use it to pick safer Runtime tab values before pressing Save, Check, and Activate.");
        builder.AppendLine();
        builder.AppendLine("Selected runtime");
        builder.AppendLine($"- Model: {CurrentOptions.Model}");
        builder.AppendLine($"- Quantization: {CurrentOptions.Quantization}");
        builder.AppendLine($"- Context: {CurrentOptions.ContextTokens.ToString("N0", CultureInfo.InvariantCulture)} tokens");
        builder.AppendLine($"- Output: {CurrentOptions.OutputTokenLimit.ToString("N0", CultureInfo.InvariantCulture)} tokens");
        builder.AppendLine($"- Temperature: {CurrentOptions.Temperature.ToString("0.###", CultureInfo.InvariantCulture)}");
        builder.AppendLine($"- Top-p: {CurrentOptions.TopP?.ToString("0.###", CultureInfo.InvariantCulture) ?? "model default"}");
        builder.AppendLine($"- Streaming: {(CurrentOptions.StreamingEnabled ? "on" : "off")}");
        builder.AppendLine($"- Vision: {(CurrentOptions.SupportsVision ? "on" : "off")}");
        builder.AppendLine($"- Suggested role: {RuntimeOptimizationAdvisor.DescribeModelRole(CurrentOptions, Machine)}");
        builder.AppendLine();
        builder.AppendLine("Current machine snapshot");
        builder.AppendLine($"- CPU: {FormatCpu(Machine.CpuName, Machine.LogicalProcessorCount)}");
        builder.AppendLine($"- GPU: {FormatGpus(Machine.Gpus)}");
        builder.AppendLine($"- CPU load now: {FormatPercent(Machine.CpuPercent)}");
        builder.AppendLine($"- RAM load now: {FormatPercent(Machine.RamPercent)}{FormatMemoryPair(Machine.AvailableRamBytes, Machine.TotalRamBytes, " available")}");
        builder.AppendLine($"- GPU load now: {FormatPercent(Machine.GpuPercent)}");
        builder.AppendLine($"- VRAM load now: {FormatPercent(Machine.VramPercent)}{FormatVramPair(Machine.VramUsageBytes, Machine.VramLimitBytes)}");
        builder.AppendLine();
        builder.AppendLine("Current estimate");
        AppendEstimate(builder, CurrentEstimate);
        builder.AppendLine();
        builder.AppendLine("Recommended paths");

        foreach (var strategy in Strategies)
        {
            builder.AppendLine();
            builder.AppendLine($"{strategy.Name}");
            builder.AppendLine($"Goal: {strategy.Goal}");
            builder.AppendLine($"Settings: context {strategy.ContextTokens:N0}, output {strategy.OutputTokenLimit:N0}, temperature {strategy.TemperatureText}, top-p {strategy.TopPText}, streaming {(strategy.StreamingEnabled ? "on" : "off")}, vision {(strategy.VisionEnabled ? "on" : "off")}");
            builder.AppendLine($"Estimated memory: {strategy.Estimate.EstimatedTotalMemoryGb:0.0} GB total ({strategy.Estimate.EstimatedModelMemoryGb:0.0} GB model + {strategy.Estimate.EstimatedContextMemoryGb:0.0} GB context/runtime)");
            builder.AppendLine($"Consequence: {strategy.Consequence}");
            builder.AppendLine($"Fit: {strategy.Estimate.FitSummary}");
        }

        return builder.ToString().TrimEnd();
    }

    private static void AppendEstimate(StringBuilder builder, RuntimeMemoryEstimate estimate)
    {
        builder.AppendLine($"- Estimated parameters: {estimate.ModelParametersBillion:0.#}B");
        builder.AppendLine($"- Estimated model memory: {estimate.EstimatedModelMemoryGb:0.0} GB");
        builder.AppendLine($"- Estimated context/runtime memory: {estimate.EstimatedContextMemoryGb:0.0} GB");
        builder.AppendLine($"- Estimated total memory pressure: {estimate.EstimatedTotalMemoryGb:0.0} GB");
        builder.AppendLine($"- Fit: {estimate.FitSummary}");
    }

    private static string FormatPercent(double? percent) =>
        percent is null ? "unknown" : $"{Math.Clamp(percent.Value, 0d, 100d):0}%";

    private static string FormatCpu(string? cpuName, int? logicalProcessors)
    {
        var name = string.IsNullOrWhiteSpace(cpuName) ? "unknown" : cpuName.Trim();
        return logicalProcessors is > 0 ? $"{name} ({logicalProcessors.Value} logical processors)" : name;
    }

    private static string FormatGpus(IReadOnlyList<RuntimeGpuHardwareInfo>? gpus)
    {
        if (gpus is null || gpus.Count == 0)
        {
            return "unknown";
        }

        return string.Join(
            "; ",
            gpus.Select(gpu => gpu.DedicatedMemoryBytes is > 0
                ? $"{gpu.Name} ({gpu.DedicatedMemoryBytes.Value / RuntimeOptimizationAdvisor.Gib:0.#} GB dedicated)"
                : gpu.Name));
    }

    private static string FormatMemoryPair(ulong? availableBytes, ulong? totalBytes, string suffix)
    {
        if (availableBytes is null || totalBytes is null || totalBytes == 0)
        {
            return string.Empty;
        }

        return $" ({BytesToGb(availableBytes.Value):0.0} GB of {BytesToGb(totalBytes.Value):0.0} GB{suffix})";
    }

    private static string FormatVramPair(double? usageBytes, double? limitBytes)
    {
        if (usageBytes is null || limitBytes is null || limitBytes <= 0)
        {
            return string.Empty;
        }

        return $" ({usageBytes.Value / RuntimeOptimizationAdvisor.Gib:0.0} GB of {limitBytes.Value / RuntimeOptimizationAdvisor.Gib:0.0} GB used)";
    }

    private static double BytesToGb(ulong bytes) => bytes / RuntimeOptimizationAdvisor.Gib;
}

public static class RuntimeOptimizationAdvisor
{
    internal const double Gib = 1024d * 1024d * 1024d;

    public static RuntimeOptimizationReport BuildReport(
        OpenAiCompatibleRuntimeOptions options,
        RuntimeMachineResourceSnapshot machine)
    {
        var current = Estimate(options, options.ContextTokens, options.OutputTokenLimit, options.SupportsVision, machine);
        var strategies = new[]
        {
            BuildLowStrategy(options, machine),
            BuildMediumStrategy(options, machine),
            BuildAggressiveStrategy(options, machine)
        };

        return new RuntimeOptimizationReport(options, machine, current, strategies);
    }

    public static string DescribeModelRole(
        OpenAiCompatibleRuntimeOptions options,
        RuntimeMachineResourceSnapshot machine)
    {
        var model = options.Model.ToLowerInvariant();
        var vramGb = machine.VramLimitBytes is > 0
            ? machine.VramLimitBytes.Value / Gib
            : machine.Gpus?
                .Where(gpu => gpu.DedicatedMemoryBytes is > 0)
                .Select(gpu => gpu.DedicatedMemoryBytes!.Value / Gib)
                .DefaultIfEmpty()
                .Max();

        if (model.Contains("deepseek-coder", StringComparison.Ordinal))
        {
            return "Recommended technical model for this PC. Best for technical questions, diagnostics, troubleshooting, and software architecture questions.";
        }

        if (model.Contains("gemma4", StringComparison.Ordinal) && model.Contains("12b", StringComparison.Ordinal))
        {
            return "Optional general assistant model. Smooth for chat, source-backed answers, planning, and maintenance, but DeepSeek remains the better technical default here.";
        }

        if (model.Contains("gemma4", StringComparison.Ordinal) && model.Contains("26b", StringComparison.Ordinal))
        {
            return vramGb is not null && vramGb.Value <= 18
                ? "Heavy experiment. On this VRAM class, expect possible spillover and desktop choppiness; keep it manual, not default."
                : "Heavy experiment. Use when quality matters more than responsiveness, and keep other GPU apps light.";
        }

        if (model.Contains("qwen3-vl", StringComparison.Ordinal) || model.Contains("vision", StringComparison.Ordinal))
        {
            return "Vision-capable local model. Use when image understanding matters; keep context modest for responsiveness.";
        }

        if (model.Contains("qwen", StringComparison.Ordinal))
        {
            return "General local model. Usually a reasonable fallback when Gemma or DeepSeek are not installed.";
        }

        return "General local model. Run a health check and compare responsiveness before making it the default.";
    }

    private static RuntimeOptimizationStrategy BuildLowStrategy(
        OpenAiCompatibleRuntimeOptions options,
        RuntimeMachineResourceSnapshot machine)
    {
        var context = options.Model.Contains("1.7b", StringComparison.OrdinalIgnoreCase)
            ? 1024
            : 2048;
        var output = 128;
        var vision = options.SupportsVision && LooksLikeVisionModel(options.Model);
        var estimate = Estimate(options, context, output, vision, machine);
        return new RuntimeOptimizationStrategy(
            "Low - steady workstation mode",
            "Prioritize stability, fast startup, low heat, and enough headroom for Visual Studio, browser tabs, voice, and screen sharing.",
            context,
            output,
            "0",
            "model default",
            true,
            vision,
            estimate,
            "Best for technical help, troubleshooting, and maintenance work. Shorter memory window and shorter answers, but least likely to bog down the PC.");
    }

    private static RuntimeOptimizationStrategy BuildMediumStrategy(
        OpenAiCompatibleRuntimeOptions options,
        RuntimeMachineResourceSnapshot machine)
    {
        var parameters = InferParameterCountBillion(options);
        var context = parameters <= 8 ? 4096 : 2048;
        var output = 256;
        var vision = options.SupportsVision && LooksLikeVisionModel(options.Model);
        var estimate = Estimate(options, context, output, vision, machine);
        return new RuntimeOptimizationStrategy(
            "Medium - balanced builder mode",
            "Balance better planning context with enough room for the desktop to stay responsive.",
            context,
            output,
            "0.2",
            "model default",
            true,
            vision,
            estimate,
            "Good default for build planning, code review, and source-backed answers. If VRAM is tight, this may still spill some work to CPU/RAM.");
    }

    private static RuntimeOptimizationStrategy BuildAggressiveStrategy(
        OpenAiCompatibleRuntimeOptions options,
        RuntimeMachineResourceSnapshot machine)
    {
        var parameters = InferParameterCountBillion(options);
        var context = parameters <= 14 ? 8192 : 4096;
        var output = Math.Clamp(Math.Max(512, options.OutputTokenLimit), 512, 1024);
        var vision = options.SupportsVision;
        var estimate = Estimate(options, context, output, vision, machine);
        return new RuntimeOptimizationStrategy(
            "Aggressive - maximum local quality",
            "Push context and answer length when the machine has idle VRAM/RAM and the user accepts slower responses.",
            context,
            output,
            "0.3",
            "0.9",
            true,
            vision,
            estimate,
            "Best for deep architecture review and long debugging sessions. Expect higher VRAM/RAM pressure, more fan noise, and slower recovery if the model spills out of VRAM.");
    }

    private static RuntimeMemoryEstimate Estimate(
        OpenAiCompatibleRuntimeOptions options,
        int contextTokens,
        int outputTokenLimit,
        bool visionEnabled,
        RuntimeMachineResourceSnapshot machine)
    {
        var parameters = InferParameterCountBillion(options);
        var bits = InferBitsPerWeight(options.Quantization);
        var modelMemory = Math.Max(0.3, parameters * bits / 8d * 1.18d);
        var kvMemory = Math.Max(0.15, parameters * contextTokens * 0.000035d);
        var outputReserve = Math.Clamp(outputTokenLimit / 2048d, 0.05d, 0.5d);
        var runtimeOverhead = 0.8d;
        var visionOverhead = visionEnabled ? 0.9d : 0d;
        var total = modelMemory + kvMemory + outputReserve + runtimeOverhead + visionOverhead;

        return new RuntimeMemoryEstimate(
            parameters,
            modelMemory,
            kvMemory + outputReserve + runtimeOverhead + visionOverhead,
            total,
            BuildFitSummary(total, machine));
    }

    private static string BuildFitSummary(double estimatedGb, RuntimeMachineResourceSnapshot machine)
    {
        var hardwareVramGb = machine.Gpus?
            .Where(gpu => gpu.DedicatedMemoryBytes is > 0)
            .Select(gpu => gpu.DedicatedMemoryBytes!.Value / Gib)
            .DefaultIfEmpty()
            .Max();
        var vramGb = machine.VramLimitBytes is > 0
            ? machine.VramLimitBytes.Value / Gib
            : hardwareVramGb is > 0
                ? hardwareVramGb
                : null;
        var ramGb = machine.TotalRamBytes is > 0 ? machine.TotalRamBytes.Value / Gib : (double?)null;

        if (vramGb is not null)
        {
            if (estimatedGb <= vramGb.Value * 0.70)
            {
                return $"Likely comfortable for GPU offload on a {vramGb.Value:0.#} GB VRAM budget.";
            }

            if (estimatedGb <= vramGb.Value * 0.92)
            {
                return $"Likely possible but tight on a {vramGb.Value:0.#} GB VRAM budget; keep other GPU apps light.";
            }

            if (ramGb is not null && estimatedGb <= ramGb.Value * 0.55)
            {
                return $"Likely to spill beyond VRAM and lean on system RAM/CPU; expect slower responses.";
            }

            return "Too aggressive for the visible VRAM/RAM budget; reduce model size, quantization, or context.";
        }

        if (ramGb is not null)
        {
            return estimatedGb <= ramGb.Value * 0.40
                ? $"RAM budget looks reasonable against {ramGb.Value:0.#} GB system RAM, but VRAM limit is unknown."
                : $"May be heavy against {ramGb.Value:0.#} GB system RAM; VRAM limit is unknown.";
        }

        return "Machine VRAM/RAM totals are unavailable; compare the estimate against Task Manager or GPU vendor tools.";
    }

    private static double InferParameterCountBillion(OpenAiCompatibleRuntimeOptions options)
    {
        foreach (var text in new[] { options.Size, options.Model, options.DisplayName })
        {
            if (TryReadParameterSize(text, out var value))
            {
                return value;
            }
        }

        return 8;
    }

    private static bool TryReadParameterSize(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var lower = text.ToLowerInvariant();
        for (var index = 0; index < lower.Length; index++)
        {
            if (!char.IsDigit(lower[index]))
            {
                continue;
            }

            var start = index;
            while (index < lower.Length && (char.IsDigit(lower[index]) || lower[index] == '.'))
            {
                index++;
            }

            var numberText = lower[start..index];
            var suffix = index < lower.Length ? lower[index] : '\0';
            if (suffix == 'b'
                && double.TryParse(numberText, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                && value > 0)
            {
                return true;
            }
        }

        return false;
    }

    private static double InferBitsPerWeight(string? quantization)
    {
        if (string.IsNullOrWhiteSpace(quantization)
            || quantization.Contains("default", StringComparison.OrdinalIgnoreCase))
        {
            return 4.8d;
        }

        var lower = quantization.ToLowerInvariant();
        if (lower.Contains("f16", StringComparison.Ordinal) || lower.Contains("fp16", StringComparison.Ordinal))
        {
            return 16d;
        }

        if (lower.Contains("q8", StringComparison.Ordinal))
        {
            return 8.2d;
        }

        if (lower.Contains("q6", StringComparison.Ordinal))
        {
            return 6.4d;
        }

        if (lower.Contains("q5", StringComparison.Ordinal))
        {
            return 5.4d;
        }

        if (lower.Contains("q4", StringComparison.Ordinal))
        {
            return 4.6d;
        }

        if (lower.Contains("q3", StringComparison.Ordinal))
        {
            return 3.6d;
        }

        if (lower.Contains("q2", StringComparison.Ordinal))
        {
            return 2.8d;
        }

        return 4.8d;
    }

    private static bool LooksLikeVisionModel(string model) =>
        model.Contains("vl", StringComparison.OrdinalIgnoreCase)
        || model.Contains("vision", StringComparison.OrdinalIgnoreCase)
        || model.Contains("visual", StringComparison.OrdinalIgnoreCase);
}

