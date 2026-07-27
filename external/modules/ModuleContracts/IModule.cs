using System;

namespace AvatarBuilder.Modules.Contracts;

/// <summary>
/// The complete contract visible to the vision chain manager. The manager may
/// start a vision module and read its two timing values. No module-specific
/// operation is permitted through this interface.
/// </summary>
public interface IModule
{
	/// <summary>
	/// Starts the module-owned worker. This operation is idempotent and does not
	/// run the module's work on the caller's thread.
	/// </summary>
	void Start();

	/// <summary>
	/// Returns the most recently completed interval spent idle while waiting
	/// for new input. Reading the value never starts work and never waits.
	/// </summary>
	TimeSpan GetIdleTime();

	/// <summary>
	/// Returns the most recently completed interval spent processing one input.
	/// Reading the value never starts work and never waits.
	/// </summary>
	TimeSpan GetWorkingTime();
}

/// <summary>
/// A frame-producing or frame-consuming module.
/// </summary>
public interface IVisionModule : IModule
{
}

/// <summary>
/// An audio-producing or audio-consuming module.
/// </summary>
public interface IAudioModule : IModule
{
}
