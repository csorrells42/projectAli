using Ali.Modules.Coordinator;

namespace Ali.Framework.Tests;

public sealed class AliToolCatalogTests
{
    [Fact]
    public void Instructions_MakeInterpretationTypoTolerantWithoutChangingTheOriginalMessage()
    {
        var instructions = AliToolCatalog.BuildInstructions("Ali");

        Assert.Contains("spelling mistakes", instructions, StringComparison.Ordinal);
        Assert.Contains("Infer the intended words from the whole sentence", instructions, StringComparison.Ordinal);
        Assert.Contains("Preserve the user's original message", instructions, StringComparison.Ordinal);
        Assert.Contains("ask one short clarifying question instead of guessing", instructions, StringComparison.Ordinal);
        Assert.Contains("predictions, forecasts, opinions", instructions, StringComparison.Ordinal);
        Assert.Contains("Never give a generic refusal", instructions, StringComparison.Ordinal);
        Assert.Contains("CanRetry is false", instructions, StringComparison.Ordinal);
        Assert.Contains("CanRetry is true", instructions, StringComparison.Ordinal);
        Assert.Contains("one refined search", instructions, StringComparison.Ordinal);
        Assert.Contains("Never use it for greetings", instructions, StringComparison.Ordinal);
        Assert.Contains("newest user message as authoritative", instructions, StringComparison.Ordinal);
        Assert.Contains("Never carry forward or retry an earlier failed action", instructions, StringComparison.Ordinal);
        Assert.Contains("use file_access_move", instructions, StringComparison.Ordinal);
        Assert.Contains("use them instead of claiming incapability", instructions, StringComparison.Ordinal);
        Assert.Contains("call dotnet_create_project", instructions, StringComparison.Ordinal);
        Assert.Contains("name collision, not missing permission", instructions, StringComparison.Ordinal);
        Assert.Contains("choose a new unique sibling name", instructions, StringComparison.Ordinal);
        Assert.Contains("roslyn_analyze_project", instructions, StringComparison.Ordinal);
        Assert.Contains("untouched template is never", instructions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("explicitly asks you not to use tools", instructions, StringComparison.Ordinal);
        Assert.Contains("arduino_create_and_compile", instructions, StringComparison.Ordinal);
        Assert.Contains("Never invent a skill name or script name", instructions, StringComparison.Ordinal);
    }
}
