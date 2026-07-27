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
    }
}
