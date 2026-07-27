using System;

namespace AvatarBuilder.Modules.Vision.Common;

public interface IStatefulFaceLandmarkTracker : IFaceLandmarkTracker, IDisposable
{
	void Reset();
}
