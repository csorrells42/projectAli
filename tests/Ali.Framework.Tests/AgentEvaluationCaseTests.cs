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
        Assert.True(cases.Length >= 18, $"Only {cases.Length} routing cases are registered.");
        var categories = cases.Select(testCase => testCase.GetProperty("category").GetString()).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("logic", categories);
        Assert.Contains("truthfulness", categories);
        Assert.Contains("memory", categories);
        Assert.Contains("current-events", categories);
        Assert.Contains("files", categories);
        Assert.Contains("coding", categories);
        Assert.Contains("recovery", categories);
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
