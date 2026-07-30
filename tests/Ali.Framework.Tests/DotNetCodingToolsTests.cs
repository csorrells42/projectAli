using Ali.Modules.Coding;
using Ali.Modules.Coordinator;
using Ali.Modules.Permissions;
using Ali.Modules.WorkstationFiles;

namespace Ali.Framework.Tests;

[Collection(ProcessEnvironmentIntegrationCollection.Name)]
public sealed class DotNetCodingToolsTests
{
    [Fact]
    public async Task UntouchedScaffold_IsRejectedUntilRequestedSourceIsWritten()
    {
        await WithProjectToolsAsync(async (root, access, scaffolder, tools, auditPath) =>
        {
            var created = await scaffolder.CreateAsync(
                "Workspace/RequestedApp/RequestedApp.csproj",
                "console",
                TestContext.Current.CancellationToken);
            Assert.True(created.Success, created.Output);

            var untouchedBuild = await tools.BuildAsync(
                "Workspace/RequestedApp/RequestedApp.csproj",
                "Debug",
                TestContext.Current.CancellationToken);
            Assert.False(untouchedBuild.Success);
            Assert.Null(untouchedBuild.ExitCode);
            Assert.Contains("untouched SDK template", untouchedBuild.Summary, StringComparison.OrdinalIgnoreCase);

            var untouchedRun = await tools.RunAsync(
                "Workspace/RequestedApp/RequestedApp.csproj",
                "Debug",
                TestContext.Current.CancellationToken);
            Assert.False(untouchedRun.Success);
            Assert.Contains("untouched SDK template", untouchedRun.Summary, StringComparison.OrdinalIgnoreCase);

            await access.Store.WriteAsync(
                "Workspace/RequestedApp/Program.cs",
                """
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "implemented.txt"), "yes");
                """,
                TestContext.Current.CancellationToken);
            var implementedBuild = await tools.BuildAsync(
                "Workspace/RequestedApp/RequestedApp.csproj",
                "Debug",
                TestContext.Current.CancellationToken);
            Assert.True(implementedBuild.Success, implementedBuild.Output);
        });
    }

    [Fact]
    public async Task RoslynIntelligence_AnalyzesFormatsFindsSymbolsAndProvidesCompletions()
    {
        await WithCodingToolsAsync(async (root, access, tools, auditPath) =>
        {
            await access.Store.WriteAsync(
                "Workspace/Intelligence/Intelligence.csproj",
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Library</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                  </PropertyGroup>
                </Project>
                """,
                TestContext.Current.CancellationToken);
            const string source = "namespace Intelligence; public sealed class Calculator{public int Add(int left,int right){return left+right;} public void Print(){Console.WriteLine(Add(1,2));}}";
            await access.Store.WriteAsync(
                "Workspace/Intelligence/Program.cs",
                source,
                TestContext.Current.CancellationToken);

            var build = await tools.BuildAsync(
                "Workspace/Intelligence/Intelligence.csproj",
                "Debug",
                TestContext.Current.CancellationToken);
            Assert.True(build.Success, build.Output);

            var analysis = await tools.AnalyzeAsync(
                "Workspace/Intelligence/Intelligence.csproj",
                TestContext.Current.CancellationToken);
            Assert.True(analysis.Success, string.Join(Environment.NewLine, analysis.Diagnostics.Select(item => item.Message)));
            Assert.True(analysis.DocumentCount >= 1);
            Assert.DoesNotContain(analysis.Diagnostics, diagnostic => diagnostic.Severity == "Error");

            var symbols = await tools.FindSymbolAsync(
                "Workspace/Intelligence/Intelligence.csproj",
                "Calculator",
                TestContext.Current.CancellationToken);
            Assert.True(symbols.Success);
            Assert.Contains(symbols.Matches, match => match.Symbol == "Calculator" && match.Kind == "NamedType");

            var completionPosition = source.IndexOf("Console.", StringComparison.Ordinal) + "Console.".Length;
            var completions = await tools.GetCompletionsAsync(
                "Workspace/Intelligence/Intelligence.csproj",
                "Workspace/Intelligence/Program.cs",
                1,
                completionPosition + 1,
                TestContext.Current.CancellationToken);
            Assert.True(completions.Success);
            Assert.Contains("WriteLine", completions.Completions);

            var formatted = await tools.FormatAsync(
                "Workspace/Intelligence/Intelligence.csproj",
                TestContext.Current.CancellationToken);
            Assert.True(formatted.Success, formatted.Summary);
            Assert.Contains("Program.cs", formatted.ChangedFiles);
            var formattedSource = await access.Store.ReadAsync(
                "Workspace/Intelligence/Program.cs",
                TestContext.Current.CancellationToken);
            Assert.NotEqual(source, formattedSource);
            Assert.Contains("Calculator {", formattedSource, StringComparison.Ordinal);
            var audit = await File.ReadAllTextAsync(auditPath, TestContext.Current.CancellationToken);
            Assert.Contains("\"engine\":\"Roslyn/MSBuild\"", audit, StringComparison.Ordinal);
            Assert.Contains("\"operation\":\"format\"", audit, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task RoslynLayersOneThroughFive_LoadNavigateClassifyPreviewAndRenameAcrossSolution()
    {
        await WithCodingToolsAsync(async (root, access, tools, auditPath) =>
        {
            await access.Store.WriteAsync(
                "Workspace/SemanticSolution/SemanticSolution.slnx",
                """
                <Solution>
                  <Project Path="Library/Library.csproj" />
                  <Project Path="App/App.csproj" />
                </Solution>
                """,
                TestContext.Current.CancellationToken);
            await access.Store.WriteAsync(
                "Workspace/SemanticSolution/Library/Library.csproj",
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                </Project>
                """,
                TestContext.Current.CancellationToken);
            await access.Store.WriteAsync(
                "Workspace/SemanticSolution/App/App.csproj",
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup><ProjectReference Include="..\Library\Library.csproj" /></ItemGroup>
                </Project>
                """,
                TestContext.Current.CancellationToken);
            const string librarySource = """
                namespace SemanticSolution.Library;

                public sealed class Calculator
                {
                    public int Add(int left, int right) => left + right;
                }
                """;
            const string appSource = """
                using SemanticSolution.Library;

                var calculator = new Calculator();
                Console.WriteLine(calculator.Add(1, 2));
                """;
            await access.Store.WriteAsync(
                "Workspace/SemanticSolution/Library/Calculator.cs",
                librarySource,
                TestContext.Current.CancellationToken);
            await access.Store.WriteAsync(
                "Workspace/SemanticSolution/App/Program.cs",
                appSource,
                TestContext.Current.CancellationToken);

            const string target = "Workspace/SemanticSolution/SemanticSolution.slnx";
            var overview = await tools.InspectSolutionAsync(target, TestContext.Current.CancellationToken);
            Assert.True(overview.Success, overview.Summary);
            Assert.Equal(2, overview.Projects.Count);
            Assert.Contains(overview.Projects, project => project.Name == "Library" && project.TargetFrameworks.Contains("net10.0"));
            Assert.Contains(overview.Projects, project => project.Name == "App" && project.ProjectReferences.Contains("Library"));

            var document = await tools.InspectDocumentAsync(
                target,
                "Workspace/SemanticSolution/Library/Calculator.cs",
                TestContext.Current.CancellationToken);
            Assert.True(document.Success, document.Summary);
            Assert.Contains(document.Outline, symbol => symbol.Name == "Calculator" && symbol.Kind == "NamedType");
            Assert.Contains(document.Outline, symbol => symbol.Name == "Add" && symbol.Kind == "Method");
            Assert.Contains(document.Classifications, span => span.Text == "Calculator");

            var addColumn = appSource.Split('\n')[3].IndexOf("Add", StringComparison.Ordinal) + 1;
            var position = await tools.InspectPositionAsync(
                target,
                "Workspace/SemanticSolution/App/Program.cs",
                4,
                addColumn,
                TestContext.Current.CancellationToken);
            Assert.True(position.Success, position.Summary);
            Assert.Contains("Add", position.Symbol, StringComparison.Ordinal);
            Assert.Contains(position.Definitions, definition => definition.File?.EndsWith("Library\\Calculator.cs", StringComparison.OrdinalIgnoreCase) == true);
            Assert.Contains(position.Signatures, signature => signature.Contains("Add(int left, int right)", StringComparison.Ordinal));

            var references = await tools.FindReferencesAsync(
                target,
                "Workspace/SemanticSolution/Library/Calculator.cs",
                5,
                16,
                TestContext.Current.CancellationToken);
            Assert.True(references.Success, references.Summary);
            Assert.Contains(references.Locations, location =>
                location.Kind == "Reference"
                && location.File?.EndsWith("App\\Program.cs", StringComparison.OrdinalIgnoreCase) == true);

            var preview = await tools.PreviewRenameAsync(
                target,
                "Workspace/SemanticSolution/Library/Calculator.cs",
                5,
                16,
                "Sum",
                TestContext.Current.CancellationToken);
            Assert.True(preview.Success, preview.Summary);
            Assert.False(preview.Applied);
            Assert.Equal(2, preview.ChangedFiles.Count);
            Assert.Contains("Add", await access.Store.ReadAsync(
                "Workspace/SemanticSolution/App/Program.cs",
                TestContext.Current.CancellationToken), StringComparison.Ordinal);

            var applied = await tools.ApplyRenameAsync(
                target,
                "Workspace/SemanticSolution/Library/Calculator.cs",
                5,
                16,
                "Sum",
                TestContext.Current.CancellationToken);
            Assert.True(applied.Success, applied.Summary);
            Assert.True(applied.Applied);
            Assert.Contains("Sum", await access.Store.ReadAsync(
                "Workspace/SemanticSolution/Library/Calculator.cs",
                TestContext.Current.CancellationToken), StringComparison.Ordinal);
            Assert.Contains("calculator.Sum", await access.Store.ReadAsync(
                "Workspace/SemanticSolution/App/Program.cs",
                TestContext.Current.CancellationToken), StringComparison.Ordinal);
            var audit = await File.ReadAllTextAsync(auditPath, TestContext.Current.CancellationToken);
            Assert.Contains("\"operation\":\"rename\"", audit, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task CreateBuildAndRun_CreatesARealWpfTicTacToeApplication()
    {
        await WithProjectToolsAsync(async (root, access, scaffolder, tools, auditPath) =>
        {
            var created = await scaffolder.CreateAsync(
                "Workspace/TicTacToe/TicTacToe.csproj",
                "wpf",
                TestContext.Current.CancellationToken);

            Assert.True(created.Success, created.Output);
            Assert.Equal(0, created.ExitCode);
            Assert.True(File.Exists(Path.Combine(root, "workspace", "TicTacToe", "TicTacToe.csproj")));
            Assert.True(File.Exists(Path.Combine(root, "workspace", "TicTacToe", "App.xaml")));

            await access.Store.WriteAsync(
                "Workspace/TicTacToe/MainWindow.xaml",
                """
                <Window x:Class="TicTacToe.MainWindow"
                        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                        Title="Tic Tac Toe" Height="430" Width="380">
                    <Grid Margin="18">
                        <Grid.RowDefinitions>
                            <RowDefinition Height="Auto" />
                            <RowDefinition Height="*" />
                            <RowDefinition Height="Auto" />
                        </Grid.RowDefinitions>
                        <TextBlock x:Name="StatusText" Text="X's turn" FontSize="22" HorizontalAlignment="Center" Margin="0,0,0,12" />
                        <UniformGrid Grid.Row="1" Rows="3" Columns="3">
                            <Button Tag="0" Click="Square_Click" FontSize="40" />
                            <Button Tag="1" Click="Square_Click" FontSize="40" />
                            <Button Tag="2" Click="Square_Click" FontSize="40" />
                            <Button Tag="3" Click="Square_Click" FontSize="40" />
                            <Button Tag="4" Click="Square_Click" FontSize="40" />
                            <Button Tag="5" Click="Square_Click" FontSize="40" />
                            <Button Tag="6" Click="Square_Click" FontSize="40" />
                            <Button Tag="7" Click="Square_Click" FontSize="40" />
                            <Button Tag="8" Click="Square_Click" FontSize="40" />
                        </UniformGrid>
                        <Button Grid.Row="2" Content="New Game" Click="Reset_Click" Padding="18,8" Margin="0,12,0,0" HorizontalAlignment="Center" />
                    </Grid>
                </Window>
                """,
                TestContext.Current.CancellationToken);
            await access.Store.WriteAsync(
                "Workspace/TicTacToe/MainWindow.xaml.cs",
                """
                using System.Windows;
                using System.Windows.Controls;
                using System.Windows.Threading;

                namespace TicTacToe;

                public partial class MainWindow : Window
                {
                    private readonly string[] _board = new string[9];
                    private string _turn = "X";

                    public MainWindow()
                    {
                        InitializeComponent();
                        Loaded += (_, _) =>
                        {
                            System.IO.File.WriteAllText(
                                System.IO.Path.Combine(AppContext.BaseDirectory, "wpf-run-proof.txt"),
                                "launched");
                            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                            timer.Tick += (_, _) => { timer.Stop(); Close(); };
                            timer.Start();
                        };
                    }

                    private void Square_Click(object sender, RoutedEventArgs e)
                    {
                        if (sender is not Button button || button.Tag is not string tag)
                        {
                            return;
                        }

                        var index = int.Parse(tag);
                        if (_board[index] is not null)
                        {
                            return;
                        }

                        _board[index] = _turn;
                        button.Content = _turn;
                        if (HasWinner(_turn))
                        {
                            StatusText.Text = $"{_turn} wins!";
                            return;
                        }

                        if (_board.All(square => square is not null))
                        {
                            StatusText.Text = "Draw!";
                            return;
                        }

                        _turn = _turn == "X" ? "O" : "X";
                        StatusText.Text = $"{_turn}'s turn";
                    }

                    private bool HasWinner(string player)
                    {
                        int[][] lines =
                        [
                            [0, 1, 2], [3, 4, 5], [6, 7, 8],
                            [0, 3, 6], [1, 4, 7], [2, 5, 8],
                            [0, 4, 8], [2, 4, 6]
                        ];
                        return lines.Any(line => line.All(index => _board[index] == player));
                    }

                    private void Reset_Click(object sender, RoutedEventArgs e)
                    {
                        Array.Clear(_board);
                        _turn = "X";
                        StatusText.Text = "X's turn";
                        foreach (var button in FindVisualChildren<Button>(this).Where(button => button.Tag is not null))
                        {
                            button.Content = null;
                        }
                    }

                    private static IEnumerable<T> FindVisualChildren<T>(System.Windows.DependencyObject parent)
                        where T : System.Windows.DependencyObject
                    {
                        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
                        {
                            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, index);
                            if (child is T typed)
                            {
                                yield return typed;
                            }

                            foreach (var descendant in FindVisualChildren<T>(child))
                            {
                                yield return descendant;
                            }
                        }
                    }
                }
                """,
                TestContext.Current.CancellationToken);

            var build = await tools.BuildAsync(
                "Workspace/TicTacToe/TicTacToe.csproj",
                "Release",
                TestContext.Current.CancellationToken);
            Assert.True(build.Success, build.Output);

            var run = await tools.RunAsync(
                "Workspace/TicTacToe/TicTacToe.csproj",
                "Release",
                TestContext.Current.CancellationToken);
            Assert.True(run.Success, run.Summary);

            var proofPath = Path.Combine(Path.GetDirectoryName(run.ArtifactPath!)!, "wpf-run-proof.txt");
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!File.Exists(proofPath) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(100, TestContext.Current.CancellationToken);
            }

            Assert.True(File.Exists(proofPath), "The WPF application did not launch and load its main window.");
            if (run.ProcessId is int processId)
            {
                try
                {
                    using var launchedProcess = System.Diagnostics.Process.GetProcessById(processId);
                    using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await launchedProcess.WaitForExitAsync(exitTimeout.Token);
                }
                catch (ArgumentException)
                {
                    // The short-lived test window had already closed.
                }
            }

            var audit = await File.ReadAllTextAsync(auditPath, TestContext.Current.CancellationToken);
            Assert.Contains("\"operation\":\"create\"", audit, StringComparison.Ordinal);
            Assert.Contains("\"operation\":\"build\"", audit, StringComparison.Ordinal);
            Assert.Contains("\"operation\":\"run\"", audit, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Create_RejectsUnsupportedTemplatesAndExistingProjectFolders()
    {
        await WithProjectToolsAsync(async (root, access, scaffolder, tools, auditPath) =>
        {
            await Assert.ThrowsAsync<ArgumentException>(() => scaffolder.CreateAsync(
                "Workspace/NewApp/NewApp.csproj",
                "classlib",
                TestContext.Current.CancellationToken));

            await access.Store.WriteAsync(
                "Workspace/Occupied/readme.txt",
                "existing content",
                TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<IOException>(() => scaffolder.CreateAsync(
                "Workspace/Occupied/Occupied.csproj",
                "console",
                TestContext.Current.CancellationToken));

            await Assert.ThrowsAsync<ArgumentException>(() => scaffolder.CreateAsync(
                Path.Combine(root, "Outside", "Outside.csproj"),
                "console",
                TestContext.Current.CancellationToken));
        });
    }

    [Fact]
    public async Task BuildAndRun_CompilesAndLaunchesApprovedTemporaryProject()
    {
        await WithCodingToolsAsync(async (root, access, tools, auditPath) =>
        {
            await access.Store.WriteAsync(
                "Workspace/TinyGame/TinyGame.csproj",
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                  </PropertyGroup>
                </Project>
                """,
                TestContext.Current.CancellationToken);
            await access.Store.WriteAsync(
                "Workspace/TinyGame/Program.cs",
                """
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "ali-run-proof.txt"), "launched");
                """,
                TestContext.Current.CancellationToken);

            var build = await tools.BuildAsync(
                "Workspace/TinyGame/TinyGame.csproj",
                "Release",
                TestContext.Current.CancellationToken);

            Assert.True(build.Success, build.Output);
            Assert.Equal(0, build.ExitCode);
            Assert.Equal(0, build.WarningCount);
            Assert.Equal(0, build.ErrorCount);
            Assert.NotNull(build.ArtifactPath);
            Assert.True(File.Exists(build.ArtifactPath));
            Assert.True(build.Output.Length <= 4_000);
            Assert.NotNull(build.DiagnosticLogPath);
            Assert.True(File.Exists(build.DiagnosticLogPath));

            var run = await tools.RunAsync(
                "Workspace/TinyGame/TinyGame.csproj",
                "Release",
                TestContext.Current.CancellationToken);
            Assert.True(run.Success, run.Summary);
            Assert.NotNull(run.ProcessId);

            var proofPath = Path.Combine(Path.GetDirectoryName(run.ArtifactPath!)!, "ali-run-proof.txt");
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!File.Exists(proofPath) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(100, TestContext.Current.CancellationToken);
            }

            Assert.True(File.Exists(proofPath), "The launched test application did not write its proof file.");
            Assert.Equal("launched", await File.ReadAllTextAsync(proofPath, TestContext.Current.CancellationToken));
            var audit = await File.ReadAllTextAsync(auditPath, TestContext.Current.CancellationToken);
            Assert.Contains("\"operation\":\"build\"", audit, StringComparison.Ordinal);
            Assert.Contains("\"operation\":\"run\"", audit, StringComparison.Ordinal);
            Assert.DoesNotContain("File.WriteAllText", audit, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Run_RejectsArtifactWhenItsRequiredRuntimeIsNotInstalled()
    {
        await WithCodingToolsAsync(async (root, access, tools, auditPath) =>
        {
            await access.Store.WriteAsync(
                "Workspace/RuntimeMismatch/RuntimeMismatch.csproj",
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """,
                TestContext.Current.CancellationToken);
            await access.Store.WriteAsync(
                "Workspace/RuntimeMismatch/Program.cs",
                "System.Console.WriteLine(\"should not launch\");",
                TestContext.Current.CancellationToken);

            var build = await tools.BuildAsync(
                "Workspace/RuntimeMismatch/RuntimeMismatch.csproj",
                "Release",
                TestContext.Current.CancellationToken);
            Assert.True(build.Success, build.Output);
            var runtimeConfig = Path.ChangeExtension(build.ArtifactPath!, ".runtimeconfig.json");
            await File.WriteAllTextAsync(
                runtimeConfig,
                """
                {"runtimeOptions":{"framework":{"name":"Microsoft.NETCore.App","version":"99.0.0"}}}
                """,
                TestContext.Current.CancellationToken);

            var run = await tools.RunAsync(
                "Workspace/RuntimeMismatch/RuntimeMismatch.csproj",
                "Release",
                TestContext.Current.CancellationToken);

            Assert.False(run.Success);
            Assert.Null(run.ProcessId);
            Assert.Contains("requires Microsoft.NETCore.App 99.0.0", run.Summary, StringComparison.Ordinal);
            Assert.Contains("process was not started", run.Summary, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void ArtifactDiscovery_UnderstandsVisualStudioPlatformConfigurationFolders()
    {
        var root = Path.Combine(Path.GetTempPath(), "ali-artifact-layout-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var projectPath = Path.Combine(root, "PolishedGame.csproj");
            var artifactPath = Path.Combine(root, "bin", "Any CPU", "Debug", "net10.0-windows", "PolishedGame.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
            File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.WriteAllText(artifactPath, "proof");

            var found = AliRoslynCodingTools.FindBuiltArtifact(projectPath, "Debug");

            Assert.Equal(artifactPath, found, ignoreCase: true);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Build_ReturnsCompilerDiagnosticsWithoutClaimingSuccess()
    {
        await WithCodingToolsAsync(async (root, access, tools, auditPath) =>
        {
            await access.Store.WriteAsync(
                "Workspace/Broken/Broken.csproj",
                """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""",
                TestContext.Current.CancellationToken);
            await access.Store.WriteAsync(
                "Workspace/Broken/Program.cs",
                "this is not valid C#;",
                TestContext.Current.CancellationToken);

            var result = await tools.BuildAsync(
                "Workspace/Broken/Broken.csproj",
                "Debug",
                TestContext.Current.CancellationToken);

            Assert.False(result.Success);
            Assert.NotEqual(0, result.ExitCode);
            Assert.True(result.ErrorCount > 0, result.Output);
            Assert.Contains("error CS", result.Output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("correct", result.Summary, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task RunningTarget_IsStoppedThroughAnExplicitToolBeforeRebuild()
    {
        await WithProjectToolsAsync(async (root, access, scaffolder, tools, auditPath) =>
        {
            var created = await scaffolder.CreateAsync(
                "Workspace/RunningApp/RunningApp.csproj",
                "console",
                TestContext.Current.CancellationToken);
            Assert.True(created.Success, created.Summary);
            await access.Store.WriteAsync(
                "Workspace/RunningApp/Program.cs",
                "using System.Threading; Thread.Sleep(Timeout.Infinite);",
                TestContext.Current.CancellationToken);

            int? launchedProcessId = null;
            try
            {
                var firstBuild = await tools.BuildAsync(
                    "Workspace/RunningApp/RunningApp.csproj",
                    "Release",
                    TestContext.Current.CancellationToken);
                Assert.True(firstBuild.Success, firstBuild.Output);

                var run = await tools.RunAsync(
                    "Workspace/RunningApp/RunningApp.csproj",
                    "Release",
                    TestContext.Current.CancellationToken);
                Assert.True(run.Success, run.Summary);
                launchedProcessId = Assert.IsType<int>(run.ProcessId);

                var blockedBuild = await tools.BuildAsync(
                    "Workspace/RunningApp/RunningApp.csproj",
                    "Release",
                    TestContext.Current.CancellationToken);
                Assert.False(blockedBuild.Success);
                Assert.Equal("RunningTarget", blockedBuild.FailureKind);
                Assert.Equal(launchedProcessId, blockedBuild.BlockingProcessId);
                Assert.Equal(run.ArtifactPath, blockedBuild.ArtifactPath);
                Assert.Contains("MSBuild was not started", blockedBuild.Summary, StringComparison.Ordinal);
                Assert.Contains("dotnet_stop_project", blockedBuild.Output, StringComparison.Ordinal);

                var stopped = await tools.StopProjectAsync(
                    "Workspace/RunningApp/RunningApp.csproj",
                    "Release",
                    TestContext.Current.CancellationToken);
                Assert.True(stopped.Success, stopped.Summary);
                Assert.Equal(launchedProcessId, stopped.ProcessId);

                var rebuilt = await tools.BuildAsync(
                    "Workspace/RunningApp/RunningApp.csproj",
                    "Release",
                    TestContext.Current.CancellationToken);
                Assert.True(rebuilt.Success, rebuilt.Output);
                var audit = await File.ReadAllTextAsync(auditPath, TestContext.Current.CancellationToken);
                Assert.Contains("\"operation\":\"stop-project\"", audit, StringComparison.Ordinal);
            }
            finally
            {
                if (launchedProcessId is int processId)
                {
                    try
                    {
                        using var process = System.Diagnostics.Process.GetProcessById(processId);
                        if (!process.HasExited)
                        {
                            process.Kill(entireProcessTree: true);
                            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
                        }
                    }
                    catch (ArgumentException)
                    {
                        // The tested stop tool already ended the target process.
                    }
                }
            }
        });
    }

    [Theory]
    [InlineData("error MSB3021: Unable to copy file because it is being used by another process.")]
    [InlineData("error MSB3027: Exceeded retry count while copying a locked target.")]
    public void MsBuildOutputLockDiagnostics_AreClassifiedWithoutInterpretingEnglish(string output)
    {
        Assert.True(AliRoslynCodingTools.IsOutputLockFailure(output));
        Assert.False(AliRoslynCodingTools.IsOutputLockFailure("error CS1002: ; expected"));
    }

    [Fact]
    public async Task Tools_RejectOutsidePathsInvalidConfigurationsAndRequireApproval()
    {
        await WithCodingToolsAsync(async (root, access, tools, auditPath) =>
        {
            await Assert.ThrowsAsync<ArgumentException>(() => tools.BuildAsync(
                Path.Combine(root, "outside.csproj"),
                "Release",
                TestContext.Current.CancellationToken));
            await access.Store.WriteAsync(
                "Workspace/App/App.csproj",
                """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""",
                TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<ArgumentException>(() => tools.BuildAsync(
                "Workspace/App/App.csproj",
                "Release --target Clean",
                TestContext.Current.CancellationToken));

            Assert.True(AliToolPermissionPolicy.RequiresApproval(AliCapabilityCatalog.DotNetBuildName));
            Assert.True(AliToolPermissionPolicy.RequiresApproval(AliCapabilityCatalog.DotNetRunName));
            Assert.True(AliToolPermissionPolicy.RequiresApproval(AliCapabilityCatalog.DotNetStopProjectName));
            Assert.True(AliToolPermissionPolicy.RequiresApproval(AliCapabilityCatalog.DotNetCreateProjectName));
            Assert.True(AliToolPermissionPolicy.RequiresApproval(AliCapabilityCatalog.RoslynFormatProjectName));
            Assert.Contains(AliCapabilityCatalog.Tools, tool => tool.Name == AliCapabilityCatalog.DotNetBuildName);
            Assert.Contains(AliCapabilityCatalog.Tools, tool => tool.Name == AliCapabilityCatalog.DotNetRunName);
            Assert.Contains(AliCapabilityCatalog.Tools, tool => tool.Name == AliCapabilityCatalog.DotNetStopProjectName);
            Assert.Contains(
                Ali.Modules.Mcp.McpServerToolCatalog.CreateDefaultPolicies(),
                policy => policy.Name == AliCapabilityCatalog.DotNetStopProjectName
                    && !policy.Enabled
                    && policy.WritesLocalData
                    && policy.ReadsPrivateData);
            Assert.Contains(AliCapabilityCatalog.Tools, tool => tool.Name == AliCapabilityCatalog.DotNetCreateProjectName);
            Assert.Contains(AliCapabilityCatalog.Tools, tool => tool.Name == AliCapabilityCatalog.RoslynAnalyzeProjectName);
            Assert.Contains(AliCapabilityCatalog.Tools, tool => tool.Name == AliCapabilityCatalog.RoslynFormatProjectName);
            Assert.Contains(AliCapabilityCatalog.Tools, tool => tool.Name == AliCapabilityCatalog.RoslynFindSymbolName);
            Assert.Contains(AliCapabilityCatalog.Tools, tool => tool.Name == AliCapabilityCatalog.RoslynGetCompletionsName);
            Assert.Contains(AliCapabilityCatalog.Tools, tool => tool.Name == AliCapabilityCatalog.RoslynInspectSolutionName);
            Assert.Contains(AliCapabilityCatalog.Tools, tool => tool.Name == AliCapabilityCatalog.RoslynInspectDocumentName);
            Assert.Contains(AliCapabilityCatalog.Tools, tool => tool.Name == AliCapabilityCatalog.RoslynInspectPositionName);
            Assert.Contains(AliCapabilityCatalog.Tools, tool => tool.Name == AliCapabilityCatalog.RoslynFindReferencesName);
            Assert.Contains(AliCapabilityCatalog.Tools, tool => tool.Name == AliCapabilityCatalog.RoslynPreviewRenameName);
            Assert.Contains(AliCapabilityCatalog.Tools, tool => tool.Name == AliCapabilityCatalog.RoslynApplyRenameName);
        });
    }

    private static async Task WithCodingToolsAsync(
        Func<string, AliWorkstationFileAccess, AliRoslynCodingTools, string, Task> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "AliDotNetCodingTests", Guid.NewGuid().ToString("N"));
        try
        {
            var permissions = new AgentToolPermissionStore(root);
            var store = new AliWorkstationFileStore(
            [
                new AliWorkstationFileMount("Workspace", Path.Combine(root, "workspace"))
            ], Path.Combine(root, "trash"));
            var audit = new AgentFileActionAuditStore(root, activeUsers: null);
            var access = new AliWorkstationFileAccess(store, audit, permissions);
            var codingAuditPath = Path.Combine(root, "dotnet-actions.jsonl");
            var tracker = new AliCodingProjectTracker();
            var tools = new AliRoslynCodingTools(new AliCodingProjectResolver(access), tracker, codingAuditPath);
            await action(root, access, tools, codingAuditPath);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task WithProjectToolsAsync(
        Func<string, AliWorkstationFileAccess, AliDotNetProjectScaffolder, AliRoslynCodingTools, string, Task> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "AliDotNetProjectTests", Guid.NewGuid().ToString("N"));
        try
        {
            var permissions = new AgentToolPermissionStore(root);
            var store = new AliWorkstationFileStore(
            [
                new AliWorkstationFileMount("Workspace", Path.Combine(root, "workspace"))
            ], Path.Combine(root, "trash"));
            var audit = new AgentFileActionAuditStore(root, activeUsers: null);
            var access = new AliWorkstationFileAccess(store, audit, permissions);
            var codingAuditPath = Path.Combine(root, "dotnet-actions.jsonl");
            var tracker = new AliCodingProjectTracker();
            var scaffolder = new AliDotNetProjectScaffolder(access, tracker, codingAuditPath);
            var tools = new AliRoslynCodingTools(new AliCodingProjectResolver(access), tracker, codingAuditPath);
            await action(root, access, scaffolder, tools, codingAuditPath);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
