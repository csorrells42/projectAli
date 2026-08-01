using System.Text;

namespace MedievalChessArena.Chess;

public sealed class ChessGame
{
    private readonly ChessPiece?[,] _board = new ChessPiece?[8, 8];
    private bool _whiteKingSide = true;
    private bool _whiteQueenSide = true;
    private bool _blackKingSide = true;
    private bool _blackQueenSide = true;
    private Square? _enPassant;
    private int _halfMoveClock;

    public ChessGame() => Reset();

    private ChessGame(ChessGame source)
    {
        Array.Copy(source._board, _board, source._board.Length);
        SideToMove = source.SideToMove;
        Status = source.Status;
        Winner = source.Winner;
        _whiteKingSide = source._whiteKingSide;
        _whiteQueenSide = source._whiteQueenSide;
        _blackKingSide = source._blackKingSide;
        _blackQueenSide = source._blackQueenSide;
        _enPassant = source._enPassant;
        _halfMoveClock = source._halfMoveClock;
    }

    public PieceColor SideToMove { get; private set; }
    public GameStatus Status { get; private set; }
    public PieceColor? Winner { get; private set; }
    public List<MoveRecord> History { get; } = [];

    public ChessPiece? this[Square square] => square.IsValid ? _board[square.Rank, square.File] : null;

    public void Reset()
    {
        Array.Clear(_board);
        var back = new[] { PieceKind.Rook, PieceKind.Knight, PieceKind.Bishop, PieceKind.Queen, PieceKind.King, PieceKind.Bishop, PieceKind.Knight, PieceKind.Rook };
        for (var file = 0; file < 8; file++)
        {
            _board[0, file] = new ChessPiece(PieceColor.White, back[file]);
            _board[1, file] = new ChessPiece(PieceColor.White, PieceKind.Pawn);
            _board[6, file] = new ChessPiece(PieceColor.Black, PieceKind.Pawn);
            _board[7, file] = new ChessPiece(PieceColor.Black, back[file]);
        }
        SideToMove = PieceColor.White;
        Status = GameStatus.Active;
        Winner = null;
        _whiteKingSide = _whiteQueenSide = _blackKingSide = _blackQueenSide = true;
        _enPassant = null;
        _halfMoveClock = 0;
        History.Clear();
    }

    public IEnumerable<(Square Square, ChessPiece Piece)> Pieces()
    {
        for (var rank = 0; rank < 8; rank++)
            for (var file = 0; file < 8; file++)
                if (_board[rank, file] is { } piece)
                    yield return (new Square(file, rank), piece);
    }

    public IReadOnlyList<ChessMove> GetLegalMoves() => GetLegalMoves(SideToMove);

    public IReadOnlyList<ChessMove> GetLegalMoves(PieceColor color)
    {
        var result = new List<ChessMove>();
        foreach (var (square, piece) in Pieces().Where(item => item.Piece.Color == color))
        {
            foreach (var move in GetPseudoMoves(square, piece, includeCastling: true))
            {
                var clone = new ChessGame(this);
                clone.ApplyUnchecked(move, switchTurn: false);
                if (!clone.IsInCheck(color)) result.Add(move);
            }
        }
        return result;
    }

    public MoveResult TryMove(ChessMove requested, string whiteController = "Human", string blackController = "Human", long version = 0)
    {
        if (Status is GameStatus.Checkmate or GameStatus.Stalemate or GameStatus.Draw)
            return new MoveResult(false, "The battle is already decided.", Snapshot(whiteController, blackController, version));

        var legal = GetLegalMoves();
        var move = legal.FirstOrDefault(candidate => candidate.From == requested.From
            && candidate.To == requested.To
            && candidate.Promotion == requested.Promotion);
        if (!legal.Contains(move))
            return new MoveResult(false, $"{requested.Uci} is not a legal move for {SideToMove}.", Snapshot(whiteController, blackController, version));

        var moving = this[move.From]!.Value;
        var captured = this[move.To] is not null || (moving.Kind == PieceKind.Pawn && move.From.File != move.To.File);
        ApplyUnchecked(move, switchTurn: true);
        var inCheck = IsInCheck(SideToMove);
        var replyMoves = GetLegalMoves();
        if (replyMoves.Count == 0)
        {
            if (inCheck)
            {
                Status = GameStatus.Checkmate;
                Winner = Opponent(SideToMove);
            }
            else Status = GameStatus.Stalemate;
        }
        else if (_halfMoveClock >= 100 || IsInsufficientMaterial()) Status = GameStatus.Draw;
        else Status = inCheck ? GameStatus.Check : GameStatus.Active;

        var notation = BuildNotation(moving, move, captured, inCheck, Status == GameStatus.Checkmate);
        History.Add(new MoveRecord((History.Count / 2) + 1, moving.Color, move, notation, captured, inCheck));
        return new MoveResult(true, $"{moving.Color} {moving.Kind} marched {move.From} to {move.To}.", Snapshot(whiteController, blackController, version));
    }

    public ArenaSnapshot Snapshot(string whiteController, string blackController, long version) => new(
        SideToMove.ToString(),
        Status.ToString(),
        Winner?.ToString(),
        whiteController,
        blackController,
        Pieces().Select(item => new PieceSnapshot(item.Square.Algebraic, item.Piece.Color.ToString(), item.Piece.Kind.ToString())).ToArray(),
        GetLegalMoves().Select(move => move.Uci).OrderBy(text => text).ToArray(),
        History.Select(item => $"{item.Number}{(item.Color == PieceColor.White ? "." : "...")} {item.Notation}").ToArray(),
        version);

    public string ExportPgn()
    {
        var builder = new StringBuilder();
        builder.AppendLine("[Event \"Medieval Chess Arena\"]");
        builder.AppendLine($"[Date \"{DateTime.Now:yyyy.MM.dd}\"]");
        builder.AppendLine($"[Result \"{ResultCode()}\"]");
        builder.AppendLine();
        for (var index = 0; index < History.Count; index++)
        {
            var move = History[index];
            if (move.Color == PieceColor.White) builder.Append($"{move.Number}. ");
            builder.Append(move.Notation).Append(' ');
        }
        builder.Append(ResultCode());
        return builder.ToString();
    }

    private IEnumerable<ChessMove> GetPseudoMoves(Square from, ChessPiece piece, bool includeCastling)
    {
        return piece.Kind switch
        {
            PieceKind.Pawn => PawnMoves(from, piece.Color),
            PieceKind.Knight => JumpMoves(from, piece.Color, [(1, 2), (2, 1), (-1, 2), (-2, 1), (1, -2), (2, -1), (-1, -2), (-2, -1)]),
            PieceKind.Bishop => SlideMoves(from, piece.Color, [(1, 1), (1, -1), (-1, 1), (-1, -1)]),
            PieceKind.Rook => SlideMoves(from, piece.Color, [(1, 0), (-1, 0), (0, 1), (0, -1)]),
            PieceKind.Queen => SlideMoves(from, piece.Color, [(1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1)]),
            PieceKind.King => KingMoves(from, piece.Color, includeCastling),
            _ => []
        };
    }

    private IEnumerable<ChessMove> PawnMoves(Square from, PieceColor color)
    {
        var direction = color == PieceColor.White ? 1 : -1;
        var startRank = color == PieceColor.White ? 1 : 6;
        var promotionRank = color == PieceColor.White ? 7 : 0;
        var one = new Square(from.File, from.Rank + direction);
        if (one.IsValid && this[one] is null)
        {
            foreach (var move in Promote(from, one, promotionRank)) yield return move;
            var two = new Square(from.File, from.Rank + (2 * direction));
            if (from.Rank == startRank && this[two] is null) yield return new ChessMove(from, two);
        }
        foreach (var fileOffset in new[] { -1, 1 })
        {
            var target = new Square(from.File + fileOffset, from.Rank + direction);
            if (!target.IsValid) continue;
            if (this[target] is { } occupant && occupant.Color != color || _enPassant == target)
                foreach (var move in Promote(from, target, promotionRank)) yield return move;
        }
    }

    private static IEnumerable<ChessMove> Promote(Square from, Square to, int promotionRank)
    {
        if (to.Rank != promotionRank) return [new ChessMove(from, to)];
        return new[] { PieceKind.Queen, PieceKind.Rook, PieceKind.Bishop, PieceKind.Knight }
            .Select(kind => new ChessMove(from, to, kind));
    }

    private IEnumerable<ChessMove> JumpMoves(Square from, PieceColor color, (int File, int Rank)[] offsets)
    {
        foreach (var offset in offsets)
        {
            var target = new Square(from.File + offset.File, from.Rank + offset.Rank);
            if (target.IsValid && (this[target] is null || this[target]!.Value.Color != color)) yield return new ChessMove(from, target);
        }
    }

    private IEnumerable<ChessMove> SlideMoves(Square from, PieceColor color, (int File, int Rank)[] directions)
    {
        foreach (var direction in directions)
        {
            var target = new Square(from.File + direction.File, from.Rank + direction.Rank);
            while (target.IsValid)
            {
                if (this[target] is null) yield return new ChessMove(from, target);
                else
                {
                    if (this[target]!.Value.Color != color) yield return new ChessMove(from, target);
                    break;
                }
                target = new Square(target.File + direction.File, target.Rank + direction.Rank);
            }
        }
    }

    private IEnumerable<ChessMove> KingMoves(Square from, PieceColor color, bool includeCastling)
    {
        foreach (var move in JumpMoves(from, color, [(1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1)])) yield return move;
        if (!includeCastling || IsInCheck(color)) yield break;
        var rank = color == PieceColor.White ? 0 : 7;
        var kingSide = color == PieceColor.White ? _whiteKingSide : _blackKingSide;
        var queenSide = color == PieceColor.White ? _whiteQueenSide : _blackQueenSide;
        if (from == new Square(4, rank) && kingSide && this[new Square(5, rank)] is null && this[new Square(6, rank)] is null
            && !IsSquareAttacked(new Square(5, rank), Opponent(color)) && !IsSquareAttacked(new Square(6, rank), Opponent(color)))
            yield return new ChessMove(from, new Square(6, rank));
        if (from == new Square(4, rank) && queenSide && this[new Square(1, rank)] is null && this[new Square(2, rank)] is null && this[new Square(3, rank)] is null
            && !IsSquareAttacked(new Square(3, rank), Opponent(color)) && !IsSquareAttacked(new Square(2, rank), Opponent(color)))
            yield return new ChessMove(from, new Square(2, rank));
    }

    private bool IsInCheck(PieceColor color)
    {
        var king = Pieces().FirstOrDefault(item => item.Piece.Color == color && item.Piece.Kind == PieceKind.King);
        return king.Piece.Kind == PieceKind.King && IsSquareAttacked(king.Square, Opponent(color));
    }

    private bool IsSquareAttacked(Square square, PieceColor byColor)
    {
        foreach (var (from, piece) in Pieces().Where(item => item.Piece.Color == byColor))
        {
            if (piece.Kind == PieceKind.Pawn)
            {
                var direction = byColor == PieceColor.White ? 1 : -1;
                if (Math.Abs(square.File - from.File) == 1 && square.Rank - from.Rank == direction) return true;
                continue;
            }
            if (GetPseudoMoves(from, piece, includeCastling: false).Any(move => move.To == square)) return true;
        }
        return false;
    }

    private void ApplyUnchecked(ChessMove move, bool switchTurn)
    {
        var piece = this[move.From]!.Value;
        var target = this[move.To];
        if (piece.Kind == PieceKind.Pawn && _enPassant == move.To && target is null)
            _board[move.From.Rank, move.To.File] = null;
        if (piece.Kind == PieceKind.King && Math.Abs(move.To.File - move.From.File) == 2)
        {
            var rookFrom = new Square(move.To.File == 6 ? 7 : 0, move.From.Rank);
            var rookTo = new Square(move.To.File == 6 ? 5 : 3, move.From.Rank);
            _board[rookTo.Rank, rookTo.File] = _board[rookFrom.Rank, rookFrom.File];
            _board[rookFrom.Rank, rookFrom.File] = null;
        }
        _board[move.To.Rank, move.To.File] = piece.Kind == PieceKind.Pawn && move.To.Rank is 0 or 7
            ? new ChessPiece(piece.Color, move.Promotion)
            : piece;
        _board[move.From.Rank, move.From.File] = null;
        UpdateCastlingRights(piece, move.From, move.To);
        _enPassant = piece.Kind == PieceKind.Pawn && Math.Abs(move.To.Rank - move.From.Rank) == 2
            ? new Square(move.From.File, (move.From.Rank + move.To.Rank) / 2)
            : null;
        _halfMoveClock = piece.Kind == PieceKind.Pawn || target is not null ? 0 : _halfMoveClock + 1;
        if (switchTurn) SideToMove = Opponent(SideToMove);
    }

    private void UpdateCastlingRights(ChessPiece piece, Square from, Square to)
    {
        if (piece.Kind == PieceKind.King)
        {
            if (piece.Color == PieceColor.White) _whiteKingSide = _whiteQueenSide = false;
            else _blackKingSide = _blackQueenSide = false;
        }
        if (from == new Square(0, 0) || to == new Square(0, 0)) _whiteQueenSide = false;
        if (from == new Square(7, 0) || to == new Square(7, 0)) _whiteKingSide = false;
        if (from == new Square(0, 7) || to == new Square(0, 7)) _blackQueenSide = false;
        if (from == new Square(7, 7) || to == new Square(7, 7)) _blackKingSide = false;
    }

    private bool IsInsufficientMaterial()
    {
        var material = Pieces().Where(item => item.Piece.Kind != PieceKind.King).Select(item => item.Piece.Kind).ToArray();
        return material.Length == 0 || (material.Length == 1 && material[0] is PieceKind.Bishop or PieceKind.Knight);
    }

    private static string BuildNotation(ChessPiece piece, ChessMove move, bool capture, bool check, bool mate)
    {
        if (piece.Kind == PieceKind.King && Math.Abs(move.To.File - move.From.File) == 2) return move.To.File == 6 ? "O-O" : "O-O-O";
        var prefix = piece.Kind == PieceKind.Pawn
            ? (capture ? ((char)('a' + move.From.File)).ToString() : string.Empty)
            : NotationLetter(piece.Kind);
        var promotion = piece.Kind == PieceKind.Pawn && move.To.Rank is 0 or 7
            ? $"={NotationLetter(move.Promotion)}"
            : string.Empty;
        return $"{prefix}{(capture ? "x" : string.Empty)}{move.To}{promotion}{(mate ? "#" : check ? "+" : string.Empty)}";
    }

    private static string NotationLetter(PieceKind kind) => kind switch
    {
        PieceKind.King => "K",
        PieceKind.Queen => "Q",
        PieceKind.Rook => "R",
        PieceKind.Bishop => "B",
        PieceKind.Knight => "N",
        _ => string.Empty
    };

    private static PieceColor Opponent(PieceColor color) => color == PieceColor.White ? PieceColor.Black : PieceColor.White;
    private string ResultCode() => Status == GameStatus.Checkmate ? Winner == PieceColor.White ? "1-0" : "0-1" : Status is GameStatus.Draw or GameStatus.Stalemate ? "1/2-1/2" : "*";
}
