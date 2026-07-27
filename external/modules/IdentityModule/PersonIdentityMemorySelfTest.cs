using System;
using System.IO;

namespace AvatarBuilder.Modules.Vision.Identity;

public sealed record PersonIdentityMemorySelfTestResult(
	bool Succeeded,
	string Detail);

public static class PersonIdentityMemorySelfTest
{
	public static PersonIdentityMemorySelfTestResult Run()
	{
		string root = Path.Combine(
			Path.GetTempPath(),
			"AvatarBuilderIdentitySelfTest",
			Guid.NewGuid().ToString("N"));
		try
		{
			Directory.CreateDirectory(root);
			using var memory = new PersonIdentityMemory(
				initializeModels: false);
			memory.ConfigureOutputFolder(root);
			float[] passerbyEmbedding = CreateEmbedding(2);
			DateTime now = DateTime.UtcNow;

			for (int index = 0; index < 30; index++)
			{
				memory.ObserveEmbeddingFrameForSelfTest(
					[passerbyEmbedding],
					now.AddSeconds(-40d + index));
			}
			PersonIdentitySnapshot passive =
				memory.ObserveEmbeddingFrameForSelfTest(
					[],
					now.AddSeconds(-9d));
			if (passive.RememberedIdentityCount != 0
				|| memory.GetIdentityReviewItems().Count != 0)
			{
				return Fail(
					"Passive viewing created a person record.");
			}

			IdentityReviewUpdateResult beginFirst =
				memory.BeginEnrollment(new IdentityEnrollmentRequest(
					"First",
					"Test Person",
					"first.test",
					"",
					"",
					"",
					"Default User"));
			if (!beginFirst.Success)
			{
				return Fail(beginFirst.Status);
			}
			for (int index = 0; index < 5; index++)
			{
				if (!memory.RequestEnrollmentCapture().Success)
				{
					return Fail("The first enrollment capture was not accepted.");
				}
				memory.ObserveEmbeddingFrameForSelfTest(
					[CreateNearbyEmbedding(0, 10 + index)],
					now.AddSeconds(-8d + index));
			}
			IdentityEnrollmentState firstEnrollment =
				memory.GetEnrollmentState();
			if (string.IsNullOrWhiteSpace(firstEnrollment.CompletedIdentityId)
				|| memory.GetIdentityReviewItems().Count != 1)
			{
				return Fail(
					"Five explicit angles did not enroll the first user.");
			}

			IdentityReviewUpdateResult beginSecond =
				memory.BeginEnrollment(new IdentityEnrollmentRequest(
					"Second",
					"Test Person",
					"second.test",
					"",
					"",
					"",
					"Default User"));
			if (!beginSecond.Success)
			{
				return Fail(beginSecond.Status);
			}
			for (int index = 0; index < 5; index++)
			{
				if (!memory.RequestEnrollmentCapture().Success)
				{
					return Fail("The second enrollment capture was not accepted.");
				}
				memory.ObserveEmbeddingFrameForSelfTest(
					[CreateNearbyEmbedding(1, 20 + index)],
					now.AddSeconds(-3d + index * 0.25d));
			}
			if (memory.GetIdentityReviewItems().Count != 2)
			{
				return Fail(
					"Five explicit angles did not enroll the second user.");
			}
			IdentityReviewUpdateResult beginDuplicate =
				memory.BeginEnrollment(new IdentityEnrollmentRequest(
					"Duplicate",
					"First Person",
					"duplicate.first",
					"",
					"",
					"",
					"Default User"));
			if (!beginDuplicate.Success)
			{
				return Fail(beginDuplicate.Status);
			}
			for (int index = 0; index < 5; index++)
			{
				memory.RequestEnrollmentCapture();
				memory.ObserveEmbeddingFrameForSelfTest(
					[CreateNearbyEmbedding(0, 30 + index)],
					now.AddMilliseconds(-1500d + index * 100d));
			}
			IdentityEnrollmentState duplicateEnrollment =
				memory.GetEnrollmentState();
			if (!string.IsNullOrWhiteSpace(
					duplicateEnrollment.CompletedIdentityId)
				|| memory.GetIdentityReviewItems().Count != 2
				|| !duplicateEnrollment.Status.Contains(
					"matches existing user",
					StringComparison.OrdinalIgnoreCase))
			{
				return Fail(
					"Duplicate face enrollment created another user.");
			}

			PersonIdentitySnapshot recognizedFirst =
				PersonIdentitySnapshot.Waiting;
			for (int index = 0; index < 6; index++)
			{
				recognizedFirst = memory.ObserveEmbeddingFrameForSelfTest(
					[CreateNearbyEmbedding(0, 3)],
					now.AddMilliseconds(index * 120d));
				if (index < 5
					&& recognizedFirst.People.Any(person => person.IsRemembered))
				{
					return Fail(
						"A registered user was published before confirmation.");
				}
			}
			if (recognizedFirst.People.Count != 1
				|| !recognizedFirst.People[0].IsRemembered)
			{
				return Fail(
					"A registered user was not published after strong confirmation.");
			}
			string confirmedIdentityId =
				recognizedFirst.People[0].IdentityId;
			PersonIdentitySnapshot weakContinuousFrame =
				memory.ObserveEmbeddingFrameForSelfTest(
					[CreateWeakContinuousEmbedding(0, 10)],
					now.AddMilliseconds(720));
			if (weakContinuousFrame.People.Count != 1
				|| weakContinuousFrame.People[0].IsRemembered
				|| weakContinuousFrame.People[0].EvidenceState
					!= PersonIdentityEvidenceState.Insufficient
				|| !string.IsNullOrWhiteSpace(
					weakContinuousFrame.People[0].IdentityId))
			{
				return Fail(
					"A weak ambiguous frame was published as a positive identity.");
			}
			PersonIdentitySnapshot recoveredFirst =
				memory.ObserveEmbeddingFrameForSelfTest(
					[CreateNearbyEmbedding(0, 3)],
					now.AddMilliseconds(840));
			if (recoveredFirst.People.Count != 1
				|| !recoveredFirst.People[0].IsRemembered
				|| !string.Equals(
					recoveredFirst.People[0].IdentityId,
					confirmedIdentityId,
					StringComparison.OrdinalIgnoreCase))
			{
				return Fail(
					"Strong evidence did not restore the confirmed registered user.");
			}
			for (int index = 0; index < 6; index++)
			{
				memory.ObserveEmbeddingFrameForSelfTest(
					[CreateNearbyEmbedding(1, 4)],
					now.AddMilliseconds(1000d + index * 120d));
			}
			memory.ObserveEmbeddingFrameForSelfTest(
				[CreateNearbyEmbedding(0, 5)],
				now.AddMilliseconds(1800));
			PersonIdentitySnapshot stableUnknown =
				PersonIdentitySnapshot.Waiting;
			for (int index = 0; index < 3; index++)
			{
				stableUnknown = memory.ObserveEmbeddingFrameForSelfTest(
					[CreateEmbedding(50)],
					now.AddMilliseconds(5000d + index * 150d));
			}
			if (stableUnknown.People.Count != 1
				|| stableUnknown.People[0].EvidenceState
					!= PersonIdentityEvidenceState.UsableUnknown
				|| stableUnknown.People[0].EvidenceConfidence < 0.82d)
			{
				return Fail(
					"A stable high-quality nonmatching face was not published "
					+ "as usable unknown evidence.");
			}

			string storePath =
				new PersonIdentityMemoryStore().GetPath(root);
			if (!File.Exists(storePath))
			{
				return Fail(
					"Retained face memory was not saved.");
			}
			byte[] header = new byte[16];
			using (FileStream stream = new(
				storePath,
				FileMode.Open,
				FileAccess.Read,
				FileShare.ReadWrite | FileShare.Delete))
			{
				if (stream.Read(header, 0, header.Length) != header.Length
					|| !string.Equals(
						System.Text.Encoding.ASCII.GetString(header),
						"SQLite format 3\0",
						StringComparison.Ordinal))
				{
					return Fail(
						"People memory was not stored as the current SQLite format.");
				}
			}
			PersonIdentityReviewItem firstReview = memory
				.GetIdentityReviewItems()
				.Single(item => string.Equals(
					item.IdentityId,
					confirmedIdentityId,
					StringComparison.OrdinalIgnoreCase));
			IdentityReviewUpdateResult reviewUpdate =
				memory.UpdateIdentityReview(new IdentityReviewUpdate(
					firstReview.IdentityId,
					"Christopher",
					"Test",
					"christopher.test",
					"christopher@example.test",
					"555-0100",
					"100 Test Lane",
					true,
					"Superuser"));
			if (!reviewUpdate.Success)
			{
				return Fail(
					"Identity review could not update a retained person.");
			}
			PersonIdentityReviewItem secondReview = memory
				.GetIdentityReviewItems()
				.Single(item => !string.Equals(
					item.IdentityId,
					firstReview.IdentityId,
					StringComparison.OrdinalIgnoreCase));
			IdentityReviewUpdateResult duplicateUsername =
				memory.UpdateIdentityReview(new IdentityReviewUpdate(
					secondReview.IdentityId,
					"Another",
					"Person",
					"CHRISTOPHER.TEST",
					"",
					"",
					"",
					true,
					"Default User"));
			if (duplicateUsername.Success)
			{
				return Fail(
					"Identity review accepted a duplicate username.");
			}
			var store = new PersonIdentityMemoryStore();
			store.SaveContextPhoto(
				root,
				firstReview.IdentityId,
				[0xff, 0xd8, 0xff, 0xd9]);
			if (!File.Exists(store.GetContextPhotoPath(
				root,
				firstReview.IdentityId)))
			{
				return Fail(
					"The retained identity context photo was not saved.");
			}
			using var reloaded = new PersonIdentityMemory(
				initializeModels: false);
			reloaded.ConfigureOutputFolder(root);
			if (reloaded.LatestSnapshot.RememberedIdentityCount != 2)
			{
				return Fail(
					"Two retained people did not survive a store reload.");
			}
			PersonIdentityReviewItem reloadedFirst = reloaded
				.GetIdentityReviewItems()
				.Single(item => string.Equals(
					item.IdentityId,
					firstReview.IdentityId,
					StringComparison.OrdinalIgnoreCase));
			if (!string.Equals(
					reloadedFirst.DisplayName,
					"Christopher Test",
					StringComparison.Ordinal)
				|| !string.Equals(
					reloadedFirst.Username,
					"christopher.test",
					StringComparison.Ordinal)
				|| !string.Equals(
					reloadedFirst.Email,
					"christopher@example.test",
					StringComparison.Ordinal)
				|| !string.Equals(
					reloadedFirst.PhoneNumber,
					"555-0100",
					StringComparison.Ordinal)
				|| !string.Equals(
					reloadedFirst.Address,
					"100 Test Lane",
					StringComparison.Ordinal)
				|| !reloadedFirst.IsRegisteredUser
				|| !string.Equals(
					reloadedFirst.PermissionLevel,
					"Superuser",
					StringComparison.Ordinal)
				|| !File.Exists(reloadedFirst.ContextPhotoPath))
			{
				return Fail(
					"Identity name, registration, role, or context photo " +
					"did not survive review and reload.");
			}
			var storedReview =
				new StoredPersonIdentityReviewService(root);
			IReadOnlyList<PersonIdentityReviewItem> storedItems =
				storedReview.GetIdentityReviewItems();
			if (storedItems.Count != 2
				|| !storedItems.Any(item =>
					string.Equals(
						item.IdentityId,
						firstReview.IdentityId,
						StringComparison.OrdinalIgnoreCase)))
			{
				return Fail(
					"Camera-independent Identity Review did not load " +
					"the current persisted people store.");
			}
			byte[] replacementPhoto =
				[0xff, 0xd8, 0x49, 0x44, 0xff, 0xd9];
			IdentityReviewUpdateResult photoUpdate =
				storedReview.ReplaceContextPhoto(
					firstReview.IdentityId,
					replacementPhoto);
			if (!photoUpdate.Success
				|| !File.ReadAllBytes(
					reloadedFirst.ContextPhotoPath)
					.SequenceEqual(replacementPhoto))
			{
				return Fail(
					"Identity Review did not replace the managed " +
					"context photo.");
			}
			IdentityReviewUpdateResult liveDelete =
				reloaded.DeleteIdentity(firstReview.IdentityId);
			if (!liveDelete.Success
				|| reloaded.GetIdentityReviewItems().Count != 1
				|| File.Exists(reloadedFirst.ContextPhotoPath))
			{
				return Fail(
					"Live identity deletion did not remove the user, "
					+ "face enrollment, and context photo.");
			}
			IdentityReviewUpdateResult offlineDelete =
				storedReview.DeleteIdentity(secondReview.IdentityId);
			if (!offlineDelete.Success
				|| storedReview.GetIdentityReviewItems().Count != 0)
			{
				return Fail(
					"Camera-independent identity deletion did not "
					+ "remove the remaining user.");
			}
			if (Directory.EnumerateFiles(
				Path.GetDirectoryName(storePath)!,
				"*.json",
				SearchOption.TopDirectoryOnly).Any())
			{
				return Fail(
					"A deprecated JSON people-memory store was written.");
			}
			return new PersonIdentityMemorySelfTestResult(
				true,
				"PASS: passive viewing created no person records; " +
				"five explicit angles enrolled each user; " +
				"duplicate face enrollment was rejected; " +
				"registered users required repeated strong evidence; " +
				"weak ambiguous evidence was withheld while stable " +
				"high-quality nonmatches were explicit; " +
				"online and camera-independent SQLite identity review, " +
				"unique usernames, contact fields, " +
				"Superuser labeling, managed full-context photo " +
				"replacement, and complete user deletion round-tripped.");
		}
		catch (Exception ex)
		{
			return Fail(ex.Message);
		}
		finally
		{
			try
			{
				string fullRoot = Path.GetFullPath(root);
				string expectedParent = Path.GetFullPath(Path.Combine(
					Path.GetTempPath(),
					"AvatarBuilderIdentitySelfTest"));
				if (fullRoot.StartsWith(
					expectedParent + Path.DirectorySeparatorChar,
					StringComparison.OrdinalIgnoreCase)
					&& Directory.Exists(fullRoot))
				{
					Directory.Delete(fullRoot, recursive: true);
				}
			}
			catch
			{
			}
		}
	}

	private static float[] CreateEmbedding(int primaryIndex)
	{
		float[] embedding =
			new float[SFaceEmbeddingExtractor.ExpectedEmbeddingLength];
		embedding[primaryIndex] = 1f;
		return embedding;
	}

	private static float[] CreateNearbyEmbedding(
		int primaryIndex,
		int secondaryIndex)
	{
		float[] embedding = CreateEmbedding(primaryIndex);
		embedding[primaryIndex] = 0.995f;
		embedding[secondaryIndex] = 0.1f;
		double norm = Math.Sqrt(
			embedding[primaryIndex] * embedding[primaryIndex]
			+ embedding[secondaryIndex] * embedding[secondaryIndex]);
		embedding[primaryIndex] /= (float)norm;
		embedding[secondaryIndex] /= (float)norm;
		return embedding;
	}

	private static float[] CreateWeakContinuousEmbedding(
		int primaryIndex,
		int secondaryIndex)
	{
		float[] embedding =
			new float[SFaceEmbeddingExtractor.ExpectedEmbeddingLength];
		embedding[primaryIndex] = 0.40f;
		embedding[secondaryIndex] =
			(float)Math.Sqrt(1d - 0.40d * 0.40d);
		return embedding;
	}

	private static PersonIdentityMemorySelfTestResult Fail(string detail)
	{
		return new PersonIdentityMemorySelfTestResult(
			false,
			"FAIL: " + detail);
	}
}
