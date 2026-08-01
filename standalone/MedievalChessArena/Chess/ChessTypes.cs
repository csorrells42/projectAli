namespace MedievalChessArena.Chess;

public enum PieceColor { White, Black }
public enum PieceKind { Pawn, Knight, Bishop, Rook, Queen, King }
public enum GameStatus { Active, Check, Checkmate, Stalemate, Draw }

public readonly record struct Square(int File, int Rank)
{
    public bool IsValid => File is >= 0 and < 8 && Rank is >= 0 and < 8;
    public string Algebraic => $"{(char)('a' + File)}{Rank + 1}";

    public static bool TryParse(string? value, out Square square)
    {
        square = default;
        if (value is null || value.Length != 2) return false;
        var file = char.ToLowerInvariant(value[0]) - 'a';
        var rank = value[1] - '1';
        square = new Square(file, rank);
        return square.IsValid;
    }

    public override string ToString() => Algebraic;
}

public readonly record struct ChessPiece(PieceColor Color, PieceKind Kind)
{
    public string Code => $"{(Color == PieceColor.White ? 'w' : 'b')}{Kind}";
}

public readonly record struct ChessMove(Square From, Square To, PieceKind Promotion = PieceKind.Queen)
{
    public string Uci => $"{From}{To}{(Promotion == PieceKind.Queen ? string.Empty : Promotion.ToString()[0].ToString().ToLowerInvariant())}";

    public static bool TryParse(string? value, out ChessMove move)
    {
        move = default;
        var text = value?.Trim().ToLowerInvariant().Replace("-", string.Empty) ?? string.Empty;
        if (text.Length is not (4 or 5)
            || !Square.TryParse(text[..2], out var from)
            || !Square.TryParse(text.Substring(2, 2), out var to)) return false;
        var promotion = text.Length == 5 ? text[4] switch
        {
            'n' => PieceKind.Knight,
            'b' => PieceKind.Bishop,
            'r' => PieceKind.Rook,
            _ => PieceKind.Queen
        } : PieceKind.Queen;
        move = new ChessMove(from, to, promotion);
        return true;
    }
}

public sealed record MoveRecord(int Number, PieceColor Color, ChessMove Move, string Notation, bool WasCapture, bool GaveCheck);

public sealed record PieceSnapshot(string Square, string Color, string Kind);

public sealed record ArenaSnapshot(
    string SideToMove,
    string Status,
    string? Winner,
    string WhiteController,
    string BlackController,
    IReadOnlyList<PieceSnapshot> Pieces,
    IReadOnlyList<string> LegalMoves,
    IReadOnlyList<string> MoveHistory,
    long Version);

public sealed record MoveResult(bool Success, string Message, ArenaSnapshot State);
public sealed record ClaimResult(bool Success, string Message, ArenaSnapshot State);
