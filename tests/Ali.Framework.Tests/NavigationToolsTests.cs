using Ali.Modules.Coordinator;
using Ali.Modules.Internet;

namespace Ali.Framework.Tests;

public sealed class NavigationToolsTests
{
    [Fact]
    public void CreateGoogleMapsDirectionsLink_PreservesOrderedQueriesWithoutInventingRouteFacts()
    {
        var turn = new CoordinatorTurnContext(
            "conversation",
            "user",
            "assistant",
            "Create a round-trip route.",
            _ => { });
        var tools = new AliNavigationTools(() => turn);

        var result = tools.CreateGoogleMapsDirectionsLink(
            "3075 SE St Lucie Blvd, Stuart, FL",
            "3075 SE St Lucie Blvd, Stuart, FL",
            ["Publix near Stuart, FL", "Waffle House near Stuart, FL", "gym near Stuart, FL"],
            "driving");

        Assert.True(result.Success);
        Assert.True(turn.UsedEvidenceTool);
        Assert.True(turn.UsedNavigationTool);
        Assert.StartsWith("https://www.google.com/maps/dir/?api=1&", result.Url, StringComparison.Ordinal);
        Assert.Contains("origin=3075%20SE%20St%20Lucie%20Blvd%2C%20Stuart%2C%20FL", result.Url, StringComparison.Ordinal);
        Assert.Contains("destination=3075%20SE%20St%20Lucie%20Blvd%2C%20Stuart%2C%20FL", result.Url, StringComparison.Ordinal);
        Assert.Contains("waypoints=Publix%20near%20Stuart%2C%20FL%7CWaffle%20House%20near%20Stuart%2C%20FL%7Cgym%20near%20Stuart%2C%20FL", result.Url, StringComparison.Ordinal);
        Assert.Contains("Google Maps will resolve", result.Status, StringComparison.Ordinal);
        Assert.Contains("does not return turn-by-turn steps", result.EvidenceBoundary, StringComparison.Ordinal);
        Assert.DoesNotContain("miles", result.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateGoogleMapsDirectionsLink_RejectsUnsupportedOrUnboundedInputs()
    {
        var tools = new AliNavigationTools(static () => null);

        Assert.Throws<ArgumentException>(() => tools.CreateGoogleMapsDirectionsLink(
            "Home",
            "Home",
            ["one", "two", "three", "four"],
            "driving"));
        Assert.Throws<ArgumentException>(() => tools.CreateGoogleMapsDirectionsLink(
            "Home",
            "Office",
            [],
            "hovercraft"));
        Assert.Throws<ArgumentException>(() => tools.CreateGoogleMapsDirectionsLink(
            " ",
            "Office",
            [],
            "walking"));
    }
}
