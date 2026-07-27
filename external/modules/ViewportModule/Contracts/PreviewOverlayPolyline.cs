using System.Collections.Generic;

namespace AvatarBuilder.Modules.Viewports.Contracts;

public sealed record PreviewOverlayPolyline(IReadOnlyList<PreviewOverlayPoint> Points, bool Closed, bool Inferred = false);
