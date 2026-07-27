using System.Collections.Generic;

namespace AvatarBuilder.Modules.Vision.Identity;

/// <summary>
/// Immutable inference evidence accepted by the shared identity policy.
/// Embeddings must use the normalized 128-value SFace representation and the
/// face box must be normalized to the source camera frame.
/// </summary>
public sealed record PersonIdentityEmbeddingObservation(
	IReadOnlyList<float> Embedding,
	double DetectionScore,
	PersonFaceBox FaceBox);
