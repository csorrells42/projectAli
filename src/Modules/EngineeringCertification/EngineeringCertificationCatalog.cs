using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ali.Modules.Coordinator;

namespace Ali.Modules.EngineeringCertification;

internal static class EngineeringCertificationCatalog
{
    internal const string CurrentVersion = "engineering-certification-v1";
    private const int VariantsPerFamily = 10;

    internal static EngineeringCertificationSuite CreateCurrent()
    {
        var tasks = new List<EngineeringCertificationTask>(100);
        for (var family = 0; family < 10; family++)
        {
            for (var variant = 1; variant <= VariantsPerFamily; variant++)
            {
                tasks.Add(CreateTask(family, variant));
            }
        }

        return new EngineeringCertificationSuite(CurrentVersion, tasks).Validate();
    }

    internal static string ComputeDigest(EngineeringCertificationSuite suite)
    {
        suite.Validate();
        var canonical = JsonSerializer.Serialize(new
        {
            suite.Version,
            Tasks = suite.Tasks.Select(task => new
            {
                task.Id,
                task.Title,
                task.Prompt,
                task.ExpectedPrimitiveId,
                RequiredToolIds = task.RequiredToolIds.Order(StringComparer.Ordinal),
                FixtureFiles = task.FixtureFiles
                    .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
                    .Select(file => new { file.RelativePath, file.Content }),
                CompletionBudgetTicks = task.CompletionBudget.Ticks,
                task.TokenBudget,
                task.InjectFirstRequiredToolFailure
            })
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static EngineeringCertificationTask CreateTask(int family, int variant)
    {
        var fixture = CreateFixture(family, variant);
        var id = $"eng-{family + 1:00}-{variant:00}";
        var requiredTools = family == 9
            ? new[]
            {
                AliCapabilityCatalog.RoslynAnalyzeProjectName,
                AliCapabilityCatalog.RoslynApplyRenameName,
                AliCapabilityCatalog.DotNetBuildName,
                AliCapabilityCatalog.DotNetTestName
            }
            : new[]
            {
                AliCapabilityCatalog.RoslynAnalyzeProjectName,
                AliCapabilityCatalog.FileWriteName,
                AliCapabilityCatalog.DotNetBuildName,
                AliCapabilityCatalog.DotNetTestName
            };

        return new EngineeringCertificationTask(
            id,
            fixture.Title,
            fixture.Prompt,
            fixture.PrimitiveId,
            requiredTools,
            [
                new EngineeringFixtureFile("Fixture.csproj", LibraryProject),
                new EngineeringFixtureFile("Subject.cs", fixture.Source),
                new EngineeringFixtureFile("Fixture.Tests/Fixture.Tests.csproj", TestProject),
                new EngineeringFixtureFile("Fixture.Tests/SubjectTests.cs", fixture.Tests)
            ],
            CompletionBudget: TimeSpan.FromMinutes(5),
            TokenBudget: 16_384,
            InjectFirstRequiredToolFailure: variant is 1 or 6);
    }

    private static FixtureDefinition CreateFixture(int family, int variant)
    {
        var value = variant.ToString(CultureInfo.InvariantCulture);
        return family switch
        {
            0 => Fixture(
                "Correct an arithmetic implementation",
                $"In the isolated fixture, correct Calculator.AddOffset so it adds {variant}. Preserve the public API and verify the Release build and tests.",
                "method-body-correction",
                """
                namespace CertificationFixture;
                public static class Calculator
                {
                    public static int AddOffset(int value) => value;
                }
                """,
                """
                using CertificationFixture;
                namespace CertificationFixture.Tests;
                public sealed class SubjectTests
                {
                    [Fact] public void AddsConfiguredOffset() => Assert.Equal(10 + __VALUE__, Calculator.AddOffset(10));
                }
                """.Replace("__VALUE__", value, StringComparison.Ordinal)),
            1 => Fixture(
                "Normalize text without changing the API",
                "Correct TextRules.Normalize so it trims surrounding whitespace and returns invariant uppercase text. Preserve its null guard and verify the Release build and tests.",
                "method-body-correction",
                """
                namespace CertificationFixture;
                public static class TextRules
                {
                    public static string Normalize(string input)
                    {
                        ArgumentNullException.ThrowIfNull(input);
                        return input;
                    }
                }
                """,
                """
                using CertificationFixture;
                namespace CertificationFixture.Tests;
                public sealed class SubjectTests
                {
                    [Fact] public void TrimsAndUppercases() => Assert.Equal("ALI", TextRules.Normalize("  Ali "));
                    [Fact] public void PreservesNullGuard() => Assert.Throws<ArgumentNullException>(() => TextRules.Normalize(null!));
                }
                """),
            2 => Fixture(
                "Repair a sequence aggregation",
                $"Correct SequenceRules.SumWithOffset so it returns the sequence sum plus {variant}, including for an empty sequence. Preserve the API and verify the Release build and tests.",
                "sequence-aggregation",
                """
                namespace CertificationFixture;
                public static class SequenceRules
                {
                    public static int SumWithOffset(IEnumerable<int> values)
                    {
                        ArgumentNullException.ThrowIfNull(values);
                        return 0;
                    }
                }
                """,
                """
                using CertificationFixture;
                namespace CertificationFixture.Tests;
                public sealed class SubjectTests
                {
                    [Fact] public void AggregatesValues() => Assert.Equal(6 + __VALUE__, SequenceRules.SumWithOffset([1, 2, 3]));
                    [Fact] public void HandlesEmptyInput() => Assert.Equal(__VALUE__, SequenceRules.SumWithOffset([]));
                }
                """.Replace("__VALUE__", value, StringComparison.Ordinal)),
            3 => Fixture(
                "Preserve order while removing duplicates",
                "Correct SequenceRules.Unique so it removes duplicate integers while preserving first-seen order. Do not change its signature; verify the Release build and tests.",
                "stable-distinct-projection",
                """
                namespace CertificationFixture;
                public static class SequenceRules
                {
                    public static IReadOnlyList<int> Unique(IEnumerable<int> values)
                    {
                        ArgumentNullException.ThrowIfNull(values);
                        return values.ToArray();
                    }
                }
                """,
                """
                using CertificationFixture;
                namespace CertificationFixture.Tests;
                public sealed class SubjectTests
                {
                    [Fact] public void RemovesDuplicatesInStableOrder() => Assert.Equal([3, 1, 2], SequenceRules.Unique([3, 1, 3, 2, 1]));
                }
                """),
            4 => Fixture(
                "Implement a safe integer parser",
                "Correct NumberParser.TryRead so valid invariant integers succeed and invalid text returns false without throwing. Preserve the Try-pattern API; verify the Release build and tests.",
                "try-pattern-implementation",
                """
                using System.Globalization;
                namespace CertificationFixture;
                public static class NumberParser
                {
                    public static bool TryRead(string? input, out int value)
                    {
                        value = 0;
                        return false;
                    }
                }
                """,
                """
                using CertificationFixture;
                namespace CertificationFixture.Tests;
                public sealed class SubjectTests
                {
                    [Fact] public void ParsesInteger() { Assert.True(NumberParser.TryRead("42", out var value)); Assert.Equal(42, value); }
                    [Fact] public void RejectsInvalidText() { Assert.False(NumberParser.TryRead("four", out var value)); Assert.Equal(0, value); }
                }
                """),
            5 => Fixture(
                "Honor asynchronous cancellation",
                $"Correct AsyncRules.WaitAndReturnAsync so it honors the supplied cancellation token, asynchronously waits, and returns the input plus {variant}. Verify the Release build and tests.",
                "cancellation-aware-async",
                """
                namespace CertificationFixture;
                public static class AsyncRules
                {
                    public static Task<int> WaitAndReturnAsync(int value, CancellationToken cancellationToken) => Task.FromResult(value);
                }
                """,
                """
                using CertificationFixture;
                namespace CertificationFixture.Tests;
                public sealed class SubjectTests
                {
                    [Fact] public async Task ReturnsAdjustedValue() => Assert.Equal(5 + __VALUE__, await AsyncRules.WaitAndReturnAsync(5, CancellationToken.None));
                    [Fact] public async Task HonorsCancellation() { using var source = new CancellationTokenSource(); source.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AsyncRules.WaitAndReturnAsync(5, source.Token)); }
                }
                """.Replace("__VALUE__", value, StringComparison.Ordinal)),
            6 => Fixture(
                "Count values by first character",
                "Correct GroupingRules.CountByFirstLetter so it returns case-insensitive counts keyed by uppercase first character and ignores empty strings. Verify the Release build and tests.",
                "keyed-aggregation",
                """
                namespace CertificationFixture;
                public static class GroupingRules
                {
                    public static IReadOnlyDictionary<char, int> CountByFirstLetter(IEnumerable<string> values) => new Dictionary<char, int>();
                }
                """,
                """
                using CertificationFixture;
                namespace CertificationFixture.Tests;
                public sealed class SubjectTests
                {
                    [Fact] public void CountsCaseInsensitively() { var result = GroupingRules.CountByFirstLetter(["Ali", "agent", "Build", ""]); Assert.Equal(2, result['A']); Assert.Equal(1, result['B']); }
                }
                """),
            7 => Fixture(
                "Clamp a value to a symmetric range",
                $"Correct RangeRules.Clamp so it constrains values to the inclusive range -{variant} through {variant}. Preserve the API and verify the Release build and tests.",
                "bounded-value-transform",
                """
                namespace CertificationFixture;
                public static class RangeRules
                {
                    public static int Clamp(int value) => value;
                }
                """,
                """
                using CertificationFixture;
                namespace CertificationFixture.Tests;
                public sealed class SubjectTests
                {
                    [Theory] [InlineData(100, __VALUE__)] [InlineData(-100, -__VALUE__)] [InlineData(0, 0)]
                    public void ClampsToRange(int input, int expected) => Assert.Equal(expected, RangeRules.Clamp(input));
                }
                """.Replace("__VALUE__", value, StringComparison.Ordinal)),
            8 => Fixture(
                "Compute an empty-safe mean",
                "Correct AverageRules.Mean so it returns zero for an empty sequence and otherwise returns the arithmetic mean as a double. Verify the Release build and tests.",
                "empty-safe-aggregation",
                """
                namespace CertificationFixture;
                public static class AverageRules
                {
                    public static double Mean(IEnumerable<int> values) => 0;
                }
                """,
                """
                using CertificationFixture;
                namespace CertificationFixture.Tests;
                public sealed class SubjectTests
                {
                    [Fact] public void ComputesMean() => Assert.Equal(2.5, AverageRules.Mean([1, 2, 3, 4]));
                    [Fact] public void EmptyIsZero() => Assert.Equal(0, AverageRules.Mean([]));
                }
                """),
            9 => CreateRenameFixture(variant),
            _ => throw new ArgumentOutOfRangeException(nameof(family))
        };
    }

    private static FixtureDefinition CreateRenameFixture(int variant)
    {
        var currentName = $"ComputeValue{variant}";
        return Fixture(
            "Apply a solution-wide semantic rename",
            $"Use a semantic C# rename to rename LegacyCalculator.Compute to {currentName} across the fixture. Do not add a duplicate wrapper; verify the Release build and tests.",
            "solution-wide-semantic-rename",
            """
            namespace CertificationFixture;
            public static class LegacyCalculator
            {
                public static int Compute(int value) => value * 2;
            }
            """,
            """
            using CertificationFixture;
            namespace CertificationFixture.Tests;
            public sealed class SubjectTests
            {
                [Fact] public void RenamedMemberStillWorks() => Assert.Equal(8, LegacyCalculator.__CURRENT__(4));
            }
            """.Replace("__CURRENT__", currentName, StringComparison.Ordinal));
    }

    private static FixtureDefinition Fixture(
        string title,
        string prompt,
        string primitiveId,
        string source,
        string tests) =>
        new(title, prompt, primitiveId, source.ReplaceLineEndings("\n"), tests.ReplaceLineEndings("\n"));

    private const string LibraryProject = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <Nullable>enable</Nullable>
            <ImplicitUsings>enable</ImplicitUsings>
          </PropertyGroup>
          <ItemGroup>
            <Compile Remove="Fixture.Tests\**\*.cs" />
          </ItemGroup>
        </Project>
        """;

    private const string TestProject = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <Nullable>enable</Nullable>
            <ImplicitUsings>enable</ImplicitUsings>
            <IsTestProject>true</IsTestProject>
          </PropertyGroup>
          <ItemGroup>
            <Using Include="Xunit" />
            <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.0.1" />
            <PackageReference Include="xunit.v3" Version="3.2.2" />
            <PackageReference Include="xunit.runner.visualstudio" Version="3.1.5" PrivateAssets="all" />
            <ProjectReference Include="..\Fixture.csproj" />
          </ItemGroup>
        </Project>
        """;

    private sealed record FixtureDefinition(
        string Title,
        string Prompt,
        string PrimitiveId,
        string Source,
        string Tests);
}
