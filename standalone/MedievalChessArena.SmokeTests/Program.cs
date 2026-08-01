using MedievalChessArena.Chess;
using MedievalChessArena.Connections;
using ModelContextProtocol.Client;
using System.Net.Http.Json;

var passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException($"FAILED: {name}");
    Console.WriteLine($"PASS: {name}");
    passed++;
}

static MoveResult Move(ChessGame game, string uci)
{
    if (!ChessMove.TryParse(uci, out var move)) throw new InvalidOperationException($"Bad test move {uci}");
    return game.TryMove(move);
}

var opening = new ChessGame();
Check(opening.GetLegalMoves().Count == 20, "initial position has 20 legal moves");
Check(Move(opening, "e2e4").Success, "white can play e2e4");
Check(Move(opening, "e7e5").Success, "black can play e7e5");
Check(Move(opening, "g1f3").Success, "knight can develop");
Check(opening.History[^1].Notation == "Nf3", "knight history uses standard N notation");
Check(opening.Pieces().Count() == 32, "quiet opening retains all pieces");

var foolsMate = new ChessGame();
foreach (var move in new[] { "f2f3", "e7e5", "g2g4", "d8h4" }) Check(Move(foolsMate, move).Success, $"Fool's mate accepts {move}");
Check(foolsMate.Status == GameStatus.Checkmate && foolsMate.Winner == PieceColor.Black, "checkmate and winner are authoritative");

var castle = new ChessGame();
foreach (var move in new[] { "e2e4", "e7e5", "g1f3", "b8c6", "f1e2", "g8f6" }) Check(Move(castle, move).Success, $"castling setup accepts {move}");
Check(Move(castle, "e1g1").Success, "king-side castling is legal");
Check(castle[new Square(6, 0)]?.Kind == PieceKind.King && castle[new Square(5, 0)]?.Kind == PieceKind.Rook, "castling relocates king and rook");

var enPassant = new ChessGame();
foreach (var move in new[] { "e2e4", "a7a6", "e4e5", "d7d5", "e5d6" }) Check(Move(enPassant, move).Success, $"en-passant sequence accepts {move}");
Check(enPassant[new Square(3, 4)] is null && enPassant[new Square(3, 5)]?.Color == PieceColor.White, "en-passant removes the bypassed pawn");

var session = new ArenaSession();
var notificationSession = new ArenaSession();
notificationSession.Changed += (_, _) =>
{
    var reader = Task.Run(notificationSession.GetSnapshot);
    if (!reader.Wait(TimeSpan.FromSeconds(2)))
        throw new InvalidOperationException("Session change notification was raised while the game lock was held.");
};
Check(notificationSession.Claim("Codex", "White").Success, "session change notifications do not hold the game lock");
Check(session.Claim("Codex", "White").Success, "Codex can claim White");
Check(!session.Move("Ali", "e2e4").Success, "wrong controller cannot move Codex's side");
Check(session.Move("Codex", "e2e4").Success, "claimed controller can move its side");

await using (var server = new ArenaServerHost(session))
{
    await server.StartAsync();
    using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:39464") };
    var health = await client.GetFromJsonAsync<Health>("/health");
    Check(health is { Actor: "Codex", State: "ready", Tools: 5 }, "Codex gate reports ready with five actor-bound MCP tools");
    var aliHealth = await client.GetFromJsonAsync<Health>("http://127.0.0.1:39465/health");
    Check(aliHealth is { Actor: "Ali", State: "ready", Tools: 5 }, "Ali gate reports ready with five actor-bound MCP tools");
    var state = await client.GetFromJsonAsync<ArenaSnapshot>("/api/state");
    Check(state is { SideToMove: "Black", WhiteController: "Codex" }, "REST state matches the authoritative session");
    var response = await client.PostAsJsonAsync("/api/move", new MoveRequest("Ali", "e7e5"));
    var remoteMove = await response.Content.ReadFromJsonAsync<MoveResult>();
    Check(remoteMove is { Success: true }, "Ali can move Black over the local API");

    await using var codexTransport = new HttpClientTransport(new HttpClientTransportOptions
    {
        Name = "Medieval Chess Arena Codex smoke client",
        Endpoint = new Uri(server.CodexMcpEndpoint),
        TransportMode = HttpTransportMode.AutoDetect,
        ConnectionTimeout = TimeSpan.FromSeconds(10)
    });
    await using var codexClient = await McpClient.CreateAsync(codexTransport);
    var tools = await codexClient.ListToolsAsync();
    Check(tools.Select(tool => tool.Name).Order().SequenceEqual(new[]
    {
        "chess_claim_side", "chess_export_pgn", "chess_get_state", "chess_make_move", "chess_reset_game"
    }), "MCP client discovers the exact five arena tools");
    var toolResult = await codexClient.CallToolAsync("chess_get_state", new Dictionary<string, object?>());
    Check(toolResult.IsError != true && toolResult.Content.Count > 0, "Codex MCP state tool returns board data");

    await using var aliTransport = new HttpClientTransport(new HttpClientTransportOptions
    {
        Name = "Medieval Chess Arena Ali smoke client",
        Endpoint = new Uri(server.AliMcpEndpoint),
        TransportMode = HttpTransportMode.AutoDetect,
        ConnectionTimeout = TimeSpan.FromSeconds(10)
    });
    await using var aliClient = await McpClient.CreateAsync(aliTransport);
    Check((await aliClient.ListToolsAsync()).Count == 5, "Ali MCP client discovers its separate five-tool gate");
    await codexClient.CallToolAsync("chess_reset_game", new Dictionary<string, object?>());
    await codexClient.CallToolAsync("chess_claim_side", new Dictionary<string, object?> { ["side"] = "White" });
    await aliClient.CallToolAsync("chess_claim_side", new Dictionary<string, object?> { ["side"] = "Black" });
    await codexClient.CallToolAsync("chess_make_move", new Dictionary<string, object?> { ["move"] = "e2e4" });
    Check(session.GetSnapshot() is { SideToMove: "Black", WhiteController: "Codex" }, "Codex gate moves only as Codex");
    await aliClient.CallToolAsync("chess_make_move", new Dictionary<string, object?> { ["move"] = "e7e5" });
    Check(session.GetSnapshot() is { SideToMove: "White", BlackController: "Ali" }, "Ali gate moves only as Ali on the same board");
}

Console.WriteLine($"All {passed} Medieval Chess Arena smoke checks passed.");

internal sealed record Health(string Service, string Actor, string State, string Mcp, int Tools);
