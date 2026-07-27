using System;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using Vortice.D3DCompiler;

namespace AvatarBuilder.Modules.Infrastructure;

internal static class EmbeddedShaderBytecode
{
	private static readonly ConcurrentDictionary<string, byte[]> Cache =
		new(StringComparer.OrdinalIgnoreCase);

	public static byte[] Load(string fileName)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
		return Cache.GetOrAdd(fileName, Compile);
	}

	private static byte[] Compile(string fileName)
	{
		string baseName = fileName.EndsWith(".vs.cso", StringComparison.OrdinalIgnoreCase)
			? fileName[..^7]
			: fileName.EndsWith(".ps.cso", StringComparison.OrdinalIgnoreCase)
				? fileName[..^7]
				: throw new InvalidOperationException(
					$"Unsupported viewport shader '{fileName}'.");
		string entryPoint = fileName.EndsWith(".vs.cso", StringComparison.OrdinalIgnoreCase)
			? "VSMain"
			: "PSMain";
		string profile = entryPoint == "VSMain" ? "vs_5_0" : "ps_5_0";
		Assembly assembly = typeof(EmbeddedShaderBytecode).Assembly;
		string resourceName = $"ViewportModule.Shaders.{baseName}.hlsl";
		using Stream stream = assembly.GetManifestResourceStream(resourceName)
			?? throw new InvalidOperationException(
				$"Embedded viewport shader source '{resourceName}' is missing.");
		using var reader = new StreamReader(stream);
		string source = reader.ReadToEnd();
		return Compiler.Compile(
			source,
			entryPoint,
			baseName + ".hlsl",
			profile,
			ShaderFlags.OptimizationLevel3).ToArray();
	}
}
