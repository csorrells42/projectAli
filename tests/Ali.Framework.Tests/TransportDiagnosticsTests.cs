using Ali.Modules.Diagnostics;

namespace Ali.Framework.Tests;

public sealed class TransportDiagnosticsTests
{
    [Fact]
    public void Capture_ReturnsTheLatestFourRawPayloads()
    {
        AliTransportDiagnostics.RecordModelRequest("model-request");
        AliTransportDiagnostics.AppendModelResponseLine("model-response-1");
        AliTransportDiagnostics.AppendModelResponseLine("model-response-2");
        AliTransportDiagnostics.RecordSerenaRequest("serena-request");
        AliTransportDiagnostics.RecordSerenaResponse("serena-response");

        var snapshot = AliTransportDiagnostics.Capture();

        Assert.Equal("model-request", snapshot.ModelRequest);
        Assert.Equal($"model-response-1{Environment.NewLine}model-response-2", snapshot.ModelResponse);
        Assert.Equal("serena-request", snapshot.SerenaRequest);
        Assert.Equal("serena-response", snapshot.SerenaResponse);
    }

    [Fact]
    public void OutboundCapture_IsImmediatelyBeforeEachLiveSend()
    {
        var runtime = ReadRepositoryFile(
            "src", "Modules", "Runtime", "OpenAiCompatibleLocalModelRuntime.ExtensionsAI.cs");
        var serena = ReadRepositoryFile(
            "src", "Modules", "Serena", "SerenaTransportDiagnosticsAIFunction.cs");

        Assert.Contains(
            "AliTransportDiagnostics.RecordModelRequest(payload);\n            using var response = await SendRuntimeAsync",
            runtime,
            StringComparison.Ordinal);
        Assert.Contains(
            "AliTransportDiagnostics.RecordSerenaRequest(SerializeRequest(Name, arguments));\n        var result = await InnerFunction.InvokeAsync",
            serena,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DiagnosticsWindow_ContainsExactlyFourPayloadTextBoxes()
    {
        var xaml = ReadRepositoryFile("src", "UI", "TransportDiagnosticsWindow.xaml");

        Assert.Equal(4, CountOccurrences(xaml, "TextBox x:Name="));
        Assert.Contains("ModelRequestTextBox", xaml, StringComparison.Ordinal);
        Assert.Contains("ModelResponseTextBox", xaml, StringComparison.Ordinal);
        Assert.Contains("SerenaRequestTextBox", xaml, StringComparison.Ordinal);
        Assert.Contains("SerenaResponseTextBox", xaml, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string value, string token)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }

        return count;
    }

    private static string ReadRepositoryFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(new[] { directory.FullName }.Concat(segments).ToArray());
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate).ReplaceLineEndings("\n");
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find repository file: {Path.Combine(segments)}");
    }
}
