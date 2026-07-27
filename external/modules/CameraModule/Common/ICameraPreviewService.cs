using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace AvatarBuilder.Modules.Webcam.Common;

public interface ICameraPreviewService : IDisposable
{
	bool IsAvailable { get; }

	int MaxOutputWidth { get; set; }

	double MaxOutputFramesPerSecond { get; set; }

	event EventHandler<BitmapSource>? FrameAvailable;

	event EventHandler<CameraFrame>? CameraFrameAvailable;

	event EventHandler<string>? StatusChanged;

	Task<bool> StartAsync(CameraDevice camera, CameraVideoMode? mode, CancellationToken cancellationToken = default(CancellationToken));

	void Stop();
}
