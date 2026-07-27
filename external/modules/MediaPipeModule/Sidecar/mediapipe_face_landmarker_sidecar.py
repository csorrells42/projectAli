import argparse
import json
import mmap
import sys
import time
import traceback
import zipfile


def load_runtime(model_path):
    import cv2
    import mediapipe as mp
    import numpy as np
    from mediapipe.tasks import python
    from mediapipe.tasks.python import vision

    delegate = python.BaseOptions.Delegate.CPU
    base_options = python.BaseOptions(model_asset_path=model_path, delegate=delegate)
    options = vision.FaceLandmarkerOptions(
        base_options=base_options,
        running_mode=vision.RunningMode.VIDEO,
        num_faces=1,
        min_face_detection_confidence=0.30,
        min_face_presence_confidence=0.30,
        min_tracking_confidence=0.30,
        output_face_blendshapes=True,
        output_facial_transformation_matrixes=True,
    )
    landmarker = vision.FaceLandmarker.create_from_options(options)
    with zipfile.ZipFile(model_path, "r") as task_bundle:
        detector_model = task_bundle.read("face_detector.tflite")
    detector_options = vision.FaceDetectorOptions(
        base_options=python.BaseOptions(
            model_asset_buffer=detector_model,
            delegate=delegate,
        ),
        running_mode=vision.RunningMode.VIDEO,
        min_detection_confidence=0.30,
    )
    detector = vision.FaceDetector.create_from_options(detector_options)
    return mp, cv2, np, detector, landmarker


class SharedMemoryFrameReader:
    def __init__(self):
        self._mapping = None
        self._mapping_name = ""
        self._capacity_bytes = 0
        self._image_byte_length = 0
        self._width = 0
        self._height = 0
        self._stride = 0
        self._pixel_format = ""

    def read_image(self, mp, cv2, np, request):
        name = request.get("sharedMemoryName")
        if name:
            capacity_bytes = int(request.get("sharedMemoryCapacityBytes", 0))
            image_byte_length = int(request.get("imageByteLength", 0))
            width = int(request.get("imageWidth", 0))
            height = int(request.get("imageHeight", 0))
            stride = int(request.get("imageStride", 0))
            pixel_format = str(request.get("imagePixelFormat", ""))
            if capacity_bytes <= 0 or capacity_bytes > 512 * 1024 * 1024:
                raise ValueError(f"invalid shared memory capacity: {capacity_bytes}")
            self._ensure_mapping(str(name), capacity_bytes)
            self._image_byte_length = image_byte_length
            self._width = width
            self._height = height
            self._stride = stride
            self._pixel_format = pixel_format
        elif self._mapping is None:
            raise ValueError("shared memory transport is not initialized")
        else:
            capacity_bytes = self._capacity_bytes
            image_byte_length = self._image_byte_length
            width = self._width
            height = self._height
            stride = self._stride
            pixel_format = self._pixel_format

        if width <= 0 or height <= 0 or stride < width * 4:
            raise ValueError(f"invalid BGRA dimensions: {width}x{height}, stride {stride}")
        if image_byte_length != stride * height or image_byte_length > capacity_bytes:
            raise ValueError(
                f"invalid shared image length: {image_byte_length}, expected {stride * height}"
            )
        if pixel_format.upper() != "BGRA32":
            raise ValueError(f"unsupported shared image format: {pixel_format}")

        pixel_rows = np.ndarray(
            shape=(height, stride),
            dtype=np.uint8,
            buffer=self._mapping,
            offset=0,
        )
        bgra = pixel_rows[:, : width * 4].reshape((height, width, 4))
        rgb = cv2.cvtColor(bgra, cv2.COLOR_BGRA2RGB)
        return mp.Image(image_format=mp.ImageFormat.SRGB, data=rgb)

    def close(self):
        if self._mapping is not None:
            self._mapping.close()
            self._mapping = None
        self._mapping_name = ""
        self._capacity_bytes = 0
        self._image_byte_length = 0
        self._width = 0
        self._height = 0
        self._stride = 0
        self._pixel_format = ""

    def _ensure_mapping(self, name, capacity_bytes):
        if (
            self._mapping is not None
            and self._mapping_name == name
            and self._capacity_bytes == capacity_bytes
        ):
            return

        self.close()
        self._mapping = mmap.mmap(
            -1,
            capacity_bytes,
            tagname=name,
            access=mmap.ACCESS_READ,
        )
        self._mapping_name = name
        self._capacity_bytes = capacity_bytes


class SharedMemoryLandmarkWriter:
    MAXIMUM_LANDMARK_COUNT = 512
    VALUES_PER_LANDMARK = 3
    MAXIMUM_TRANSFORMATION_MATRIX_VALUE_COUNT = 16
    TRANSFORMATION_MATRIX_OFFSET_BYTES = (
        MAXIMUM_LANDMARK_COUNT * VALUES_PER_LANDMARK * 4
    )

    def __init__(self):
        self._mapping = None
        self._mapping_name = ""
        self._capacity_bytes = 0

    def configure(self, request):
        name = request.get("landmarkSharedMemoryName")
        if name:
            capacity_bytes = int(request.get("landmarkSharedMemoryCapacityBytes", 0))
            required_bytes = (
                self.TRANSFORMATION_MATRIX_OFFSET_BYTES
                + self.MAXIMUM_TRANSFORMATION_MATRIX_VALUE_COUNT * 4
            )
            if required_bytes > capacity_bytes or capacity_bytes <= 0 or capacity_bytes > 1024 * 1024:
                raise ValueError(f"invalid landmark shared memory capacity: {capacity_bytes}")
            self._ensure_mapping(str(name), capacity_bytes)
        elif self._mapping is None:
            raise ValueError("landmark shared memory transport is not initialized")

    def write(self, np, landmarks, transformation_matrix):
        if self._mapping is None:
            raise ValueError("landmark shared memory transport is not initialized")
        required_bytes = (
            self.TRANSFORMATION_MATRIX_OFFSET_BYTES
            + self.MAXIMUM_TRANSFORMATION_MATRIX_VALUE_COUNT * 4
        )
        if required_bytes > self._capacity_bytes:
            raise ValueError(f"invalid landmark shared memory capacity: {self._capacity_bytes}")
        if len(landmarks) > self.MAXIMUM_LANDMARK_COUNT:
            raise ValueError(f"invalid landmark count: {len(landmarks)}")

        values = np.ndarray(
            shape=(len(landmarks), self.VALUES_PER_LANDMARK),
            dtype=np.float32,
            buffer=self._mapping,
            offset=0,
        )
        for index, landmark in enumerate(landmarks):
            values[index, 0] = landmark.x
            values[index, 1] = landmark.y
            values[index, 2] = landmark.z

        if transformation_matrix is None:
            return 0
        matrix_values = np.asarray(transformation_matrix, dtype=np.float32).reshape(-1)
        if len(matrix_values) > self.MAXIMUM_TRANSFORMATION_MATRIX_VALUE_COUNT:
            raise ValueError(
                f"invalid transformation matrix value count: {len(matrix_values)}"
            )
        matrix_target = np.ndarray(
            shape=(len(matrix_values),),
            dtype=np.float32,
            buffer=self._mapping,
            offset=self.TRANSFORMATION_MATRIX_OFFSET_BYTES,
        )
        matrix_target[:] = matrix_values
        return len(matrix_values)

    def close(self):
        if self._mapping is not None:
            self._mapping.close()
            self._mapping = None
        self._mapping_name = ""
        self._capacity_bytes = 0

    def _ensure_mapping(self, name, capacity_bytes):
        if (
            self._mapping is not None
            and self._mapping_name == name
            and self._capacity_bytes == capacity_bytes
        ):
            return

        self.close()
        self._mapping = mmap.mmap(
            -1,
            capacity_bytes,
            tagname=name,
            access=mmap.ACCESS_WRITE,
        )
        self._mapping_name = name
        self._capacity_bytes = capacity_bytes


def handle_request(
    mp,
    cv2,
    np,
    detector,
    landmarker,
    frame_reader,
    landmark_writer,
    request,
):
    collect_diagnostics = bool(request.get("collectDiagnostics", False))
    total_started = time.perf_counter() if collect_diagnostics else 0.0
    request_id = request.get("requestId", "")
    transport_started = time.perf_counter() if collect_diagnostics else 0.0
    image = frame_reader.read_image(mp, cv2, np, request)
    landmark_writer.configure(request)
    transport_ms = elapsed_ms(transport_started) if collect_diagnostics else 0.0
    inference_started = time.perf_counter() if collect_diagnostics else 0.0
    timestamp_ms = int(request.get("timestampMilliseconds", 0))
    detection_result = detector.detect_for_video(image, timestamp_ms)
    result = landmarker.detect_for_video(image, timestamp_ms)
    inference_ms = elapsed_ms(inference_started) if collect_diagnostics else 0.0
    face_box = {}
    if detection_result.detections:
        detection = detection_result.detections[0]
        bounding_box = detection.bounding_box
        face_box = {
            "faceBoxX": float(bounding_box.origin_x) / image.width,
            "faceBoxY": float(bounding_box.origin_y) / image.height,
            "faceBoxWidth": float(bounding_box.width) / image.width,
            "faceBoxHeight": float(bounding_box.height) / image.height,
        }
        if detection.categories:
            face_box["faceDetectionScore"] = float(
                detection.categories[0].score
            )
    if not result.face_landmarks:
        response = {
            "requestId": request_id,
            "ok": True,
            "hasFace": False,
            "landmarkCount": 0,
        }
        response.update(face_box)
        if collect_diagnostics:
            response["timingsMilliseconds"] = {
                "decode": transport_ms,
                "sharedMemoryRead": transport_ms,
                "inference": inference_ms,
                "total": elapsed_ms(total_started),
            }
        return response

    face_landmarks = result.face_landmarks[0]
    facial_transformation_matrix = (
        result.facial_transformation_matrixes[0]
        if result.facial_transformation_matrixes
        else None
    )
    facial_transformation_matrix_count = landmark_writer.write(
        np,
        face_landmarks,
        facial_transformation_matrix,
    )
    eye_blink_left = None
    eye_blink_right = None
    jaw_open = None
    mouth_close = None
    if result.face_blendshapes:
        found_blendshapes = 0
        for category in result.face_blendshapes[0]:
            name = category.category_name
            if name == "eyeBlinkLeft":
                eye_blink_left = float(category.score)
                found_blendshapes += 1
            elif name == "eyeBlinkRight":
                eye_blink_right = float(category.score)
                found_blendshapes += 1
            elif name == "jawOpen":
                jaw_open = float(category.score)
                found_blendshapes += 1
            elif name == "mouthClose":
                mouth_close = float(category.score)
                found_blendshapes += 1
            if found_blendshapes == 4:
                break
    response = {
        "requestId": request_id,
        "ok": True,
        "hasFace": True,
        "landmarkCount": len(face_landmarks),
        "eyeBlinkLeft": eye_blink_left,
        "eyeBlinkRight": eye_blink_right,
        "jawOpen": jaw_open,
        "mouthClose": mouth_close,
        "facialTransformationMatrixCount": facial_transformation_matrix_count,
    }
    response.update(face_box)
    if collect_diagnostics:
        response["timingsMilliseconds"] = {
            "decode": transport_ms,
            "sharedMemoryRead": transport_ms,
            "inference": inference_ms,
            "total": elapsed_ms(total_started),
        }
    return response


def elapsed_ms(started):
    return round((time.perf_counter() - started) * 1000.0, 4)


def write_response(response):
    sys.stdout.write(json.dumps(response, separators=(",", ":")) + "\n")
    sys.stdout.flush()


def main():
    parser = argparse.ArgumentParser(description="Avatar Builder MediaPipe Face Landmarker sidecar")
    parser.add_argument("--model", required=True, help="Path to face_landmarker.task")
    parser.add_argument("--probe", action="store_true")
    args = parser.parse_args()

    try:
        mp, cv2, np, detector, landmarker = load_runtime(args.model)
    except Exception as exc:
        sys.stderr.write(f"MediaPipe sidecar startup failed: {exc}\n")
        sys.stderr.write(traceback.format_exc())
        sys.stderr.flush()
        return 3

    if args.probe:
        detector.close()
        landmarker.close()
        write_response(
            {
                "ok": True,
                "delegate": "cpu",
                "status": "MediaPipe CPU delegate ready",
            }
        )
        return 0

    frame_reader = SharedMemoryFrameReader()
    landmark_writer = SharedMemoryLandmarkWriter()
    try:
        for line in sys.stdin:
            line = line.strip()
            if not line:
                continue

            request_id = ""
            try:
                request = json.loads(line)
                request_id = request.get("requestId", "")
                write_response(
                    handle_request(
                        mp,
                        cv2,
                        np,
                        detector,
                        landmarker,
                        frame_reader,
                        landmark_writer,
                        request,
                    )
                )
            except Exception as exc:
                write_response(
                    {
                        "requestId": request_id,
                        "ok": False,
                        "hasFace": False,
                        "status": f"MediaPipe sidecar request failed: {exc}",
                        "landmarkCount": 0,
                    }
                )
    finally:
        landmark_writer.close()
        frame_reader.close()
        detector.close()
        landmarker.close()

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
