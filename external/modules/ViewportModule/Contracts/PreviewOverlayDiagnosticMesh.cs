using System.Collections.Generic;

namespace AvatarBuilder.Modules.Viewports.Contracts;

public sealed record PreviewOverlayDiagnosticMesh(IReadOnlyList<PreviewOverlayPoint> Points, IReadOnlyList<PreviewOverlayEdge> Edges, PreviewOverlayDiagnosticMeshRole Role, bool DrawPoints = true);
