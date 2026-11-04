/*
 * Copyright (c) 2026 ETH Zürich, IT Services
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System;
using System.Threading.Tasks;
using System.Windows.Input;
using SafeExamBrowser.DebugStream.Viewer;
using SafeExamBrowser.Logging.Contracts;
using SafeExamBrowser.UserInterface.Shared.Activators;
using SafeExamBrowser.WindowsApi.Contracts;
using SafeExamBrowser.WindowsApi.Contracts.Events;

namespace SafeExamBrowser.Client.DebugTools
{
	/// <summary>
	/// Detects the sequence which shows and hides the debug stream viewer, i.e. Ctrl + Shift + Alt + F12 (see
	/// <see cref="StreamViewer.ToggleGesture"/>). As a keyboard hook which is registered later than another one is called first,
	/// this works even for keys which are subsequently blocked by the keyboard interceptor.
	/// </summary>
	internal class DebugStreamActivator : KeyboardActivator
	{
		private readonly ILogger logger;

		private bool alt, ctrl, f12, shift;

		internal event Action Activated;

		internal DebugStreamActivator(ILogger logger, INativeMethods nativeMethods) : base(nativeMethods)
		{
			this.logger = logger;
		}

		protected override void OnBeforeResume()
		{
			alt = false;
			ctrl = false;
			f12 = false;
			shift = false;
		}

		protected override bool Process(Key key, KeyModifier modifier, KeyState state)
		{
			var changed = false;
			var pressed = state == KeyState.Pressed;

			switch (key)
			{
				case Key.LeftAlt:
				case Key.RightAlt:
					changed = alt != pressed;
					alt = pressed;
					break;
				case Key.LeftCtrl:
				case Key.RightCtrl:
					changed = ctrl != pressed;
					ctrl = pressed;
					break;
				case Key.LeftShift:
				case Key.RightShift:
					changed = shift != pressed;
					shift = pressed;
					break;
				case Key.F12:
					changed = f12 != pressed;
					f12 = pressed;
					break;
			}

			// Only pressing F12 while all modifiers are held activates - not holding F12 and then adding the last modifier.
			if (key == Key.F12 && changed && pressed && alt && ctrl && shift)
			{
				Task.Run(() =>
				{
					logger.Debug("Detected activation sequence for the debug stream viewer.");
					Activated?.Invoke();
				});
			}

			// The keys are intentionally not consumed, so they behave as usual (e.g. for the interceptor and the viewer).
			return false;
		}
	}
}
