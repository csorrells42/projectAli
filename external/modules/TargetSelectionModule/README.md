# TargetSelectionModule

Correlates independently published Identity and MediaPipe observations using
their monotonic capture timestamps and normalized face regions. Initial target
acquisition requires both visual sources to agree. The established UserId is
retained while either visual lock remains, or for a 1.5-second spatially gated
grace period when both are briefly lost. Matching optional speaker evidence can
contribute at most 35 percent of lock quality and can never acquire a target by
itself.

The module publishes immutable latest-value target snapshots and never calls,
modifies, waits for, or back-pressures Identity, MediaPipe, Speaker Recognition,
or any subscriber.
