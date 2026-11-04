using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using SafeExamBrowser.Logging.Contracts;

namespace SafeExamBrowser.DebugStream.Host
{
	/// <summary>
	/// Finds and starts Firefox on the desktop of the calling process, which for the runtime is the original desktop.
	/// </summary>
	internal static class FirefoxLocator
	{
		internal const string EXECUTABLE_NAME = "firefox.exe";

		private const string APP_PATHS = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + EXECUTABLE_NAME;

		internal static bool TryFind(out string path)
		{
			path = default;

			foreach (var candidate in GetCandidates())
			{
				if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
				{
					path = candidate;

					return true;
				}
			}

			return false;
		}

		/// <summary>
		/// Opens a new Firefox window. If Firefox is already running, the new window is opened by the running instance and thus
		/// belongs to the same profile (and the same sessions / logins) as the windows the user already has.
		/// </summary>
		internal static bool TryOpenNewWindow(ILogger logger)
		{
			if (!TryFind(out var path))
			{
				logger.Warn("Could not find Firefox on this computer!");

				return false;
			}

			try
			{
				// IMPORTANT: Do not use the process factory of the runtime here, as it may be configured to start new processes on
				// the isolated desktop. A process started directly inherits the desktop of the runtime, i.e. the original desktop.
				var info = new ProcessStartInfo(path, "-new-window about:newtab") { UseShellExecute = false };

				using (Process.Start(info))
				{
					logger.Info($"Started '{path}' to open a new window on the original desktop.");
				}

				return true;
			}
			catch (Exception e)
			{
				logger.Error($"Failed to start '{path}'!", e);

				return false;
			}
		}

		private static string[] GetCandidates()
		{
			return new[]
			{
				ReadRegistry(Registry.LocalMachine),
				ReadRegistry(Registry.CurrentUser),
				Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Mozilla Firefox", EXECUTABLE_NAME),
				Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Mozilla Firefox", EXECUTABLE_NAME)
			};
		}

		private static string ReadRegistry(RegistryKey hive)
		{
			try
			{
				using (var key = hive.OpenSubKey(APP_PATHS))
				{
					return (key?.GetValue(null) as string)?.Trim('"');
				}
			}
			catch
			{
				return default;
			}
		}
	}
}
