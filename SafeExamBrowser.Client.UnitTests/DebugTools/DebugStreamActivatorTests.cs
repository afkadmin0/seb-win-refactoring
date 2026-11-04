/*
 * Copyright (c) 2026 ETH Zürich, IT Services
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using SafeExamBrowser.Client.DebugTools;
using SafeExamBrowser.Logging.Contracts;
using SafeExamBrowser.WindowsApi.Contracts;
using SafeExamBrowser.WindowsApi.Contracts.Events;

namespace SafeExamBrowser.Client.UnitTests.DebugTools
{
	[TestClass]
	public class DebugStreamActivatorTests
	{
		private const int F12 = 0x7B;
		private const int LEFT_ALT = 0xA4;
		private const int LEFT_CTRL = 0xA2;
		private const int LEFT_SHIFT = 0xA0;
		private const int RIGHT_ALT = 0xA5;
		private const int RIGHT_CTRL = 0xA3;
		private const int RIGHT_SHIFT = 0xA1;

		private KeyboardHookCallback callback;
		private Guid hookId;
		private Mock<INativeMethods> nativeMethods;
		private DebugStreamActivator sut;
		private int activations;
		private ManualResetEventSlim activated;

		[TestInitialize]
		public void Initialize()
		{
			activations = 0;
			activated = new ManualResetEventSlim(false);
			hookId = Guid.NewGuid();
			nativeMethods = new Mock<INativeMethods>();
			nativeMethods.Setup(n => n.RegisterKeyboardHook(It.IsAny<KeyboardHookCallback>())).Callback<KeyboardHookCallback>(c => callback = c).Returns(hookId);

			sut = new DebugStreamActivator(new Mock<ILogger>().Object, nativeMethods.Object);
			sut.Activated += () =>
			{
				Interlocked.Increment(ref activations);
				activated.Set();
			};
			sut.Start();
		}

		[TestMethod]
		public void MustActivateOnCtrlShiftAltF12()
		{
			Press(LEFT_CTRL, LEFT_SHIFT, LEFT_ALT, F12);

			Assert.IsTrue(activated.Wait(2000));
			Assert.AreEqual(1, activations);
		}

		[TestMethod]
		public void MustActivateWithModifiersInAnyOrder()
		{
			Press(LEFT_ALT, LEFT_SHIFT, LEFT_CTRL);
			Press(F12);

			Assert.IsTrue(activated.Wait(2000));
			Assert.AreEqual(1, activations);
		}

		[TestMethod]
		public void MustActivateWithRightModifiers()
		{
			Press(RIGHT_CTRL, RIGHT_SHIFT, RIGHT_ALT, F12);

			Assert.IsTrue(activated.Wait(2000));
		}

		[TestMethod]
		public void MustNotActivateWithoutAllModifiers()
		{
			foreach (var missing in new[] { LEFT_CTRL, LEFT_SHIFT, LEFT_ALT })
			{
				Reset();

				foreach (var modifier in new[] { LEFT_CTRL, LEFT_SHIFT, LEFT_ALT })
				{
					if (modifier != missing)
					{
						Press(modifier);
					}
				}

				Press(F12);
				Release(F12, LEFT_CTRL, LEFT_SHIFT, LEFT_ALT);
			}

			AssertNotActivated();
		}

		[TestMethod]
		public void MustNotActivateWithoutF12()
		{
			Press(LEFT_CTRL, LEFT_SHIFT, LEFT_ALT, 0x7A); // F11
			Press(0x41); // A

			AssertNotActivated();
		}

		[TestMethod]
		public void MustOnlyActivateWhenF12CompletesTheSequence()
		{
			Press(LEFT_CTRL, LEFT_SHIFT, F12);
			Press(LEFT_ALT);

			AssertNotActivated();
		}

		[TestMethod]
		public void MustNotActivateOnRelease()
		{
			Press(LEFT_CTRL, LEFT_SHIFT, LEFT_ALT);
			Release(F12);

			AssertNotActivated();
		}

		[TestMethod]
		public void MustNotActivateRepeatedlyWhileKeyIsHeld()
		{
			Press(LEFT_CTRL, LEFT_SHIFT, LEFT_ALT, F12);
			Assert.IsTrue(activated.Wait(2000));

			// Holding the key sends further key down events without any key up event in between.
			Press(F12, F12, F12);
			Thread.Sleep(200);

			Assert.AreEqual(1, activations);

			Release(F12);
			Press(F12);

			SpinWait.SpinUntil(() => activations == 2, 2000);

			Assert.AreEqual(2, activations);
		}

		[TestMethod]
		public void MustNotActivateAfterModifiersWereReleased()
		{
			Press(LEFT_CTRL, LEFT_SHIFT, LEFT_ALT);
			Release(LEFT_ALT);
			Press(F12);

			AssertNotActivated();
		}

		[TestMethod]
		public void MustNeverConsumeKeys()
		{
			foreach (var key in new[] { LEFT_CTRL, LEFT_SHIFT, LEFT_ALT, F12, 0x41 })
			{
				Assert.IsFalse(callback(key, KeyModifier.None, KeyState.Pressed));
				Assert.IsFalse(callback(key, KeyModifier.None, KeyState.Released));
			}
		}

		[TestMethod]
		public void MustNotActivateWhilePaused()
		{
			sut.Pause();
			Press(LEFT_CTRL, LEFT_SHIFT, LEFT_ALT, F12);

			AssertNotActivated();
		}

		[TestMethod]
		public void MustForgetKeysPressedBeforeResuming()
		{
			Press(LEFT_CTRL, LEFT_SHIFT, LEFT_ALT);
			sut.Pause();
			sut.Resume();
			Press(F12);

			AssertNotActivated();
		}

		[TestMethod]
		public void MustDeregisterHookWhenStopped()
		{
			sut.Stop();

			nativeMethods.Verify(n => n.DeregisterKeyboardHook(hookId), Times.Once);
		}

		private void AssertNotActivated()
		{
			Assert.IsFalse(activated.Wait(300));
			Assert.AreEqual(0, activations);
		}

		private void Press(params int[] keys)
		{
			foreach (var key in keys)
			{
				callback(key, KeyModifier.None, KeyState.Pressed);
			}
		}

		private void Release(params int[] keys)
		{
			foreach (var key in keys)
			{
				callback(key, KeyModifier.None, KeyState.Released);
			}
		}

		private void Reset()
		{
			Release(F12, LEFT_CTRL, LEFT_SHIFT, LEFT_ALT);
			activated.Reset();
		}
	}
}
