using MedievalChessArena.Chess;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace MedievalChessArena.Rendering;

internal sealed class BoardScene
{
    private readonly Dictionary<Model3D, Square> _squares = [];
    private readonly Material _lightStone = MeshFactory.Material(Color.FromRgb(178, 151, 105), Color.FromRgb(244, 214, 150), 25);
    private readonly Material _darkStone = MeshFactory.Material(Color.FromRgb(65, 49, 48), Color.FromRgb(128, 88, 65), 25);
    private readonly Material _selected = MeshFactory.Material(Color.FromRgb(57, 121, 110), Color.FromRgb(126, 246, 214), 75);
    private readonly Material _legal = MeshFactory.Material(Color.FromRgb(129, 96, 43), Color.FromRgb(255, 210, 96), 75);
    private readonly Material _frame = MeshFactory.Material(Color.FromRgb(51, 27, 19), Color.FromRgb(165, 103, 49), 65);
    private readonly Material _gold = MeshFactory.Material(Color.FromRgb(167, 112, 31), Color.FromRgb(255, 222, 127), 95);

    public Model3DGroup Root { get; } = new();

    public void Rebuild(ArenaSnapshot snapshot, Square? selected)
    {
        Root.Children.Clear();
        _squares.Clear();
        AddEnvironment();
        var legalDestinations = selected is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : snapshot.LegalMoves.Where(m => m.StartsWith(selected.Value.Algebraic, StringComparison.OrdinalIgnoreCase))
                .Select(m => m.Substring(2, 2)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var rank = 0; rank < 8; rank++)
        for (var file = 0; file < 8; file++)
        {
            var square = new Square(file, rank);
            var material = selected == square ? _selected : legalDestinations.Contains(square.Algebraic)
                ? _legal : (file + rank) % 2 == 0 ? _lightStone : _darkStone;
            var tile = MeshFactory.Box(file - 3.5, 0.02, rank - 3.5, .96, .10, .96, material);
            _squares[tile] = square;
            Root.Children.Add(tile);
        }

        foreach (var piece in snapshot.Pieces)
        {
            if (!Square.TryParse(piece.Square, out var square)
                || !Enum.TryParse<PieceColor>(piece.Color, out var color)
                || !Enum.TryParse<PieceKind>(piece.Kind, out var kind)) continue;
            Root.Children.Add(MedievalPieceFactory.Create(new ChessPiece(color, kind), square.File - 3.5, square.Rank - 3.5));
        }
    }

    public bool TryGetSquare(Model3D? model, out Square square)
    {
        square = default;
        return model is not null && _squares.TryGetValue(model, out square);
    }

    private void AddEnvironment()
    {
        Root.Children.Add(new AmbientLight(Color.FromRgb(78, 69, 65)));
        Root.Children.Add(new DirectionalLight(Color.FromRgb(255, 225, 173), new Vector3D(-1.2, -2.4, -1)));
        Root.Children.Add(new DirectionalLight(Color.FromRgb(91, 120, 163), new Vector3D(1, -1.5, 1.5)));
        Root.Children.Add(MeshFactory.Box(0, -.18, 0, 9.25, .28, 9.25, _frame));
        Root.Children.Add(MeshFactory.Box(0, -.01, -4.34, 9.25, .28, .46, _gold));
        Root.Children.Add(MeshFactory.Box(0, -.01, 4.34, 9.25, .28, .46, _gold));
        Root.Children.Add(MeshFactory.Box(-4.34, -.01, 0, .46, .28, 8.6, _gold));
        Root.Children.Add(MeshFactory.Box(4.34, -.01, 0, .46, .28, 8.6, _gold));

        var towerStone = MeshFactory.Material(Color.FromRgb(83, 72, 68), Color.FromRgb(158, 143, 133), 45);
        foreach (var (x, z) in new[] { (-4.48, -4.48), (4.48, -4.48), (-4.48, 4.48), (4.48, 4.48) })
        {
            Root.Children.Add(Translated(MeshFactory.Cylinder(.47, .40, .72, 12, towerStone), x, z));
            Root.Children.Add(Translated(MeshFactory.Cylinder(.56, .56, .16, 12, _gold, .72), x, z));
        }
    }

    private static Model3D Translated(Model3D model, double x, double z)
    {
        model.Transform = new TranslateTransform3D(x, 0, z);
        return model;
    }
}
