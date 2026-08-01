using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace MedievalChessArena.Rendering;

internal static class MeshFactory
{
    public static GeometryModel3D Box(double x, double y, double z, double width, double height, double depth, Material material)
    {
        var x0 = x - width / 2; var x1 = x + width / 2;
        var y0 = y; var y1 = y + height;
        var z0 = z - depth / 2; var z1 = z + depth / 2;
        var points = new[]
        {
            new Point3D(x0,y0,z0), new Point3D(x1,y0,z0), new Point3D(x1,y1,z0), new Point3D(x0,y1,z0),
            new Point3D(x0,y0,z1), new Point3D(x1,y0,z1), new Point3D(x1,y1,z1), new Point3D(x0,y1,z1)
        };
        var faces = new[]
        {
            0,2,1, 0,3,2, 4,5,6, 4,6,7,
            0,4,7, 0,7,3, 1,2,6, 1,6,5,
            3,7,6, 3,6,2, 0,1,5, 0,5,4
        };
        var mesh = new MeshGeometry3D { Positions = new Point3DCollection(points), TriangleIndices = new Int32Collection(faces) };
        mesh.Freeze();
        return Model(mesh, material);
    }

    public static GeometryModel3D Cylinder(double radiusBottom, double radiusTop, double height, int segments, Material material, double y = 0)
    {
        var positions = new Point3DCollection();
        var indices = new Int32Collection();
        for (var i = 0; i < segments; i++)
        {
            var angle = Math.PI * 2 * i / segments;
            positions.Add(new Point3D(Math.Cos(angle) * radiusBottom, y, Math.Sin(angle) * radiusBottom));
            positions.Add(new Point3D(Math.Cos(angle) * radiusTop, y + height, Math.Sin(angle) * radiusTop));
        }
        var bottomCenter = positions.Count;
        positions.Add(new Point3D(0, y, 0));
        var topCenter = positions.Count;
        positions.Add(new Point3D(0, y + height, 0));
        for (var i = 0; i < segments; i++)
        {
            var next = (i + 1) % segments;
            var b0 = i * 2; var t0 = b0 + 1; var b1 = next * 2; var t1 = b1 + 1;
            indices.Add(b0); indices.Add(t0); indices.Add(t1);
            indices.Add(b0); indices.Add(t1); indices.Add(b1);
            indices.Add(bottomCenter); indices.Add(b1); indices.Add(b0);
            indices.Add(topCenter); indices.Add(t0); indices.Add(t1);
        }
        var mesh = new MeshGeometry3D { Positions = positions, TriangleIndices = indices };
        mesh.Freeze();
        return Model(mesh, material);
    }

    public static GeometryModel3D Sphere(double radius, Material material, double y, int latitude = 12, int longitude = 18)
    {
        var positions = new Point3DCollection();
        var indices = new Int32Collection();
        for (var lat = 0; lat <= latitude; lat++)
        {
            var phi = Math.PI * lat / latitude;
            for (var lon = 0; lon <= longitude; lon++)
            {
                var theta = 2 * Math.PI * lon / longitude;
                positions.Add(new Point3D(radius * Math.Sin(phi) * Math.Cos(theta), y + radius * Math.Cos(phi), radius * Math.Sin(phi) * Math.Sin(theta)));
            }
        }
        for (var lat = 0; lat < latitude; lat++)
            for (var lon = 0; lon < longitude; lon++)
            {
                var a = lat * (longitude + 1) + lon;
                var b = a + longitude + 1;
                indices.Add(a); indices.Add(b); indices.Add(a + 1);
                indices.Add(a + 1); indices.Add(b); indices.Add(b + 1);
            }
        var mesh = new MeshGeometry3D { Positions = positions, TriangleIndices = indices };
        mesh.Freeze();
        return Model(mesh, material);
    }

    public static Material Material(Color color, Color shine, double specular = 45)
    {
        var group = new MaterialGroup();
        group.Children.Add(new DiffuseMaterial(new SolidColorBrush(color)));
        group.Children.Add(new SpecularMaterial(new SolidColorBrush(shine), specular));
        group.Freeze();
        return group;
    }

    public static GeometryModel3D Model(MeshGeometry3D mesh, Material material) => new(mesh, material) { BackMaterial = material };

    public static Model3DGroup Transform(Model3DGroup group, double x, double z, double scale = 1, double rotation = 0)
    {
        var transforms = new Transform3DGroup();
        transforms.Children.Add(new ScaleTransform3D(scale, scale, scale));
        if (rotation != 0) transforms.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), rotation)));
        transforms.Children.Add(new TranslateTransform3D(x, 0, z));
        group.Transform = transforms;
        return group;
    }
}
