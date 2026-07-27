# OpenCV YuNet Face Detector

This folder is reserved for the portable OpenCV YuNet ONNX face detector.

The current app looks for:

```text
dependencies/vision/opencv/yunet/face_detection_yunet_2023mar.onnx
dependencies/vision/opencv/yunet/yunet_manifest.json
```

When the ONNX model is present, Avatar Builder uses OpenCV `FaceDetectorYN`/YuNet to locate the face before falling back to Haar cascades. YuNet improves the first step of the tracking pipeline: dynamically finding the face when the camera auto-follows, the user leans back/forward, or the face is not centered.

Keep model/runtime files under `dependencies` so they are copied beside the executable and the app remains portable.
