# IdentityEnrollmentGuidanceModule

An optional, dialog-scoped coordinator for explicit identity enrollment.

- consumes immutable `MediaPipeOutput` through its own one-slot subscription;
- evaluates only MediaPipe face presence and head yaw, pitch, and roll;
- asks the new user to look at the camera, waits for 350 ms of stable requested
  pose, speaks an asynchronous `3, 2, 1` countdown, then requests one capture
  from `IdentityModule`;
- advances only after `IdentityModule` confirms that capture was accepted;
- uses the local Windows SAPI voice on its own STA worker with latest-value
  prompt replacement, never a speech queue;
- never modifies, delays, or back-pressures CameraModule, MediaPipeModule, or
  IdentityModule.
