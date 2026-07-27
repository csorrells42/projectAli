using OpenCvSharp;

namespace AvatarBuilder.Modules.Vision.OpenCv;

public sealed record YuNetFaceDetection(Rect FaceBox, Point2f RightEye, Point2f LeftEye, Point2f NoseTip, Point2f RightMouthCorner, Point2f LeftMouthCorner, double Score);
