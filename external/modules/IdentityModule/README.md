# IdentityModule

`IdentityModule` is an independent `IVisionModule` and camera subscriber.
It receives immutable `CameraOutput` objects, performs YuNet detection and
SFace embedding on its own one-in-flight worker, and publishes immutable
`IdentityOutput` objects.

It never waits for MediaPipe, calls camera acquisition, renders UI, or executes
subscriber code. A slow identity inference drops only identity's own arriving
frames. It cannot slow camera display or MediaPipe tracking.

Identity Review can explicitly delete a registered user. Deletion removes the
user row, cascaded face prototypes, active in-memory identity tracks, and the
managed context photo. It does not delete separately owned avatar data.
