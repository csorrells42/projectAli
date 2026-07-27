using System.Collections.Generic;

namespace AvatarBuilder.Modules.Viewports.Contracts;

public sealed record PreviewOverlayIndexedPath(IReadOnlyList<int> PointIndices, bool Closed, PreviewOverlayMeshFeatureRole Role);
