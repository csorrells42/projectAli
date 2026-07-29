using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using OpenCvSharp;

namespace AvatarBuilder.Modules.Vision.Identity;

/// <summary>
/// Recognizes explicitly enrolled users and keeps short-lived spatial tracks.
/// Passive camera observations never create or persist a person. Enrollment
/// is deliberate, multi-angle, and stores one user-approved context photo.
/// </summary>
public sealed class PersonIdentityMemory :
	IDisposable,
	IPersonIdentityReviewService
{
	private const int MaximumFacesPerObservation = 8;

	private const int MaximumPrototypesPerIdentity = 12;

	private const double KnownIdentitySimilarity = 0.50d;

	private const double RegisteredUserSimilarity = 0.60d;

	private const double RegisteredUserMinimumMatchMargin = 0.12d;

	private const double StrongUnknownMaximumSimilarity = 0.42d;

	private const double UsableEvidenceDetectionScore = 0.82d;

	private const int UsableUnknownObservations = 3;

	private const int RegisteredUserConfirmationObservations = 6;

	private const double TemporaryTrackSimilarity = 0.42d;

	private static readonly TimeSpan TemporaryTrackLifetime =
		TimeSpan.FromSeconds(20);

	private static readonly TimeSpan NewEncounterGap =
		TimeSpan.FromMinutes(2);

	private static readonly TimeSpan PersistenceInterval =
		TimeSpan.FromSeconds(15);

	private static readonly TimeSpan ActiveIdentityMaximumAge =
		TimeSpan.FromSeconds(3);

	private static readonly TimeSpan ConfirmedTrackContinuityMaximumAge =
		TimeSpan.FromSeconds(1);

	private static readonly TimeSpan RegisteredUserConfirmationDuration =
		TimeSpan.FromMilliseconds(500);

	private static readonly TimeSpan RegisteredUserConfirmationMaximumGap =
		TimeSpan.FromMilliseconds(500);

	private static readonly TimeSpan UsableUnknownConfirmationDuration =
		TimeSpan.FromMilliseconds(250);

	private static readonly string[] EnrollmentPrompts =
	[
		"Look directly into the camera.",
		"Turn your head slightly to your left.",
		"Turn your head slightly to your right.",
		"Raise your chin slightly.",
		"Lower your chin slightly."
	];

	private readonly object _stateLock = new();

	private readonly object _inferenceLock = new();

	private readonly PersonIdentityMemoryStore _store = new();

	private readonly OpenCv.OpenCvYuNetFaceDetector _faceDetector = new();

	private readonly SFaceEmbeddingExtractor? _embeddingExtractor;

	private readonly string _externalBackendName = "";

	private readonly List<PersonIdentityRecord> _rememberedPeople = [];

	private readonly List<TrackState> _activeTracks = [];

	private readonly HashSet<string> _dirtyIdentityIds =
		new(StringComparer.OrdinalIgnoreCase);

	private string _outputFolder = "";

	private DateTime _lastSavedAtUtc = DateTime.MinValue;

	private PersonIdentitySnapshot _latestSnapshot =
		PersonIdentitySnapshot.Waiting;

	private string _initializationStatus;

	private bool _enrollmentAvailable;

	private EnrollmentSession? _enrollment;

	private IdentityEnrollmentState _enrollmentState =
		IdentityEnrollmentState.Unavailable(
			"Identity enrollment is not initialized.");

	private bool _disposed;

	public event EventHandler<PersonIdentitySnapshot>? SnapshotChanged;

	public bool IsAvailable => !string.IsNullOrWhiteSpace(_externalBackendName)
		|| (_embeddingExtractor is not null && _faceDetector.IsAvailable);

	public string Status
	{
		get
		{
			lock (_stateLock)
			{
				return _latestSnapshot == PersonIdentitySnapshot.Waiting
					? _initializationStatus
					: _latestSnapshot.Status;
			}
		}
	}

	public PersonIdentitySnapshot LatestSnapshot
	{
		get
		{
			lock (_stateLock)
			{
				return _latestSnapshot;
			}
		}
	}

	public IReadOnlyList<PersonIdentityReviewItem> GetIdentityReviewItems()
	{
		lock (_stateLock)
		{
			return _rememberedPeople
				.OrderByDescending(person => person.LastSeenAtUtc)
				.Select(person => new PersonIdentityReviewItem(
					person.Id,
					person.DisplayName,
					person.FirstName,
					person.LastName,
					person.Username,
					person.Email,
					person.PhoneNumber,
					person.Address,
					string.IsNullOrWhiteSpace(_outputFolder)
						? ""
						: _store.GetContextPhotoPath(
							_outputFolder,
							person.Id),
					person.IsRegisteredUser,
					NormalizePermission(person.PermissionLevel),
					person.FirstSeenAtUtc,
					person.LastSeenAtUtc,
					person.ObservationCount,
					person.EncounterCount))
				.ToArray();
		}
	}

	public IdentityReviewUpdateResult UpdateIdentityReview(
		IdentityReviewUpdate update)
	{
		ArgumentNullException.ThrowIfNull(update);
		ArgumentException.ThrowIfNullOrWhiteSpace(update.IdentityId);
		string firstName = update.FirstName.Trim();
		string lastName = update.LastName.Trim();
		string username = update.Username.Trim();
		if (update.RegisterAsUser
			&& (string.IsNullOrWhiteSpace(firstName)
				|| string.IsNullOrWhiteSpace(lastName)
				|| string.IsNullOrWhiteSpace(username)))
		{
			return new IdentityReviewUpdateResult(
				false,
				"Registered users require first name, last name, and username.");
		}
		lock (_stateLock)
		{
			PersonIdentityRecord? person =
				_rememberedPeople.FirstOrDefault(candidate =>
					string.Equals(
						candidate.Id,
						update.IdentityId,
						StringComparison.OrdinalIgnoreCase));
			if (person is null)
			{
				return new IdentityReviewUpdateResult(
					false,
					"That identity is no longer available.");
			}
			if (update.RegisterAsUser
				&& _rememberedPeople.Any(candidate =>
					!ReferenceEquals(candidate, person)
					&& candidate.IsRegisteredUser
					&& string.Equals(
						candidate.Username,
						username,
						StringComparison.OrdinalIgnoreCase)))
			{
				return new IdentityReviewUpdateResult(
					false,
					$"Username '{username}' is already assigned.");
			}
			person.FirstName = firstName;
			person.LastName = lastName;
			person.Username = username;
			person.Email = update.Email.Trim();
			person.PhoneNumber = update.PhoneNumber.Trim();
			person.Address = update.Address.Trim();
			if (!string.IsNullOrWhiteSpace(firstName)
				|| !string.IsNullOrWhiteSpace(lastName))
			{
				person.DisplayName =
					(firstName + " " + lastName).Trim();
			}
			person.IsRegisteredUser = update.RegisterAsUser;
			person.PermissionLevel =
				update.RegisterAsUser
					? NormalizePermission(update.PermissionLevel)
					: "Default User";
			_dirtyIdentityIds.Add(person.Id);
			SaveIfDirtyLocked(force: true, DateTime.UtcNow);
			return new IdentityReviewUpdateResult(
				true,
				$"{person.DisplayName} saved.");
		}
	}

	public IdentityReviewUpdateResult ReplaceContextPhoto(
		string identityId,
		ReadOnlyMemory<byte> jpegBytes)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(identityId);
		if (jpegBytes.IsEmpty)
		{
			return new IdentityReviewUpdateResult(
				false,
				"The replacement photo is empty.");
		}
		lock (_stateLock)
		{
			PersonIdentityRecord? person =
				_rememberedPeople.FirstOrDefault(candidate =>
					string.Equals(
						candidate.Id,
						identityId,
						StringComparison.OrdinalIgnoreCase));
			if (person is null)
			{
				return new IdentityReviewUpdateResult(
					false,
					"That identity is no longer available.");
			}
			if (string.IsNullOrWhiteSpace(_outputFolder))
			{
				return new IdentityReviewUpdateResult(
					false,
					"Identity photo storage is not configured.");
			}
			try
			{
				_store.SaveContextPhoto(
					_outputFolder,
					person.Id,
					jpegBytes.Span);
				return new IdentityReviewUpdateResult(
					true,
					$"{person.DisplayName}'s context photo was replaced.");
			}
			catch (Exception exception)
			{
				return new IdentityReviewUpdateResult(
					false,
					"Replacement photo could not be saved: "
					+ exception.Message);
			}
		}
	}

	public IdentityReviewUpdateResult DeleteIdentity(string identityId)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(identityId);
		lock (_stateLock)
		{
			PersonIdentityRecord? person =
				_rememberedPeople.FirstOrDefault(candidate =>
					string.Equals(
						candidate.Id,
						identityId,
						StringComparison.OrdinalIgnoreCase));
			if (person is null)
			{
				return new IdentityReviewUpdateResult(
					false,
					"That identity is no longer available.");
			}
			if (string.IsNullOrWhiteSpace(_outputFolder))
			{
				return new IdentityReviewUpdateResult(
					false,
					"Identity storage is not configured.");
			}
			try
			{
				_store.Delete(_outputFolder, person.Id);
				_rememberedPeople.Remove(person);
				_activeTracks.RemoveAll(track =>
					string.Equals(
						track.IdentityId,
						person.Id,
						StringComparison.OrdinalIgnoreCase));
				_dirtyIdentityIds.Remove(person.Id);
				_latestSnapshot = new PersonIdentitySnapshot(
					DateTime.UtcNow,
					Array.Empty<PersonIdentityObservation>(),
					_rememberedPeople.Count,
					BackendName,
					$"{person.DisplayName} was deleted");
				return new IdentityReviewUpdateResult(
					true,
					$"{person.DisplayName} and the linked identity "
					+ "enrollment data were deleted.");
			}
			catch (Exception exception)
			{
				return new IdentityReviewUpdateResult(
					false,
					"Identity could not be deleted: "
					+ exception.Message);
			}
		}
	}

	public IdentityReviewUpdateResult BeginEnrollment(
		IdentityEnrollmentRequest request)
	{
		ArgumentNullException.ThrowIfNull(request);
		string firstName = request.FirstName.Trim();
		string lastName = request.LastName.Trim();
		string username = request.Username.Trim();
		if (string.IsNullOrWhiteSpace(firstName)
			|| string.IsNullOrWhiteSpace(lastName)
			|| string.IsNullOrWhiteSpace(username))
		{
			return new IdentityReviewUpdateResult(
				false,
				"Enrollment requires first name, last name, and username.");
		}
		lock (_stateLock)
		{
			if (!_enrollmentAvailable)
			{
				return new IdentityReviewUpdateResult(
					false,
					"Identity enrollment is unavailable: "
					+ _initializationStatus);
			}
			if (string.IsNullOrWhiteSpace(_outputFolder))
			{
				return new IdentityReviewUpdateResult(
					false,
					"Identity storage is not configured.");
			}
			PersonIdentityRecord? existingProfile = _rememberedPeople
				.FirstOrDefault(person =>
					person.IsRegisteredUser
					&& string.Equals(
						person.Username,
						username,
						StringComparison.OrdinalIgnoreCase));
			if (existingProfile is not null
				&& existingProfile.Prototypes.Count > 0)
			{
				return new IdentityReviewUpdateResult(
					false,
					$"Username '{username}' is already assigned.");
			}
			var normalizedRequest = request with
			{
				FirstName = firstName,
				LastName = lastName,
				Username = username,
				Email = request.Email.Trim(),
				PhoneNumber = request.PhoneNumber.Trim(),
				Address = request.Address.Trim(),
				PermissionLevel = NormalizePermission(
					request.PermissionLevel)
			};
			_enrollment = new EnrollmentSession(
				normalizedRequest,
				existingProfile?.Id);
			_enrollmentState = CreateEnrollmentState(
				_enrollment,
				existingProfile is null
					? "Enrollment ready. " + EnrollmentPrompts[0]
					: "Camera enrollment will be added to the existing profile. "
						+ EnrollmentPrompts[0]);
			return new IdentityReviewUpdateResult(
				true,
				_enrollmentState.Status);
		}
	}

	public IdentityReviewUpdateResult CreateUserProfile(
		IdentityEnrollmentRequest request)
	{
		ArgumentNullException.ThrowIfNull(request);
		string firstName = request.FirstName.Trim();
		string lastName = request.LastName.Trim();
		string username = request.Username.Trim();
		if (string.IsNullOrWhiteSpace(firstName)
			|| string.IsNullOrWhiteSpace(lastName)
			|| string.IsNullOrWhiteSpace(username))
		{
			return new IdentityReviewUpdateResult(
				false,
				"User profiles require first name, last name, and username.");
		}
		lock (_stateLock)
		{
			if (string.IsNullOrWhiteSpace(_outputFolder))
			{
				return new IdentityReviewUpdateResult(false, "Identity storage is not configured.");
			}
			if (_rememberedPeople.Any(person => person.IsRegisteredUser
				&& string.Equals(person.Username, username, StringComparison.OrdinalIgnoreCase)))
			{
				return new IdentityReviewUpdateResult(false, $"Username '{username}' is already assigned.");
			}
			DateTime now = DateTime.UtcNow;
			var person = new PersonIdentityRecord
			{
				Id = "person-" + Guid.NewGuid().ToString("N"),
				DisplayName = (firstName + " " + lastName).Trim(),
				FirstName = firstName,
				LastName = lastName,
				Username = username,
				Email = request.Email.Trim(),
				PhoneNumber = request.PhoneNumber.Trim(),
				Address = request.Address.Trim(),
				IsRegisteredUser = true,
				PermissionLevel = NormalizePermission(request.PermissionLevel),
				FirstSeenAtUtc = now,
				LastSeenAtUtc = now,
				ObservationCount = 0,
				EncounterCount = 0,
				Prototypes = []
			};
			try
			{
				_store.Upsert(_outputFolder, [person]);
				_rememberedPeople.Add(person);
				return new IdentityReviewUpdateResult(
					true,
					$"{person.DisplayName} was created without camera enrollment.");
			}
			catch (Exception exception)
			{
				return new IdentityReviewUpdateResult(false, "User profile could not be saved: " + exception.Message);
			}
		}
	}

	public IdentityReviewUpdateResult RequestEnrollmentCapture()
	{
		lock (_stateLock)
		{
			if (_enrollment is null)
			{
				return new IdentityReviewUpdateResult(
					false,
					"Start a new user enrollment first.");
			}
			if (_enrollment.CapturePending)
			{
				return new IdentityReviewUpdateResult(
					false,
					"A capture is already waiting for the next identity frame.");
			}
			_enrollment.CapturePending = true;
			_enrollmentState = CreateEnrollmentState(
				_enrollment,
				"Capturing the next frame. Hold still.");
			return new IdentityReviewUpdateResult(
				true,
				_enrollmentState.Status);
		}
	}

	public IdentityEnrollmentState GetEnrollmentState()
	{
		lock (_stateLock)
		{
			return _enrollmentState;
		}
	}

	public void CancelEnrollment()
	{
		lock (_stateLock)
		{
			_enrollment = null;
			_enrollmentState = _enrollmentAvailable
				? ReadyEnrollmentState("Enrollment cancelled.")
				: IdentityEnrollmentState.Unavailable(
					_initializationStatus);
		}
	}

	public PersonIdentityMemory()
		: this(initializeModels: true)
	{
	}

	/// <summary>
	/// Creates identity policy and persistence for an external inference
	/// backend. The caller supplies normalized SFace-compatible embeddings and
	/// measured YuNet face boxes; this class remains the single owner of all
	/// enrollment, matching, confirmation, and persistence rules.
	/// </summary>
	public PersonIdentityMemory(string externalBackendName)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(externalBackendName);
		_externalBackendName = externalBackendName.Trim();
		_initializationStatus = $"{_externalBackendName} people memory ready";
		_enrollmentAvailable = true;
		_enrollmentState = ReadyEnrollmentState(
			"Identity enrollment ready.");
	}

	internal PersonIdentityMemory(bool initializeModels)
	{
		if (!initializeModels)
		{
			_initializationStatus = "People memory self-test";
			_enrollmentAvailable = true;
			_enrollmentState = ReadyEnrollmentState(
				"Identity enrollment self-test ready.");
			return;
		}
		FaceIdentityModelInfo model = FaceIdentityModelInfo.Load();
		if (!model.IsReady)
		{
			_initializationStatus = model.Status;
			return;
		}
		try
		{
			_embeddingExtractor =
				new SFaceEmbeddingExtractor(model.ModelPath);
			_enrollmentAvailable = true;
			_initializationStatus =
				$"{_embeddingExtractor.BackendName} people memory ready";
			_enrollmentState = ReadyEnrollmentState(
				"Identity enrollment ready.");
		}
		catch (Exception ex)
		{
			_initializationStatus =
				"People memory unavailable: " + ex.Message;
		}
	}

	internal PersonIdentitySnapshot ObserveEmbeddingFrameForSelfTest(
		IReadOnlyList<float[]> embeddings,
		DateTime capturedAtUtc)
	{
		var samples = new List<FaceSample>(embeddings.Count);
		for (int index = 0; index < embeddings.Count; index++)
		{
			double left = 0.08d + index * 0.11d;
			samples.Add(new FaceSample(
				embeddings[index].ToArray(),
				0.96d,
				new PersonFaceBox(
					left,
					0.12d,
					Math.Min(0.96d, left + 0.09d),
					0.88d)));
		}
		lock (_stateLock)
		{
			_latestSnapshot = UpdateMemoryLocked(
				samples,
				capturedAtUtc,
				() => [0xff, 0xd8, 0xff, 0xd9]);
			return _latestSnapshot;
		}
	}

	/// <summary>
	/// Applies one externally inferred frame without rerunning face detection or
	/// embedding inference. Samples and embeddings are copied before entering
	/// identity state so inference cannot mutate published evidence afterward.
	/// </summary>
	public PersonIdentitySnapshot ObserveEmbeddingFrame(
		IReadOnlyList<PersonIdentityEmbeddingObservation> observations,
		DateTime capturedAtUtc,
		Func<byte[]>? contextPhotoFactory = null)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		ArgumentNullException.ThrowIfNull(observations);
		if (string.IsNullOrWhiteSpace(_externalBackendName))
		{
			throw new InvalidOperationException(
				"External observations require an external inference backend.");
		}
		var samples = new List<FaceSample>(
			Math.Min(observations.Count, MaximumFacesPerObservation));
		foreach (PersonIdentityEmbeddingObservation observation in observations
			.Take(MaximumFacesPerObservation))
		{
			if (observation.Embedding.Count
					!= SFaceEmbeddingExtractor.ExpectedEmbeddingLength
				|| observation.Embedding.Any(value => !float.IsFinite(value)))
			{
				continue;
			}
			float[] embedding = observation.Embedding.ToArray();
			if (!NormalizeEmbeddingInPlace(embedding))
			{
				continue;
			}
			samples.Add(new FaceSample(
				embedding,
				Math.Clamp(observation.DetectionScore, 0d, 1d),
				observation.FaceBox));
		}
		PersonIdentitySnapshot snapshot;
		lock (_stateLock)
		{
			snapshot = UpdateMemoryLocked(
				samples,
				capturedAtUtc == default ? DateTime.UtcNow : capturedAtUtc,
				contextPhotoFactory);
			_latestSnapshot = snapshot;
		}
		try
		{
			SnapshotChanged?.Invoke(this, snapshot);
		}
		catch
		{
		}
		return snapshot;
	}

	public void ConfigureOutputFolder(string outputFolder)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		ArgumentException.ThrowIfNullOrWhiteSpace(outputFolder);
		lock (_stateLock)
		{
			SaveIfDirtyLocked(force: true, DateTime.UtcNow);
			_outputFolder = outputFolder;
			_rememberedPeople.Clear();
			_rememberedPeople.AddRange(_store.Load(outputFolder));
			_activeTracks.Clear();
			_enrollment = null;
			_enrollmentState = _enrollmentAvailable
				? ReadyEnrollmentState("Identity enrollment ready.")
				: IdentityEnrollmentState.Unavailable(
					_initializationStatus);
			_dirtyIdentityIds.Clear();
			_lastSavedAtUtc = DateTime.UtcNow;
			_latestSnapshot = new PersonIdentitySnapshot(
				DateTime.MinValue,
				Array.Empty<PersonIdentityObservation>(),
				_rememberedPeople.Count,
				BackendName,
				_initializationStatus);
		}
	}

	public void ObserveBgra(
		byte[] bgraPixels,
		int width,
		int height,
		int stride,
		DateTime capturedAtUtc)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		SFaceEmbeddingExtractor? extractor = _embeddingExtractor;
		if (extractor is null
			|| !_faceDetector.IsAvailable
			|| width <= 0
			|| height <= 0
			|| stride < width * 4
			|| bgraPixels.Length < stride * height)
		{
			return;
		}

		lock (_inferenceLock)
		{
			using Mat bgra = Mat.FromPixelData(
				height,
				width,
				MatType.CV_8UC4,
				bgraPixels,
				stride);
			using Mat bgr = new();
			Cv2.CvtColor(bgra, bgr, ColorConversionCodes.BGRA2BGR);
			ObserveBgrLocked(
				bgr,
				extractor,
				capturedAtUtc == default
					? DateTime.UtcNow
					: capturedAtUtc);
		}
	}

	public void ObserveBgr(
		Mat bgrFrame,
		DateTime capturedAtUtc)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		SFaceEmbeddingExtractor? extractor = _embeddingExtractor;
		if (extractor is null
			|| !_faceDetector.IsAvailable
			|| bgrFrame.Empty()
			|| bgrFrame.Channels() != 3)
		{
			return;
		}
		lock (_inferenceLock)
		{
			ObserveBgrLocked(
				bgrFrame,
				extractor,
				capturedAtUtc == default
					? DateTime.UtcNow
					: capturedAtUtc);
		}
	}

	private void ObserveBgrLocked(
		Mat sourceBgr,
		SFaceEmbeddingExtractor extractor,
		DateTime capturedAtUtc)
	{
		using Mat resizedBgr = new();
		Mat observationBgr = sourceBgr;
		const int maximumObservationDimension = 960;
		int sourceDimension = Math.Max(sourceBgr.Width, sourceBgr.Height);
		if (sourceDimension > maximumObservationDimension)
		{
			double scale =
				(double)maximumObservationDimension /
				sourceDimension;
			Cv2.Resize(
				sourceBgr,
				resizedBgr,
				new Size(
					Math.Max(1, (int)Math.Round(sourceBgr.Width * scale)),
					Math.Max(1, (int)Math.Round(sourceBgr.Height * scale))));
			observationBgr = resizedBgr;
		}
		List<FaceSample> samples = [];
		IReadOnlyList<OpenCv.YuNetFaceDetection> faces =
			_faceDetector.DetectAll(observationBgr);
		foreach (OpenCv.YuNetFaceDetection face in faces
			.Where(face => face.Score >= 0.72d)
			.Take(MaximumFacesPerObservation))
		{
			if (!extractor.TryExtract(
				observationBgr,
				face,
				out float[] embedding))
			{
				continue;
			}
			samples.Add(new FaceSample(
				embedding,
				face.Score,
				ToNormalizedBox(
					face.FaceBox,
					observationBgr.Width,
					observationBgr.Height)));
		}
		var contextPhoto = new Lazy<byte[]>(() =>
		{
			Cv2.ImEncode(
				".jpg",
				sourceBgr,
				out byte[] jpeg,
				[(int)ImwriteFlags.JpegQuality, 92]);
			return jpeg;
		});
		PersonIdentitySnapshot snapshot;
		lock (_stateLock)
		{
			snapshot = UpdateMemoryLocked(
				samples,
				capturedAtUtc,
				() => contextPhoto.Value);
			_latestSnapshot = snapshot;
		}
		try
		{
			SnapshotChanged?.Invoke(this, snapshot);
		}
		catch
		{
		}
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		lock (_stateLock)
		{
			SaveIfDirtyLocked(force: true, DateTime.UtcNow);
			_activeTracks.Clear();
		}
		lock (_inferenceLock)
		{
			_embeddingExtractor?.Dispose();
			_faceDetector.Dispose();
		}
	}

	private string BackendName => _embeddingExtractor?.BackendName
		?? _externalBackendName;

	private static bool NormalizeEmbeddingInPlace(float[] values)
	{
		double squaredNorm = 0d;
		foreach (float value in values)
		{
			squaredNorm += value * value;
		}
		double norm = Math.Sqrt(squaredNorm);
		if (!double.IsFinite(norm) || norm < 1e-8d)
		{
			return false;
		}
		float inverseNorm = (float)(1d / norm);
		for (int index = 0; index < values.Length; index++)
		{
			values[index] *= inverseNorm;
		}
		return true;
	}

	private PersonIdentitySnapshot UpdateMemoryLocked(
		IReadOnlyList<FaceSample> samples,
		DateTime capturedAtUtc,
		Func<byte[]>? contextPhotoFactory = null)
	{
		PruneExpiredTracksLocked(capturedAtUtc);
		ProcessPendingEnrollmentCaptureLocked(
			samples,
			capturedAtUtc,
			contextPhotoFactory);
		var usedTracks = new HashSet<string>(
			StringComparer.OrdinalIgnoreCase);
		var usedIdentities = new HashSet<string>(
			StringComparer.OrdinalIgnoreCase);
		var observations = new List<PersonIdentityObservation>(samples.Count);
		foreach (FaceSample sample in samples)
		{
			KnownMatch registeredUser = FindKnownMatchLocked(
				sample.Embedding,
				usedIdentities,
				registeredUsersOnly: true);
			TrackState track;
			PersonIdentityRecord? person = null;
			double similarity = Math.Clamp(
				registeredUser.BestSimilarity,
				0d,
				1d);
			bool positivelyIdentified = false;
			if (registeredUser.Person is PersonIdentityRecord recognizedUser
				&& registeredUser.BestSimilarity >= RegisteredUserSimilarity
				&& registeredUser.BestSimilarity
					- registeredUser.SecondSimilarity
					>= RegisteredUserMinimumMatchMargin)
			{
				person = recognizedUser;
				similarity = registeredUser.BestSimilarity;
				track = FindOrCreateKnownTrackLocked(
					recognizedUser,
					sample,
					capturedAtUtc,
					usedTracks);
			}
			else
			{
				TrackState? confirmedTrack =
					FindContinuousConfirmedTrackLocked(
						sample,
						capturedAtUtc,
						usedTracks);
				if (confirmedTrack is not null)
				{
					track = confirmedTrack;
					person = _rememberedPeople.FirstOrDefault(candidate =>
						string.Equals(
							candidate.Id,
							track.IdentityId,
							StringComparison.OrdinalIgnoreCase));
					similarity = person is null
						? 0d
						: MaximumSimilarity(
							sample.Embedding,
							person.Prototypes);
				}
				else
				{
					track = FindOrCreateTemporaryTrackLocked(
						sample,
						capturedAtUtc,
						usedTracks);
				}
			}

			track.Update(
				sample,
				capturedAtUtc);
			if (person?.IsRegisteredUser == true)
			{
				bool isUnambiguousBestMatch =
					registeredUser.Person is not null
					&& string.Equals(
						registeredUser.Person.Id,
						person.Id,
						StringComparison.OrdinalIgnoreCase)
					&& registeredUser.BestSimilarity
						- registeredUser.SecondSimilarity
						>= RegisteredUserMinimumMatchMargin;
				positivelyIdentified =
					similarity >= RegisteredUserSimilarity
						&& isUnambiguousBestMatch
						? track.ObserveStrongRegisteredUserMatch(
							person.Id,
							capturedAtUtc)
						: false;
			}
			usedTracks.Add(track.TrackId);
			if (person is not null)
			{
				track.IdentityId = person.Id;
				usedIdentities.Add(person.Id);
				UpdateRememberedPersonLocked(
					person,
					sample,
					capturedAtUtc,
					similarity >= RegisteredUserSimilarity);
				if (similarity <= 0d)
				{
					similarity = MaximumSimilarity(
						sample.Embedding,
						person.Prototypes);
				}
			}
			PersonIdentityEvidenceState evidenceState = positivelyIdentified
				? PersonIdentityEvidenceState.ConfirmedRegisteredUser
				: HasUsableUnknownEvidence(
					track,
					sample,
					registeredUser.BestSimilarity,
					capturedAtUtc)
					? PersonIdentityEvidenceState.UsableUnknown
					: PersonIdentityEvidenceState.Insufficient;
			double evidenceConfidence = evidenceState switch
			{
				PersonIdentityEvidenceState.ConfirmedRegisteredUser =>
					Math.Clamp(similarity, 0d, 1d),
				PersonIdentityEvidenceState.UsableUnknown =>
					Math.Clamp(track.AverageDetectionScore, 0d, 1d),
				_ => 0d
			};
			observations.Add(new PersonIdentityObservation(
				track.TrackId,
				positivelyIdentified ? person!.Id : "",
				positivelyIdentified ? person!.DisplayName : "",
				positivelyIdentified ? person!.Username : "",
				positivelyIdentified,
				positivelyIdentified,
				Math.Clamp(similarity, 0d, 1d),
				sample.FaceBox,
				evidenceState,
				evidenceConfidence));
		}

		SaveIfDirtyLocked(force: false, capturedAtUtc);
		int rememberedInFrame =
			observations.Count(observation => observation.IsRemembered);
		int unknownInFrame = observations.Count - rememberedInFrame;
		string status = FormatStatus(
			observations.Count,
			rememberedInFrame,
			unknownInFrame);
		return new PersonIdentitySnapshot(
			capturedAtUtc,
			observations,
			_rememberedPeople.Count,
			BackendName,
			status);
	}

	private static bool HasUsableUnknownEvidence(
		TrackState track,
		FaceSample sample,
		double bestRegisteredUserSimilarity,
		DateTime capturedAtUtc)
	{
		return sample.DetectionScore >= UsableEvidenceDetectionScore
			&& track.AverageDetectionScore >= UsableEvidenceDetectionScore
			&& track.ObservationCount >= UsableUnknownObservations
			&& capturedAtUtc - track.FirstSeenAtUtc
				>= UsableUnknownConfirmationDuration
			&& bestRegisteredUserSimilarity
				< StrongUnknownMaximumSimilarity;
	}

	private void ProcessPendingEnrollmentCaptureLocked(
		IReadOnlyList<FaceSample> samples,
		DateTime capturedAtUtc,
		Func<byte[]>? contextPhotoFactory)
	{
		EnrollmentSession? enrollment = _enrollment;
		if (enrollment is null || !enrollment.CapturePending)
		{
			return;
		}
		enrollment.CapturePending = false;
		if (samples.Count != 1)
		{
			_enrollmentState = CreateEnrollmentState(
				enrollment,
				samples.Count == 0
					? "No face was detected. Keep one face visible and try again."
					: "Enrollment requires exactly one face in view.");
			return;
		}
		FaceSample sample = samples[0];
		if (sample.DetectionScore < 0.82d)
		{
			_enrollmentState = CreateEnrollmentState(
				enrollment,
				"Face confidence is too low. Improve lighting and try again.");
			return;
		}
		byte[] jpeg = contextPhotoFactory?.Invoke() ?? [];
		if (jpeg.Length == 0)
		{
			_enrollmentState = CreateEnrollmentState(
				enrollment,
				"The current frame could not be captured. Try again.");
			return;
		}
		enrollment.Embeddings.Add(sample.Embedding.ToArray());
		if (enrollment.ContextPhotoJpeg.Length == 0)
		{
			enrollment.ContextPhotoJpeg = jpeg;
		}
		if (enrollment.Embeddings.Count < EnrollmentPrompts.Length)
		{
			_enrollmentState = CreateEnrollmentState(
				enrollment,
				$"Capture saved. {EnrollmentPrompts[enrollment.Embeddings.Count]}");
			return;
		}
		CompleteEnrollmentLocked(enrollment, capturedAtUtc);
	}

	private void CompleteEnrollmentLocked(
		EnrollmentSession enrollment,
		DateTime capturedAtUtc)
	{
		PersonIdentityRecord? duplicate = _rememberedPeople
			.Where(person => person.IsRegisteredUser
				&& !string.Equals(
					person.Id,
					enrollment.ExistingIdentityId,
					StringComparison.OrdinalIgnoreCase))
			.Select(person => new
			{
				Person = person,
				Similarity = enrollment.Embeddings.Max(embedding =>
					MaximumSimilarity(embedding, person.Prototypes))
			})
			.Where(candidate =>
				candidate.Similarity >= RegisteredUserSimilarity)
			.OrderByDescending(candidate => candidate.Similarity)
			.Select(candidate => candidate.Person)
			.FirstOrDefault();
		if (duplicate is not null)
		{
			_activeTracks.Clear();
			_enrollment = null;
			_enrollmentState = new IdentityEnrollmentState(
				true,
				false,
				false,
				enrollment.Embeddings.Count,
				EnrollmentPrompts.Length,
				"",
				$"Enrollment stopped: this face matches existing user "
					+ $"{duplicate.DisplayName}.",
				"");
			return;
		}
		if (_rememberedPeople.Any(person =>
			person.IsRegisteredUser
			&& !string.Equals(
				person.Id,
				enrollment.ExistingIdentityId,
				StringComparison.OrdinalIgnoreCase)
			&& string.Equals(
				person.Username,
				enrollment.Request.Username,
				StringComparison.OrdinalIgnoreCase)))
		{
			_activeTracks.Clear();
			_enrollment = null;
			_enrollmentState = new IdentityEnrollmentState(
				true,
				false,
				false,
				enrollment.Embeddings.Count,
				EnrollmentPrompts.Length,
				"",
				$"Enrollment stopped: username "
					+ $"'{enrollment.Request.Username}' is already assigned.",
				"");
			return;
		}
		PersonIdentityRecord? existing = string.IsNullOrWhiteSpace(
				enrollment.ExistingIdentityId)
			? null
			: _rememberedPeople.FirstOrDefault(person => string.Equals(
				person.Id,
				enrollment.ExistingIdentityId,
				StringComparison.OrdinalIgnoreCase));
		var person = existing ?? new PersonIdentityRecord
		{
			Id = "person-" + Guid.NewGuid().ToString("N"),
			FirstSeenAtUtc = capturedAtUtc
		};
		person.DisplayName =
			$"{enrollment.Request.FirstName} {enrollment.Request.LastName}";
		person.FirstName = enrollment.Request.FirstName;
		person.LastName = enrollment.Request.LastName;
		person.Username = enrollment.Request.Username;
		person.Email = enrollment.Request.Email;
		person.PhoneNumber = enrollment.Request.PhoneNumber;
		person.Address = enrollment.Request.Address;
		person.IsRegisteredUser = true;
		person.PermissionLevel = NormalizePermission(
			enrollment.Request.PermissionLevel);
		person.LastSeenAtUtc = capturedAtUtc;
		person.ObservationCount += enrollment.Embeddings.Count;
		person.EncounterCount = Math.Max(1, person.EncounterCount + 1);
		person.Prototypes = enrollment.Embeddings
			.Select(embedding => embedding.ToArray())
			.Take(MaximumPrototypesPerIdentity)
			.ToList();
		if (existing is null)
		{
			_rememberedPeople.Add(person);
		}
		_activeTracks.Clear();
		_store.SaveContextPhoto(
			_outputFolder,
			person.Id,
			enrollment.ContextPhotoJpeg);
		_dirtyIdentityIds.Add(person.Id);
		SaveIfDirtyLocked(force: true, capturedAtUtc);
		_enrollment = null;
		_enrollmentState = new IdentityEnrollmentState(
			true,
			false,
			false,
			enrollment.Embeddings.Count,
			EnrollmentPrompts.Length,
			"",
			existing is null
				? $"{person.DisplayName} was enrolled as a registered user."
				: $"Camera enrollment was added to {person.DisplayName}.",
			person.Id);
	}

	private static IdentityEnrollmentState CreateEnrollmentState(
		EnrollmentSession enrollment,
		string status)
	{
		int captured = enrollment.Embeddings.Count;
		return new IdentityEnrollmentState(
			true,
			true,
			enrollment.CapturePending,
			captured,
			EnrollmentPrompts.Length,
			EnrollmentPrompts[Math.Min(
				captured,
				EnrollmentPrompts.Length - 1)],
			status,
			"");
	}

	private static IdentityEnrollmentState ReadyEnrollmentState(
		string status)
	{
		return new IdentityEnrollmentState(
			true,
			false,
			false,
			0,
			EnrollmentPrompts.Length,
			"",
			status,
			"");
	}

	private TrackState FindOrCreateKnownTrackLocked(
		PersonIdentityRecord person,
		FaceSample sample,
		DateTime capturedAtUtc,
		ISet<string> usedTracks)
	{
		TrackState? track = _activeTracks
			.Where(candidate =>
				!usedTracks.Contains(candidate.TrackId)
				&& string.Equals(
					candidate.IdentityId,
					person.Id,
					StringComparison.OrdinalIgnoreCase))
			.OrderByDescending(candidate =>
				IntersectionOverUnion(
					candidate.LastFaceBox,
					sample.FaceBox))
			.FirstOrDefault();
		if (track is not null)
		{
			return track;
		}
		track = new TrackState(sample, capturedAtUtc)
		{
			IdentityId = person.Id
		};
		_activeTracks.Add(track);
		return track;
	}

	private TrackState FindOrCreateTemporaryTrackLocked(
		FaceSample sample,
		DateTime capturedAtUtc,
		ISet<string> usedTracks)
	{
		TrackState? best = null;
		double bestScore = double.NegativeInfinity;
		foreach (TrackState candidate in _activeTracks)
		{
			if (usedTracks.Contains(candidate.TrackId)
				|| !string.IsNullOrWhiteSpace(candidate.IdentityId)
				|| capturedAtUtc - candidate.LastSeenAtUtc
					> TemporaryTrackLifetime)
			{
				continue;
			}
			double similarity = Cosine(
				sample.Embedding,
				candidate.Centroid);
			double overlap = IntersectionOverUnion(
				sample.FaceBox,
				candidate.LastFaceBox);
			if (similarity < TemporaryTrackSimilarity
				|| (similarity < KnownIdentitySimilarity
					&& overlap < 0.05d))
			{
				continue;
			}
			double score = similarity + overlap * 0.12d;
			if (score > bestScore)
			{
				best = candidate;
				bestScore = score;
			}
		}
		if (best is not null)
		{
			return best;
		}
		var created = new TrackState(sample, capturedAtUtc);
		_activeTracks.Add(created);
		return created;
	}

	private TrackState? FindContinuousConfirmedTrackLocked(
		FaceSample sample,
		DateTime capturedAtUtc,
		ISet<string> usedTracks)
	{
		TrackState? best = null;
		double bestScore = double.NegativeInfinity;
		foreach (TrackState candidate in _activeTracks)
		{
			if (usedTracks.Contains(candidate.TrackId)
				|| string.IsNullOrWhiteSpace(candidate.IdentityId)
				|| capturedAtUtc - candidate.LastSeenAtUtc
					> ConfirmedTrackContinuityMaximumAge)
			{
				continue;
			}
			double similarity = Cosine(
				sample.Embedding,
				candidate.Centroid);
			double overlap = IntersectionOverUnion(
				sample.FaceBox,
				candidate.LastFaceBox);
			double centerDistance = FaceBoxCenterDistance(
				sample.FaceBox,
				candidate.LastFaceBox);
			double faceScale = Math.Max(
				0.02d,
				Math.Max(
					FaceBoxWidth(sample.FaceBox),
					FaceBoxWidth(candidate.LastFaceBox)));
			bool spatiallyContinuous = overlap >= 0.08d
				|| centerDistance <= faceScale * 0.55d;
			if (!spatiallyContinuous || similarity < 0.30d)
			{
				continue;
			}
			double score = overlap + similarity * 0.25d;
			if (score > bestScore)
			{
				best = candidate;
				bestScore = score;
			}
		}
		return best;
	}

	private KnownMatch FindKnownMatchLocked(
		IReadOnlyList<float> embedding,
		ISet<string> excludedIdentityIds,
		bool registeredUsersOnly = false)
	{
		PersonIdentityRecord? bestPerson = null;
		double best = -1d;
		double second = -1d;
		foreach (PersonIdentityRecord person in _rememberedPeople)
		{
			if (excludedIdentityIds.Contains(person.Id)
				|| (registeredUsersOnly && !person.IsRegisteredUser)
				|| person.Prototypes.Count == 0)
			{
				continue;
			}
			double similarity = MaximumSimilarity(
				embedding,
				person.Prototypes);
			if (similarity > best)
			{
				second = best;
				best = similarity;
				bestPerson = person;
			}
			else if (similarity > second)
			{
				second = similarity;
			}
		}
		return new KnownMatch(bestPerson, best, second);
	}

	private void UpdateRememberedPersonLocked(
		PersonIdentityRecord person,
		FaceSample sample,
		DateTime capturedAtUtc,
		bool updatePrototype)
	{
		if (capturedAtUtc - person.LastSeenAtUtc >= NewEncounterGap)
		{
			person.EncounterCount++;
		}
		person.LastSeenAtUtc = capturedAtUtc;
		person.ObservationCount++;
		if (updatePrototype
			&& sample.DetectionScore >= 0.82d
			&& person.Prototypes.Count < MaximumPrototypesPerIdentity
			&& MaximumSimilarity(
				sample.Embedding,
				person.Prototypes) < 0.92d)
		{
			person.Prototypes.Add(sample.Embedding.ToArray());
		}
		_dirtyIdentityIds.Add(person.Id);
	}

	private bool TryGetSingleActiveRememberedPersonLocked(
		DateTime utcNow,
		[NotNullWhen(true)] out PersonIdentityRecord? person)
	{
		person = null;
		PersonIdentitySnapshot snapshot = _latestSnapshot;
		if (snapshot.CapturedAtUtc == DateTime.MinValue
			|| utcNow - snapshot.CapturedAtUtc > ActiveIdentityMaximumAge
			|| snapshot.People.Count != 1)
		{
			return false;
		}
		PersonIdentityObservation observation = snapshot.People[0];
		if (!observation.IsRemembered
			|| string.IsNullOrWhiteSpace(observation.IdentityId))
		{
			return false;
		}
		person = _rememberedPeople.FirstOrDefault(candidate =>
			string.Equals(
				candidate.Id,
				observation.IdentityId,
				StringComparison.OrdinalIgnoreCase));
		return person is not null;
	}

	private void SaveIfDirtyLocked(bool force, DateTime utcNow)
	{
		if (_dirtyIdentityIds.Count == 0
			|| string.IsNullOrWhiteSpace(_outputFolder)
			|| (!force
				&& utcNow - _lastSavedAtUtc < PersistenceInterval))
		{
			return;
		}
		List<PersonIdentityRecord> changedPeople = _rememberedPeople
			.Where(person => _dirtyIdentityIds.Contains(person.Id))
			.ToList();
		_store.Upsert(_outputFolder, changedPeople);
		_lastSavedAtUtc = utcNow;
		_dirtyIdentityIds.Clear();
	}

	private void PruneExpiredTracksLocked(DateTime capturedAtUtc)
	{
		_activeTracks.RemoveAll(track =>
			capturedAtUtc - track.LastSeenAtUtc
				> TemporaryTrackLifetime);
	}

	private static string FormatStatus(
		int visibleCount,
		int rememberedCount,
		int unknownCount)
	{
		if (visibleCount == 0)
		{
			return "Identity: no face observed";
		}
		string people = visibleCount == 1
			? "1 face"
			: $"{visibleCount} faces";
		if (rememberedCount == 0)
		{
			return $"Identity: {people}; {unknownCount} unknown";
		}
		if (unknownCount == 0)
		{
			return $"Identity: {people}; {rememberedCount} registered user" +
				(rememberedCount == 1 ? "" : "s");
		}
		return "Identity: " +
			$"{people}; {rememberedCount} registered, " +
			$"{unknownCount} unknown";
	}

	private static PersonFaceBox ToNormalizedBox(
		Rect face,
		int width,
		int height)
	{
		return new PersonFaceBox(
			Math.Clamp((double)face.Left / width, 0d, 1d),
			Math.Clamp((double)face.Top / height, 0d, 1d),
			Math.Clamp((double)face.Right / width, 0d, 1d),
			Math.Clamp((double)face.Bottom / height, 0d, 1d));
	}

	private static string NormalizePermission(string? permission)
	{
		return string.Equals(
			permission?.Trim(),
			"Superuser",
			StringComparison.OrdinalIgnoreCase)
			? "Superuser"
			: "Default User";
	}

	private static double MaximumSimilarity(
		IReadOnlyList<float> embedding,
		IEnumerable<float[]> prototypes)
	{
		double best = -1d;
		foreach (float[] prototype in prototypes)
		{
			best = Math.Max(best, Cosine(embedding, prototype));
		}
		return best;
	}

	private static double Cosine(
		IReadOnlyList<float> first,
		IReadOnlyList<float> second)
	{
		if (first.Count != second.Count || first.Count == 0)
		{
			return -1d;
		}
		double dot = 0d;
		for (int index = 0; index < first.Count; index++)
		{
			dot += first[index] * second[index];
		}
		return Math.Clamp(dot, -1d, 1d);
	}

	private static double IntersectionOverUnion(
		PersonFaceBox first,
		PersonFaceBox second)
	{
		double left = Math.Max(first.Left, second.Left);
		double top = Math.Max(first.Top, second.Top);
		double right = Math.Min(first.Right, second.Right);
		double bottom = Math.Min(first.Bottom, second.Bottom);
		double intersection =
			Math.Max(0d, right - left) *
			Math.Max(0d, bottom - top);
		double firstArea =
			Math.Max(0d, first.Right - first.Left) *
			Math.Max(0d, first.Bottom - first.Top);
		double secondArea =
			Math.Max(0d, second.Right - second.Left) *
			Math.Max(0d, second.Bottom - second.Top);
		double union = firstArea + secondArea - intersection;
		return union <= 1e-8d ? 0d : intersection / union;
	}

	private static double FaceBoxCenterDistance(
		PersonFaceBox first,
		PersonFaceBox second)
	{
		double deltaX = (first.Left + first.Right
			- second.Left - second.Right) * 0.5d;
		double deltaY = (first.Top + first.Bottom
			- second.Top - second.Bottom) * 0.5d;
		return Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
	}

	private static double FaceBoxWidth(PersonFaceBox box)
	{
		return Math.Max(0d, box.Right - box.Left);
	}

	private sealed class TrackState
	{
		private string _registeredUserCandidateId = "";

		private int _registeredUserCandidateObservations;

		private DateTime _registeredUserCandidateFirstSeenAtUtc;

		private DateTime _registeredUserCandidateLastSeenAtUtc;

		private string _confirmedRegisteredUserId = "";

		public string TrackId { get; } =
			"track-" + Guid.NewGuid().ToString("N");

		public string IdentityId { get; set; } = "";

		public DateTime FirstSeenAtUtc { get; }

		public DateTime LastSeenAtUtc { get; private set; }

		public PersonFaceBox LastFaceBox { get; private set; }

		public int ObservationCount { get; private set; }

		public double AverageDetectionScore { get; private set; }

		public float[] Centroid { get; private set; }

		public TrackState(FaceSample initial, DateTime capturedAtUtc)
		{
			FirstSeenAtUtc = capturedAtUtc;
			LastSeenAtUtc = capturedAtUtc;
			LastFaceBox = initial.FaceBox;
			Centroid = initial.Embedding.ToArray();
		}

		public void Update(
			FaceSample sample,
			DateTime capturedAtUtc)
		{
			int previousCount = ObservationCount;
			ObservationCount++;
			LastSeenAtUtc = capturedAtUtc;
			LastFaceBox = sample.FaceBox;
			AverageDetectionScore =
				(AverageDetectionScore * previousCount
					+ sample.DetectionScore) /
				ObservationCount;
			float previousWeight = previousCount;
			for (int index = 0; index < Centroid.Length; index++)
			{
				Centroid[index] =
					(Centroid[index] * previousWeight
						+ sample.Embedding[index]) /
					ObservationCount;
			}
			NormalizeInPlace(Centroid);
		}

		public bool ObserveStrongRegisteredUserMatch(
			string identityId,
			DateTime capturedAtUtc)
		{
			if (string.Equals(
					_confirmedRegisteredUserId,
					identityId,
					StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
			if (!string.Equals(
					_registeredUserCandidateId,
					identityId,
					StringComparison.OrdinalIgnoreCase)
				|| (_registeredUserCandidateLastSeenAtUtc != default
					&& capturedAtUtc - _registeredUserCandidateLastSeenAtUtc
						> RegisteredUserConfirmationMaximumGap))
			{
				_registeredUserCandidateId = identityId;
				_registeredUserCandidateObservations = 0;
				_registeredUserCandidateFirstSeenAtUtc = capturedAtUtc;
			}
			_registeredUserCandidateLastSeenAtUtc = capturedAtUtc;
			_registeredUserCandidateObservations++;
			if (_registeredUserCandidateObservations
					< RegisteredUserConfirmationObservations
				|| capturedAtUtc - _registeredUserCandidateFirstSeenAtUtc
					< RegisteredUserConfirmationDuration)
			{
				return false;
			}
			_confirmedRegisteredUserId = identityId;
			return true;
		}

		private static void NormalizeInPlace(float[] values)
		{
			double squaredNorm = 0d;
			for (int index = 0; index < values.Length; index++)
			{
				squaredNorm += values[index] * values[index];
			}
			double norm = Math.Sqrt(squaredNorm);
			if (norm < 1e-8d)
			{
				return;
			}
			float inverseNorm = (float)(1d / norm);
			for (int index = 0; index < values.Length; index++)
			{
				values[index] *= inverseNorm;
			}
		}
	}

	private sealed record FaceSample(
		float[] Embedding,
		double DetectionScore,
		PersonFaceBox FaceBox);

	private sealed class EnrollmentSession(
		IdentityEnrollmentRequest request,
		string? existingIdentityId = null)
	{
		public IdentityEnrollmentRequest Request { get; } = request;

		public string? ExistingIdentityId { get; } = existingIdentityId;

		public bool CapturePending { get; set; }

		public List<float[]> Embeddings { get; } = [];

		public byte[] ContextPhotoJpeg { get; set; } = [];
	}

	private readonly record struct KnownMatch(
		PersonIdentityRecord? Person,
		double BestSimilarity,
		double SecondSimilarity);
}
