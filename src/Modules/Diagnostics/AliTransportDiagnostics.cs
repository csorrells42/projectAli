using System.Text;

namespace Ali.Modules.Diagnostics;

public sealed record AliTransportDiagnosticsSnapshot(
    string ModelRequest,
    string ModelResponse,
    string SerenaRequest,
    string SerenaResponse);

public static class AliTransportDiagnostics
{
    private static readonly object ModelResponseSync = new();
    private static readonly StringBuilder ModelResponseBuffer = new();
    private static string _modelRequest = string.Empty;
    private static string _serenaRequest = string.Empty;
    private static string _serenaResponse = string.Empty;

    public static void RecordModelRequest(string rawPayload)
    {
        Interlocked.Exchange(ref _modelRequest, rawPayload ?? string.Empty);
        lock (ModelResponseSync)
        {
            ModelResponseBuffer.Clear();
        }
    }

    public static void RecordModelResponse(string rawPayload)
    {
        lock (ModelResponseSync)
        {
            ModelResponseBuffer.Clear();
            ModelResponseBuffer.Append(rawPayload ?? string.Empty);
        }
    }

    public static void AppendModelResponseLine(string rawLine)
    {
        lock (ModelResponseSync)
        {
            if (ModelResponseBuffer.Length > 0)
            {
                ModelResponseBuffer.AppendLine();
            }

            ModelResponseBuffer.Append(rawLine);
        }
    }

    public static void RecordSerenaRequest(string rawPayload) =>
        Interlocked.Exchange(ref _serenaRequest, rawPayload ?? string.Empty);

    public static void RecordSerenaResponse(string rawPayload) =>
        Interlocked.Exchange(ref _serenaResponse, rawPayload ?? string.Empty);

    public static AliTransportDiagnosticsSnapshot Capture()
    {
        string modelResponse;
        lock (ModelResponseSync)
        {
            modelResponse = ModelResponseBuffer.ToString();
        }

        return new AliTransportDiagnosticsSnapshot(
            Volatile.Read(ref _modelRequest),
            modelResponse,
            Volatile.Read(ref _serenaRequest),
            Volatile.Read(ref _serenaResponse));
    }
}
