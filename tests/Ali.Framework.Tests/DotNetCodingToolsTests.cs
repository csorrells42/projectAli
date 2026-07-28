using Ali.Modules.Coding;
using Ali.Modules.Coordinator;
using Ali.Modules.Permissions;
using Ali.Modules.WorkstationFiles;

namespace Ali.Framework.Tests;

public sealed class DotNetCodingToolsTests
{
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
            Assert.NotNull(build.ArtifactPath);
            Assert.True(File.Exists(build.ArtifactPath));

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
            Assert.Contains("error CS", result.Output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("correct", result.Summary, StringComparison.OrdinalIgnoreCase);
        });
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
            Assert.True(AliToolPermissionPolicy.RequiresApproval(AliCapabilityCatalog.DotNetCreateProjectName));
            Assert.Contains(AliCapabilityCatalog.Tools, tool => tool.Name == AliCapabilityCatalog.DotNetBuildName);
            Assert.Contains(AliCapabilityCatalog.Tools, tool => tool.Name == AliCapabilityCatalog.DotNetRunName);
            Assert.Contains(AliCapabilityCatalog.Tools, tool => tool.Name == AliCapabilityCatalog.DotNetCreateProjectName);
        });
    }

    private static async Task WithCodingToolsAsync(
        Func<string, AliWorkstationFileAccess, AliDotNetCodingTools, string, Task> action)
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
            var tools = new AliDotNetCodingTools(access, codingAuditPath);
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
        Func<string, AliWorkstationFileAccess, AliDotNetProjectScaffolder, AliDotNetCodingTools, string, Task> action)
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
            var scaffolder = new AliDotNetProjectScaffolder(access, codingAuditPath);
            var tools = new AliDotNetCodingTools(access, codingAuditPath);
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
