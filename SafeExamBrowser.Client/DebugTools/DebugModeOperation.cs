/*
 * Copyright (c) 2026 ETH Zürich, IT Services
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System;
using System.IO;
using System.Linq;
using SafeExamBrowser.Client.Operations;
using SafeExamBrowser.Core.Contracts.OperationModel;
using SafeExamBrowser.Core.Contracts.OperationModel.Events;
using SafeExamBrowser.DebugStream;
using SafeExamBrowser.DebugStream.Tools;
using SafeExamBrowser.DebugStream.Viewer;
using SafeExamBrowser.Logging.Contracts;
using SafeExamBrowser.Monitoring.Contracts;
using SafeExamBrowser.Monitoring.Contracts.Applications;
using SafeExamBrowser.Settings.Applications;
using SafeExamBrowser.Settings.Security;

namespace SafeExamBrowser.Client.DebugTools
{
	/// <summary>
	/// Makes the debug tools (Firefox of the original desktop, relaxed clipboard / keyboard restrictions, AutoHotkey launcher)
	/// available within the session, but - unlike earlier versions of this operation - does nothing automatically at startup.
	/// Does nothing at all unless <see cref="DebugModeConfiguration.IsEnabled"/>; if it is, the keyboard hook for
	/// <see cref="StreamViewer.ToggleGesture"/> is registered at startup, but everything it actually does is deferred to the first
	/// time that sequence is pressed during the session:
	///
	/// - The clipboard is allowed (otherwise it is cleared every 50 ms). It is shared with the original desktop, which is what makes
	///   copy &amp; paste between the streamed Firefox and the session work.
	/// - Ctrl+C / Ctrl+V / Ctrl+X and injected keys (as sent by AutoHotkey) are no longer blocked by the keyboard interceptor.
	/// - The AutoHotkey interpreters running at that point are started again within the session (only if the session uses its own
	///   desktop) and whitelisted, so the application monitor does not hide or close their windows.
	/// - The debug stream viewer is shown.
	///
	/// Every later press of the sequence only shows / hides the viewer again - the rest only ever happens once per session.
	/// </summary>
	internal class DebugModeOperation : ClientOperation
	{
		private readonly IApplicationMonitor applicationMonitor;
		private readonly DebugStreamActivator activator;
		private readonly IClipboard clipboard;
		private readonly object activationLock = new object();
		private readonly AutoHotkeyLauncher launcher;
		private readonly ILogger logger;

		private bool activated;
		private StreamViewer viewer;

		public override event StatusChangedEventHandler StatusChanged { add { } remove { } }

		internal DebugModeOperation(
			ClientContext context,
			IApplicationMonitor applicationMonitor,
			DebugStreamActivator activator,
			IClipboard clipboard,
			AutoHotkeyLauncher launcher,
			ILogger logger) : base(context)
		{
			this.applicationMonitor = applicationMonitor;
			this.activator = activator;
			this.clipboard = clipboard;
			this.launcher = launcher;
			this.logger = logger;
		}

		public override OperationResult Perform()
		{
			if (!DebugModeConfiguration.IsEnabled)
			{
				return OperationResult.Success;
			}

			try
			{
				activator.Activated += Activator_Activated;
				activator.Start();

				logger.Debug($"Debug mode is available for this session. Press {StreamViewer.ToggleGesture} to activate it.");
			}
			catch (Exception e)
			{
				// A diagnostic tool must never keep the application from starting.
				logger.Error("Failed to initialize debug mode!", e);
			}

			return OperationResult.Success;
		}

		public override OperationResult Revert()
		{
			try
			{
				activator.Activated -= Activator_Activated;
				activator.Stop();
				launcher.Dispose();
				viewer?.Dispose();
			}
			catch (Exception e)
			{
				logger.Error("Failed to finalize debug mode!", e);
			}

			return OperationResult.Success;
		}

		private void Activator_Activated()
		{
			lock (activationLock)
			{
				if (!activated)
				{
					activated = true;

					try
					{
						Activate();
					}
					catch (Exception e)
					{
						logger.Error("Failed to activate debug mode!", e);
					}
				}
			}

			// The viewer only exists once Activate() has run (first press), so this is reached - and toggles the now-existing,
			// not-yet-shown viewer into view - on that very same press. Every later press just shows / hides it again.
			viewer?.Toggle();
		}

		private void Activate()
		{
			var settings = Context.Settings;

			settings.Security.ClipboardPolicy = ClipboardPolicy.Allow;
			settings.Keyboard.AllowCtrlC = true;
			settings.Keyboard.AllowCtrlV = true;
			settings.Keyboard.AllowCtrlX = true;
			settings.Keyboard.AllowInjected = true;

			// The clipboard component already started with whatever policy the configuration specified (and, if restrictive, is
			// actively clearing the clipboard on a timer) - KeyboardInterceptor, by contrast, reads the Allow* flags just set above
			// directly off the live KeyboardSettings on every keystroke, so setting them is all that is needed for that part.
			clipboard.Terminate();
			clipboard.Initialize(ClipboardPolicy.Allow);

			// Within the same desktop, the scripts of the user already work (and starting them again would replace them).
			if (settings.Security.KioskMode == KioskMode.CreateNewDesktop)
			{
				StartAutoHotkey();
			}

			viewer = new StreamViewer(logger, Context.AppConfig.RuntimeId);

			logger.Warn($"*** DEBUG MODE: Activated for this session - clipboard and injected keys are now allowed, AutoHotkey " +
				$"scripts (if any) have been started, and Firefox of the original desktop is available. ***");
		}

		private void StartAutoHotkey()
		{
			var commands = launcher.Discover();

			foreach (var executable in commands.Select(c => c.Executable).Distinct(StringComparer.OrdinalIgnoreCase))
			{
				applicationMonitor.Whitelist(new WhitelistApplication
				{
					AllowRunning = true,
					AutoStart = false,
					Description = "AutoHotkey (debug mode)",
					DisplayName = "AutoHotkey (debug mode)",
					ExecutableName = Path.GetFileName(executable),
					ExecutablePath = Path.GetDirectoryName(executable),
					ShowInShell = false
				});

				logger.Info($"Whitelisted '{executable}' for the AutoHotkey debug scripts.");
			}

			launcher.Start(commands);
		}
	}
}
