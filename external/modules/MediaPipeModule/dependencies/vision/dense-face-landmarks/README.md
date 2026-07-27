# Dense Face Landmark Backend

This folder is reserved for the portable dense face-landmark model bundle.

The current app looks for:

```text
dependencies/vision/dense-face-landmarks/face_landmarker.task
dependencies/vision/dense-face-landmarks/face_landmarker_manifest.json
```

The target model is Google's MediaPipe Face Landmarker task bundle:

```text
https://storage.googleapis.com/mediapipe-models/face_landmarker/face_landmarker/float16/latest/face_landmarker.task
```

The target backend is a local face-landmark model that can output a dense face mesh, eye landmarks, lip/jaw landmarks, blendshape-style cue scores, head pose, and tracking confidence. Until both the model file and a real local inference runtime are present, Avatar Builder uses the OpenCV LBF and aperture fallback backends. A model file by itself is not enough to make dense tracking active.

Keep model/runtime files under `dependencies` so they are copied beside the executable and the app remains portable.
