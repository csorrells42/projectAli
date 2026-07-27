using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace AvatarBuilder.Modules.Vision.MediaPipe;

public sealed class DenseFaceLandmarkModelInfo
{
	private sealed class Manifest
	{
		public string Backend { get; init; } = "";

		public string ModelFile { get; init; } = "";

		public string ModelUrl { get; init; } = "";

		public string Sha256 { get; init; } = "";

		public int ExpectedLandmarks { get; init; }

		public IReadOnlyList<string> Outputs { get; init; } = Array.Empty<string>();

		public string Runtime { get; init; } = "";

		public IReadOnlyList<string> RuntimeFiles { get; init; } = Array.Empty<string>();

		public string InferenceImplementationStatus { get; init; } = "";

		public string Status { get; init; } = "";

		public string Notes { get; init; } = "";
	}

	private const string RelativeModelDirectory = "dependencies/vision/dense-face-landmarks";

	private const string DefaultManifestFileName = "face_landmarker_manifest.json";

	private const string DefaultModelFileName = "face_landmarker.task";

	private static readonly JsonSerializerOptions ManifestJsonOptions = new JsonSerializerOptions
	{
		PropertyNameCaseInsensitive = true
	};

	public string ModelDirectory { get; init; } = "";

	public string ManifestPath { get; init; } = "";

	public string ModelPath { get; init; } = "";

	public string Backend { get; init; } = "Dense face landmark backend";

	public int ExpectedLandmarks { get; init; }

	public IReadOnlyList<string> Outputs { get; init; } = Array.Empty<string>();

	public string ModelUrl { get; init; } = "";

	public string Sha256 { get; init; } = "";

	public string Runtime { get; init; } = "";

	public IReadOnlyList<string> RuntimeFiles { get; init; } = Array.Empty<string>();

	public string InferenceImplementationStatus { get; init; } = "";

	public string ManifestStatus { get; init; } = "";

	public string Notes { get; init; } = "";

	public bool ManifestExists => File.Exists(ManifestPath);

	public bool ModelExists => File.Exists(ModelPath);

	public bool IsReady
	{
		get
		{
			if (ManifestExists)
			{
				return ModelExists;
			}
			return false;
		}
	}

	public bool RuntimeFilesExist
	{
		get
		{
			if (RuntimeFiles.Count > 0)
			{
				return RuntimeFiles.All((string file) => File.Exists(Path.Combine(ModelDirectory, file)));
			}
			return false;
		}
	}

	public bool CanRunInference
	{
		get
		{
			if (IsReady && string.Equals(InferenceImplementationStatus, "ready", StringComparison.OrdinalIgnoreCase))
			{
				if (RuntimeFiles.Count != 0)
				{
					return RuntimeFilesExist;
				}
				return true;
			}
			return false;
		}
	}

	public string Status
	{
		get
		{
			if (!ManifestExists)
			{
				return "dense landmark manifest missing";
			}
			if (!ModelExists)
			{
				return "dense landmark model missing";
			}
			if (!CanRunInference)
			{
				if (!string.IsNullOrWhiteSpace(InferenceImplementationStatus))
				{
					return "dense landmark model present; inference " + FormatInferenceStatus(InferenceImplementationStatus);
				}
				return "dense landmark model present; inference runtime not ready";
			}
			return "dense landmark model and runtime ready";
		}
	}

	private static string FormatInferenceStatus(string status)
	{
		status = status.Replace('_', ' ').Trim();
		if (!status.StartsWith("runtime ", StringComparison.OrdinalIgnoreCase))
		{
			return "runtime " + status;
		}
		return status;
	}

	public static DenseFaceLandmarkModelInfo Load()
	{
		string text = Path.Combine(AppContext.BaseDirectory, "dependencies/vision/dense-face-landmarks".Replace('/', Path.DirectorySeparatorChar));
		string text2 = Path.Combine(text, "face_landmarker_manifest.json");
		string modelPath = Path.Combine(text, "face_landmarker.task");
		if (!File.Exists(text2))
		{
			return new DenseFaceLandmarkModelInfo
			{
				ModelDirectory = text,
				ManifestPath = text2,
				ModelPath = modelPath
			};
		}
		try
		{
			Manifest? manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(text2), ManifestJsonOptions);
			string path = (string.IsNullOrWhiteSpace(manifest?.ModelFile) ? "face_landmarker.task" : manifest.ModelFile);
			return new DenseFaceLandmarkModelInfo
			{
				ModelDirectory = text,
				ManifestPath = text2,
				ModelPath = Path.Combine(text, path),
				Backend = (string.IsNullOrWhiteSpace(manifest?.Backend) ? "Dense face landmark backend" : manifest.Backend),
				ExpectedLandmarks = (manifest?.ExpectedLandmarks ?? 0),
				Outputs = (manifest?.Outputs ?? Array.Empty<string>()),
				ModelUrl = (manifest?.ModelUrl ?? ""),
				Sha256 = (manifest?.Sha256 ?? ""),
				Runtime = (manifest?.Runtime ?? ""),
				RuntimeFiles = (manifest?.RuntimeFiles ?? Array.Empty<string>()),
				InferenceImplementationStatus = (manifest?.InferenceImplementationStatus ?? ""),
				ManifestStatus = (manifest?.Status ?? ""),
				Notes = (manifest?.Notes ?? "")
			};
		}
		catch (Exception ex)
		{
			return new DenseFaceLandmarkModelInfo
			{
				ModelDirectory = text,
				ManifestPath = text2,
				ModelPath = modelPath,
				ManifestStatus = "manifest unreadable: " + ex.Message
			};
		}
	}
}
