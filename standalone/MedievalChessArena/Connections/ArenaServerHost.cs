using MedievalChessArena.Chess;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace MedievalChessArena.Connections;

public sealed record ClaimRequest(string Actor, string Side);
public sealed record MoveRequest(string Actor, string Move);

public sealed class ArenaServerHost(ArenaSession session) : IAsyncDisposable
{
    public const int CodexPort = 39464;
    public const int AliPort = 39465;
    public const string McpPath = "/mcp";
    private readonly List<WebApplication> _applications = [];

    public string CodexMcpEndpoint => $"http://127.0.0.1:{CodexPort}{McpPath}";
    public string AliMcpEndpoint => $"http://127.0.0.1:{AliPort}{McpPath}";
    public string McpEndpoint => CodexMcpEndpoint;
    public string ApiEndpoint => $"http://127.0.0.1:{CodexPort}/api";
    public bool IsRunning => _applications.Count == 2;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_applications.Count > 0) return;
        try
        {
            _applications.Add(await StartGateAsync("Codex", CodexPort, exposeApi: true, cancellationToken).ConfigureAwait(false));
            _applications.Add(await StartGateAsync("Ali", AliPort, exposeApi: false, cancellationToken).ConfigureAwait(false));
        }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        var applications = _applications.ToArray();
        _applications.Clear();
        foreach (var app in applications)
        {
            try { await app.StopAsync().ConfigureAwait(false); }
            finally { await app.DisposeAsync().ConfigureAwait(false); }
        }
    }

    private async Task<WebApplication> StartGateAsync(
        string actor,
        int port,
        bool exposeApi,
        CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(ArenaServerHost).Assembly.FullName,
            Args = []
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");

        var tools = CreateMcpTools(actor);
        builder.Services
            .AddMcpServer()
            .WithHttpTransport(options => options.Stateless = true)
            .WithTools(tools);

        var app = builder.Build();
        app.MapGet("/health", () => Results.Json(new
        {
            service = $"Medieval Chess Arena {actor} War Gate",
            actor,
            state = "ready",
            mcp = $"http://127.0.0.1:{port}{McpPath}",
            tools = tools.Count
        }));
        if (exposeApi)
        {
            app.MapGet("/api/state", () => Results.Json(session.GetSnapshot()));
            app.MapPost("/api/claim", (ClaimRequest request) => Results.Json(session.Claim(request.Actor, request.Side)));
            app.MapPost("/api/move", (MoveRequest request) => Results.Json(session.Move(request.Actor, request.Move)));
            app.MapPost("/api/reset", () => Results.Json(session.Reset()));
            app.MapGet("/api/pgn", () => Results.Text(session.ExportPgn(), "text/plain"));
        }
        app.MapMcp(McpPath);
        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        return app;
    }

    private IReadOnlyList<McpServerTool> CreateMcpTools(string actor)
    {
        ArenaSnapshot GetState() => session.GetSnapshot();
        ClaimResult ClaimSide(string side) => session.Claim(actor, side);
        MoveResult MakeMove(string move) => session.Move(actor, move);
        ArenaSnapshot ResetGame() => session.Reset();
        string ExportPgn() => session.ExportPgn();

        var functions = new AIFunction[]
        {
            AIFunctionFactory.Create(
                (Func<ArenaSnapshot>)GetState,
                "chess_get_state",
                $"Return the authoritative board, legal moves, turn, controllers, history, and result. You are {actor}; read this before moving."),
            AIFunctionFactory.Create(
                (Func<string, ClaimResult>)ClaimSide,
                "chess_claim_side",
                $"Claim White or Black as {actor}. This gate cannot impersonate another combatant."),
            AIFunctionFactory.Create(
                (Func<string, MoveResult>)MakeMove,
                "chess_make_move",
                $"Submit one legal move as {actor} using UCI notation such as e2e4, g1f3, or a7a8q. Read chess_get_state first."),
            AIFunctionFactory.Create(
                (Func<ArenaSnapshot>)ResetGame,
                "chess_reset_game",
                "Reset the Medieval Chess Arena to the standard initial position while retaining the selected commanders."),
            AIFunctionFactory.Create(
                (Func<string>)ExportPgn,
                "chess_export_pgn",
                "Return the shared game chronicle as portable PGN text.")
        };
        return functions.Select(function => McpServerTool.Create(function)).ToArray();
    }
}
