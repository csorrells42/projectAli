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
        Assert.Contains("newest user message as authoritative", instructions, StringComparison.Ordinal);
        Assert.Contains("Never carry forward or retry an earlier failed action", instructions, StringComparison.Ordinal);
        Assert.Contains("use file_access_move", instructions, StringComparison.Ordinal);
    }
}
