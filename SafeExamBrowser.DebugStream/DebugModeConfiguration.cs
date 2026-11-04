using System;
using Microsoft.Win32;

namespace SafeExamBrowser.DebugStream
{
	/// <summary>
	/// Determines whether the debug tools (Firefox stream, relaxed clipboard / keyboard restrictions, AutoHotkey launcher) are
	/// active for the current process. Unlike the rest of this assembly, this class is always compiled - including into Release
	/// builds - so that an installed, Release-configuration build can still have the debug tools turned on, without shipping a
	/// separate Debug build. It is the single gate every entry point of the debug tools must check before doing anything.
	///
	/// - In a build compiled with the <c>DEBUG</c> constant (i.e. a developer's own Debug build), the tools are always active.
	/// - In any other build, they are only active if a machine administrator explicitly turned them on - normally via the
	///   <c>ENABLEDEBUGMODE=1</c> property of the installer (see Setup\Components\DebugMode.wxs), which writes the registry value
	///   this class reads. The registry key is only writable by an administrator, which is who runs the installer.
	///
	/// This must never be true by default for a plain, unmodified installation - these tools give an exam session full,
	/// unrestricted access to Firefox and the clipboard of the machine running it.
	/// </summary>
	public static class DebugModeConfiguration
	{
		internal const string REGISTRY_KEY = @"HKEY_LOCAL_MACHINE\SOFTWARE\SafeExamBrowser";
		internal const string REGISTRY_VALUE = "DebugMode";

		/// <summary>
		/// Reads the registry value which determines whether debug mode is enabled by the installation. Overridable for testing -
		/// see <see cref="IsEnabledByInstallation"/>. Defaults to the real registry.
		/// </summary>
		internal static Func<object> ReadRegistryValue { get; set; } = () => Registry.GetValue(REGISTRY_KEY, REGISTRY_VALUE, 0);

		/// <summary>
		/// Whether the debug tools are active for the current process.
		/// </summary>
		public static bool IsEnabled
		{
			get
			{
#if DEBUG
				return true;
#else
				return IsEnabledByInstallation();
#endif
			}
		}

		/// <summary>
		/// Checks only the installation flag, ignoring whether this is a Debug build. Exposed for testing; use <see cref="IsEnabled"/>.
		/// </summary>
		internal static bool IsEnabledByInstallation()
		{
			try
			{
				return ReadRegistryValue() is int number && number != 0;
			}
			catch
			{
				// Being unable to read the registry (e.g. insufficient rights) must never be mistaken for the flag being set.
				return false;
			}
		}
	}
}
