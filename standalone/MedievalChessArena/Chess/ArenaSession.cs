namespace MedievalChessArena.Chess;

public sealed class ArenaSession
{
    private readonly object _gate = new();
    private ChessGame _game = new();
    private long _version;
    private string _whiteController = "Human";
    private string _blackController = "Ali";

    public event EventHandler? Changed;

    public ArenaSnapshot GetSnapshot()
    {
        lock (_gate) return _game.Snapshot(_whiteController, _blackController, _version);
    }

    public ChessGame GetGameForDisplay()
    {
        lock (_gate) return _game;
    }

    public ClaimResult Claim(string actor, string side)
    {
        actor = NormalizeController(actor);
        if (!Enum.TryParse<PieceColor>(side, true, out var color))
            return new ClaimResult(false, "Side must be White or Black.", GetSnapshot());

        ClaimResult result;
        lock (_gate)
        {
            if (color == PieceColor.White) _whiteController = actor;
            else _blackController = actor;
            _version++;
            result = new ClaimResult(true, $"{actor} now commands the {color} host.", _game.Snapshot(_whiteController, _blackController, _version));
        }


        RaiseChanged();
        return result;
    }

    public MoveResult Move(string actor, string uci)
    {
        actor = NormalizeController(actor);
        MoveResult result;
        lock (_gate)
        {
            var expected = _game.SideToMove == PieceColor.White ? _whiteController : _blackController;
            if (!string.Equals(expected, actor, StringComparison.OrdinalIgnoreCase) && !string.Equals(actor, "Human", StringComparison.OrdinalIgnoreCase))
                return new MoveResult(false, $"It is {_game.SideToMove}'s turn, commanded by {expected}.", _game.Snapshot(_whiteController, _blackController, _version));
            if (!ChessMove.TryParse(uci, out var move))
                return new MoveResult(false, "Use coordinate notation such as e2e4 or a7a8q.", _game.Snapshot(_whiteController, _blackController, _version));
            result = _game.TryMove(move, _whiteController, _blackController, _version + 1);
            if (result.Success)
            {
                _version++;
                result = result with { State = _game.Snapshot(_whiteController, _blackController, _version) };
            }
        }


        if (result.Success) RaiseChanged();
        return result;
    }

    public ArenaSnapshot Reset()
    {
        ArenaSnapshot snapshot;
        lock (_gate)
        {
            _game = new ChessGame();
            _version++;
            snapshot = _game.Snapshot(_whiteController, _blackController, _version);
        }


        RaiseChanged();
        return snapshot;
    }

    public string ExportPgn()
    {
        lock (_gate) return _game.ExportPgn();
    }

    private static string NormalizeController(string? value) => string.IsNullOrWhiteSpace(value) ? "Remote" : value.Trim() switch
    {
        var text when text.Equals("codex", StringComparison.OrdinalIgnoreCase) => "Codex",
        var text when text.Equals("ali", StringComparison.OrdinalIgnoreCase) => "Ali",
        var text when text.Equals("human", StringComparison.OrdinalIgnoreCase) => "Human",
        var text => text
    };

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
