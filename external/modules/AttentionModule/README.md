# AttentionModule

Consumes only immutable `MediaPipeOutput`. It uses MediaPipe face presence and
head yaw, pitch, and roll, then requires 250 ms of stable evidence before the
public attention state changes. Pitch accepts up to 30 degrees so a user can
look naturally at a 27-inch monitor beneath a top-mounted webcam; yaw remains
limited to 22 degrees and roll to 24 degrees. It performs no iris tracking.
