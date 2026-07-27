using System.Text.Json;
using Ali.Modules.Coordinator;

namespace Ali.Framework.Tests;

public sealed class AgentEvaluationCaseTests
{
    [Fact]
    public void RoutingEvaluationCases_ReferenceOnlyRegisteredTools()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Cases", "agent-routing-cases.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var registered = AliCapabilityCatalog.Tools
            .Select(tool => tool.Name)
            .ToHashSet(StringComparer.Ordinal);

        var cases = document.RootElement.EnumerateArray().ToArray();
        Assert.NotEmpty(cases);
        Assert.All(cases, testCase =>
        {
            Assert.False(string.IsNullOrWhiteSpace(testCase.GetProperty("name").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(testCase.GetProperty("prompt").GetString()));
            if (testCase.TryGetProperty("expectedTool", out var expected)
                && expected.ValueKind == JsonValueKind.String)
            {
                Assert.Contains(expected.GetString()!, registered);
            }

            if (testCase.TryGetProperty("mustNotUse", out var forbidden))
            {
                Assert.All(forbidden.EnumerateArray(), tool =>
                    Assert.Contains(tool.GetString()!, registered));
            }
        });
    }
}
