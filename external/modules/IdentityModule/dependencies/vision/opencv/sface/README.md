# OpenCV SFace model

`face_recognition_sface_2021dec.onnx` is the official OpenCV Zoo SFace face-recognition model used by Avatar Builder's passive people-memory observer.

- Upstream model: `https://github.com/opencv/opencv_zoo/tree/main/models/face_recognition_sface`
- Model file: `face_recognition_sface_2021dec.onnx`
- File size: `38,696,353` bytes
- SHA-256: `0BA9FBFA01B5270C96627C4EF784DA859931E02F04419C829E83484087C34E79`
- License: Apache License 2.0; the upstream `LICENSE` file is preserved beside the model.

Avatar Builder uses YuNet's five measured facial points to perform the same 112-by-112 similarity alignment expected by SFace. Inference attempts OpenCL first and falls back to CPU only inside the independent, lower-priority people-memory lane. The camera presentation path never waits for this model.

The model produces normalized 128-value embeddings. Avatar Builder may persist those embeddings and observation metadata after a sustained coherent encounter, but this module never stores source face images and does not claim identity authentication.
