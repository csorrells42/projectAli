namespace AvatarBuilder.Modules.Vision.MediaPipe;

internal readonly record struct MediaPipeSharedMemoryFrameDescriptor(string Name, long CapacityBytes, int ImageByteLength, int Width, int Height, int Stride, string PixelFormat);
