using MedievalChessArena.Chess;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace MedievalChessArena.Rendering;

internal static class MedievalPieceFactory
{
    public static Model3DGroup Create(ChessPiece piece, double x, double z)
    {
        var ivory = MeshFactory.Material(Color.FromRgb(224, 205, 160), Color.FromRgb(255, 244, 202), 70);
        var onyx = MeshFactory.Material(Color.FromRgb(35, 31, 37), Color.FromRgb(172, 62, 47), 80);
        var gold = MeshFactory.Material(Color.FromRgb(181, 127, 38), Color.FromRgb(255, 221, 124), 95);
        var steel = MeshFactory.Material(Color.FromRgb(73, 72, 78), Color.FromRgb(190, 190, 205), 75);
        var body = piece.Color == PieceColor.White ? ivory : onyx;
        var trim = piece.Color == PieceColor.White ? gold : steel;
        var group = new Model3DGroup();
        AddBase(group, body, trim);
        switch (piece.Kind)
        {
            case PieceKind.Pawn: Pawn(group, body, trim); break;
            case PieceKind.Rook: Rook(group, body, trim); break;
            case PieceKind.Knight: Knight(group, body, trim, piece.Color); break;
            case PieceKind.Bishop: Bishop(group, body, trim); break;
            case PieceKind.Queen: Queen(group, body, trim); break;
            case PieceKind.King: King(group, body, trim); break;
        }
        return MeshFactory.Transform(group, x, z, 0.78, piece.Color == PieceColor.Black ? 180 : 0);
    }

    private static void AddBase(Model3DGroup group, Material body, Material trim)
    {
        group.Children.Add(MeshFactory.Cylinder(.42, .36, .12, 24, trim));
        group.Children.Add(MeshFactory.Cylinder(.34, .27, .14, 24, body, .12));
    }

    private static void Pawn(Model3DGroup g, Material b, Material t)
    {
        g.Children.Add(MeshFactory.Cylinder(.22, .14, .48, 20, b, .24));
        g.Children.Add(MeshFactory.Sphere(.18, b, .83));
        g.Children.Add(MeshFactory.Cylinder(.19, .19, .045, 20, t, .67));
    }

    private static void Rook(Model3DGroup g, Material b, Material t)
    {
        g.Children.Add(MeshFactory.Cylinder(.27, .23, .65, 20, b, .24));
        g.Children.Add(MeshFactory.Cylinder(.34, .34, .13, 8, t, .89));
        for (var i = 0; i < 4; i++)
        {
            var angle = i * Math.PI / 2;
            g.Children.Add(MeshFactory.Box(Math.Cos(angle) * .25, 1.02, Math.Sin(angle) * .25, .18, .18, .18, b));
        }
    }

    private static void Knight(Model3DGroup g, Material b, Material t, PieceColor color)
    {
        g.Children.Add(MeshFactory.Cylinder(.25, .16, .48, 20, b, .24));
        var neck = MeshFactory.Box(0, .62, -.04, .30, .48, .28, b);
        neck.Transform = new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(1, 0, 0), -18), new Point3D(0, .62, 0));
        g.Children.Add(neck);
        g.Children.Add(MeshFactory.Sphere(.24, b, 1.10));
        g.Children.Add(MeshFactory.Cylinder(.075, 0, .28, 10, t, 1.26));
        g.Children.Add(MeshFactory.Box(0, 1.12, -.20, .16, .18, .34, b));
    }

    private static void Bishop(Model3DGroup g, Material b, Material t)
    {
        g.Children.Add(MeshFactory.Cylinder(.25, .12, .76, 24, b, .24));
        g.Children.Add(MeshFactory.Sphere(.20, b, 1.08));
        g.Children.Add(MeshFactory.Cylinder(.08, 0, .22, 12, t, 1.28));
    }

    private static void Queen(Model3DGroup g, Material b, Material t)
    {
        g.Children.Add(MeshFactory.Cylinder(.28, .14, .86, 24, b, .24));
        g.Children.Add(MeshFactory.Cylinder(.28, .28, .09, 16, t, 1.10));
        for (var i = 0; i < 8; i++)
        {
            var angle = i * Math.PI / 4;
            var spike = MeshFactory.Cylinder(.055, 0, .27, 10, t, 1.18);
            spike.Transform = new TranslateTransform3D(Math.Cos(angle) * .22, 0, Math.Sin(angle) * .22);
            g.Children.Add(spike);
        }
        g.Children.Add(MeshFactory.Sphere(.10, t, 1.40));
    }

    private static void King(Model3DGroup g, Material b, Material t)
    {
        g.Children.Add(MeshFactory.Cylinder(.29, .15, .92, 24, b, .24));
        g.Children.Add(MeshFactory.Cylinder(.24, .24, .10, 16, t, 1.16));
        g.Children.Add(MeshFactory.Box(0, 1.27, 0, .09, .42, .09, t));
        g.Children.Add(MeshFactory.Box(0, 1.39, 0, .30, .09, .09, t));
    }
}
