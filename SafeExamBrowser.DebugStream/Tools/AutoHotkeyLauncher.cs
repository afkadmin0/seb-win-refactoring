using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;
using SafeExamBrowser.Logging.Contracts;

namespace SafeExamBrowser.DebugStream.Tools
{
	/// <summary>
	/// A command line of an AutoHotkey script, split into the executable and its arguments.
	/// </summary>
	public sealed class AutoHotkeyCommand
	{
		public string Arguments { get; set; }
		public string Executable { get; set; }

		public override string ToString()
		{
			return $"\"{Executable}\" {Arguments}".Trim();
		}
	}

	/// <summary>
	/// AutoHotkey scripts started on the original desktop cannot see or control anything on the isolated desktop
	/// of a session, as hooks, <c>SendInput</c> and windows are bound to a desktop. This launcher therefore starts the very same
	/// scripts a second time within the session (a process started by the client lives on the desktop of the client).
	///
	/// Which scripts are used is determined by (in this order):
	/// 1. The environment variable <c>SEB_DEBUG_AHK</c>: One or more complete command lines, separated by <c>|</c>, e.g.
	///    <c>"C:\Program Files\AutoHotkey\v2\AutoHotkey64.exe" "C:\debug\selection.ahk"</c>. Also supports compiled scripts.
	/// 2. All AutoHotkey interpreters currently running, which are started again with the same command line.
	/// </summary>
	public sealed class AutoHotkeyLauncher : IDisposable
	{
		internal const string ENVIRONMENT_VARIABLE = "SEB_DEBUG_AHK";

		private static readonly Regex InterpreterName = new Regex(@"^AutoHotkey.*\.exe$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

		private readonly Func<string, string> getEnvironmentVariable;
		private readonly ILogger logger;
		private readonly IList<Process> started = new List<Process>();

		public AutoHotkeyLauncher(ILogger logger) : this(logger, Environment.GetEnvironmentVariable)
		{
		}

		internal AutoHotkeyLauncher(ILogger logger, Func<string, string> getEnvironmentVariable)
		{
			this.getEnvironmentVariable = getEnvironmentVariable;
			this.logger = logger;
		}

		/// <summary>
		/// Determines the scripts to be started, without starting them.
		/// </summary>
		public IList<AutoHotkeyCommand> Discover()
		{
			var configured = getEnvironmentVariable(ENVIRONMENT_VARIABLE);

			if (!string.IsNullOrWhiteSpace(configured))
			{
				logger.Info($"Using AutoHotkey command line(s) of the environment variable {ENVIRONMENT_VARIABLE}.");

				return ParseConfiguration(configured);
			}

			return DiscoverRunning();
		}

		/// <summary>
		/// Starts the given scripts on the desktop of the calling process. Failures are logged, they never throw.
		/// </summary>
		public void Start(IEnumerable<AutoHotkeyCommand> commands)
		{
			foreach (var command in commands ?? Enumerable.Empty<AutoHotkeyCommand>())
			{
				try
				{
					var info = new ProcessStartInfo(command.Executable, command.Arguments ?? string.Empty)
					{
						// IMPORTANT: Without a shell, the process inherits the desktop of this process, i.e. the one of the session.
						UseShellExecute = false,
						WorkingDirectory = Path.GetDirectoryName(command.Executable)
					};

					started.Add(Process.Start(info));
					logger.Info($"Started AutoHotkey script within the session: {command}");
				}
				catch (Exception e)
				{
					logger.Error($"Failed to start AutoHotkey script {command}!", e);
				}
			}
		}

		/// <summary>
		/// Terminates all scripts started by this launcher.
		/// </summary>
		public void Dispose()
		{
			foreach (var process in started)
			{
				try
				{
					if (!process.HasExited)
					{
						process.Kill();
					}

					process.Dispose();
				}
				catch (Exception e)
				{
					logger.Warn($"Failed to terminate AutoHotkey process: {e.Message}");
				}
			}

			started.Clear();
		}

		internal static IList<AutoHotkeyCommand> ParseConfiguration(string configuration)
		{
			var commands = new List<AutoHotkeyCommand>();

			foreach (var line in (configuration ?? string.Empty).Split('|'))
			{
				if (TrySplitCommandLine(line, out var executable, out var arguments))
				{
					commands.Add(new AutoHotkeyCommand { Executable = executable, Arguments = arguments });
				}
			}

			return commands;
		}

		/// <summary>
		/// Splits a command line into the executable (quoted or not) and the remaining arguments.
		/// </summary>
		internal static bool TrySplitCommandLine(string commandLine, out string executable, out string arguments)
		{
			executable = default;
			arguments = default;

			var line = commandLine?.Trim();

			if (string.IsNullOrEmpty(line))
			{
				return false;
			}

			if (line[0] == '"')
			{
				var end = line.IndexOf('"', 1);

				if (end < 0)
				{
					return false;
				}

				executable = line.Substring(1, end - 1);
				arguments = line.Substring(end + 1).Trim();
			}
			else
			{
				var space = line.IndexOf(' ');

				executable = space < 0 ? line : line.Substring(0, space);
				arguments = space < 0 ? string.Empty : line.Substring(space + 1).Trim();
			}

			return executable.Length > 0;
		}

		internal static bool IsAutoHotkeyInterpreter(string processName)
		{
			return processName != null && InterpreterName.IsMatch(processName);
		}

		private IList<AutoHotkeyCommand> DiscoverRunning()
		{
			var commands = new List<AutoHotkeyCommand>();
			var self = Process.GetCurrentProcess().Id;

			try
			{
				using (var searcher = new ManagementObjectSearcher("SELECT ProcessId, ParentProcessId, Name, CommandLine FROM Win32_Process WHERE Name LIKE 'AutoHotkey%'"))
				using (var results = searcher.Get())
				{
					foreach (ManagementObject process in results)
					{
						using (process)
						{
							var name = Convert.ToString(process["Name"]);
							var parent = Convert.ToInt32(process["ParentProcessId"]);
							var commandLine = Convert.ToString(process["CommandLine"]);

							// Scripts started by this client in a previous run must not be started again.
							if (IsAutoHotkeyInterpreter(name) && parent != self && TrySplitCommandLine(commandLine, out var executable, out var arguments) && File.Exists(executable))
							{
								commands.Add(new AutoHotkeyCommand { Executable = executable, Arguments = arguments });
							}
						}
					}
				}
			}
			catch (Exception e)
			{
				logger.Error("Failed to discover running AutoHotkey scripts!", e);
			}

			logger.Info($"Found {commands.Count} running AutoHotkey script(s) to be started within the session. Set {ENVIRONMENT_VARIABLE} to choose them explicitly.");

			return commands;
		}
	}
}
