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
using SafeExamBrowser.Configuration.Contracts;
using SafeExamBrowser.Core.Contracts.OperationModel;
using SafeExamBrowser.DebugStream;
using SafeExamBrowser.DebugStream.Tools;
using SafeExamBrowser.Logging.Contracts;
using SafeExamBrowser.Monitoring.Contracts;
using SafeExamBrowser.Monitoring.Contracts.Applications;
using SafeExamBrowser.Settings;
using SafeExamBrowser.Settings.Applications;
using SafeExamBrowser.Settings.Security;
using SafeExamBrowser.WindowsApi.Contracts;
using SafeExamBrowser.WindowsApi.Contracts.Events;

namespace SafeExamBrowser.Client.UnitTests.DebugTools
{
	[TestClass]
	public class DebugModeOperationTests
	{
		private const int LEFT_ALT = 0xA4;
		private const int LEFT_CTRL = 0xA2;
		private const int LEFT_SHIFT = 0xA0;
		private const int F12 = 0x7B;

		private Mock<IApplicationMonitor> applicationMonitor;
		private Mock<IClipboard> clipboard;
		private ClientContext context;
		private Guid hookId;
		private KeyboardHookCallback keyPress;
		private Mock<ILogger> logger;
		private Mock<INativeMethods> nativeMethods;
		private DebugModeOperation sut;

		[TestInitialize]
		public void Initialize()
		{
			applicationMonitor = new Mock<IApplicationMonitor>();
			clipboard = new Mock<IClipboard>();
			context = new ClientContext { AppConfig = new AppConfig { RuntimeId = Guid.NewGuid() }, Settings = new AppSettings() };
			hookId = Guid.NewGuid();
			logger = new Mock<ILogger>();
			nativeMethods = new Mock<INativeMethods>();

			nativeMethods.Setup(n => n.RegisterKeyboardHook(It.IsAny<KeyboardHookCallback>())).Callback<KeyboardHookCallback>(c => keyPress = c).Returns(hookId);

			context.Settings.Security.ClipboardPolicy = ClipboardPolicy.Isolated;
			context.Settings.Security.KioskMode = KioskMode.CreateNewDesktop;

			// Most tests exercise the "enabled" behavior, which in a non-Debug build requires the installation flag.
			DebugModeConfiguration.ReadRegistryValue = () => 1;

			Environment.SetEnvironmentVariable("SEB_DEBUG_AHK", "\"C:\\Tools\\AutoHotkey\\AutoHotkey64.exe\" \"C:\\debug\\selection.ahk\"");

			var activator = new DebugStreamActivator(logger.Object, nativeMethods.Object);
			var launcher = new AutoHotkeyLauncher(logger.Object);

			sut = new DebugModeOperation(context, applicationMonitor.Object, activator, clipboard.Object, launcher, logger.Object);
		}

		[TestCleanup]
		public void Cleanup()
		{
			Environment.SetEnvironmentVariable("SEB_DEBUG_AHK", null);
			DebugModeConfiguration.ReadRegistryValue = () => Microsoft.Win32.Registry.GetValue(DebugModeConfiguration.REGISTRY_KEY, DebugModeConfiguration.REGISTRY_VALUE, 0);
		}

		[TestMethod]
		public void MustRegisterHotkeyButTouchNothingElseOnPerform()
		{
			var result = sut.Perform();

			Assert.AreEqual(OperationResult.Success, result);
			nativeMethods.Verify(n => n.RegisterKeyboardHook(It.IsAny<KeyboardHookCallback>()), Times.Once);

			Assert.AreEqual(ClipboardPolicy.Isolated, context.Settings.Security.ClipboardPolicy);
			Assert.IsFalse(context.Settings.Keyboard.AllowCtrlC);
			Assert.IsFalse(context.Settings.Keyboard.AllowInjected);
			clipboard.Verify(c => c.Initialize(It.IsAny<ClipboardPolicy>()), Times.Never);
			clipboard.Verify(c => c.Terminate(), Times.Never);
			applicationMonitor.Verify(m => m.Whitelist(It.IsAny<WhitelistApplication>()), Times.Never);
			logger.Verify(l => l.Warn(It.IsAny<string>()), Times.Never);
		}

		[TestMethod]
		public void MustActivateEverythingOnFirstPress()
		{
			sut.Perform();

			var activated = new ManualResetEventSlim(false);

			// Warn() is the last thing Activate() does, so waiting for it guarantees everything before it (including starting
			// AutoHotkey) has already happened - unlike waiting for clipboard.Initialize(), which happens earlier in that method.
			logger.Setup(l => l.Warn(It.Is<string>(m => m.Contains("DEBUG MODE")))).Callback(() => activated.Set());

			Press();

			Assert.IsTrue(activated.Wait(2000), "Debug mode was expected to activate after the first press.");

			clipboard.Verify(c => c.Terminate(), Times.Once);
			clipboard.Verify(c => c.Initialize(ClipboardPolicy.Allow), Times.Once);
			Assert.AreEqual(ClipboardPolicy.Allow, context.Settings.Security.ClipboardPolicy);
			Assert.IsTrue(context.Settings.Keyboard.AllowCtrlC);
			Assert.IsTrue(context.Settings.Keyboard.AllowCtrlV);
			Assert.IsTrue(context.Settings.Keyboard.AllowCtrlX);
			Assert.IsTrue(context.Settings.Keyboard.AllowInjected);

			applicationMonitor.Verify(m => m.Whitelist(It.Is<WhitelistApplication>(a => a.ExecutableName == "AutoHotkey64.exe")), Times.Once);
			logger.Verify(l => l.Warn(It.Is<string>(m => m.Contains("DEBUG MODE"))), Times.Once);
		}

		[TestMethod]
		public void MustWhitelistWithCorrectDetails()
		{
			sut.Perform();

			var whitelisted = new ManualResetEventSlim(false);
			WhitelistApplication application = null;

			applicationMonitor.Setup(m => m.Whitelist(It.IsAny<WhitelistApplication>())).Callback<WhitelistApplication>(a =>
			{
				application = a;
				whitelisted.Set();
			});

			Press();

			Assert.IsTrue(whitelisted.Wait(2000));
			Assert.AreEqual("AutoHotkey64.exe", application.ExecutableName);
			Assert.AreEqual("C:\\Tools\\AutoHotkey", application.ExecutablePath);
			Assert.IsTrue(application.AllowRunning);
			Assert.IsFalse(application.AutoStart);
			Assert.IsFalse(application.ShowInShell);
		}

		[TestMethod]
		public void MustOnlyWhitelistEachInterpreterOnce()
		{
			Environment.SetEnvironmentVariable("SEB_DEBUG_AHK", "\"C:\\a\\AutoHotkey64.exe\" one.ahk|\"C:\\a\\AutoHotkey64.exe\" two.ahk|\"C:\\b\\AutoHotkey32.exe\" three.ahk");

			sut.Perform();

			var warned = new ManualResetEventSlim(false);

			logger.Setup(l => l.Warn(It.Is<string>(m => m.Contains("DEBUG MODE")))).Callback(() => warned.Set());

			Press();

			Assert.IsTrue(warned.Wait(2000));
			applicationMonitor.Verify(m => m.Whitelist(It.IsAny<WhitelistApplication>()), Times.Exactly(2));
		}

		[TestMethod]
		public void MustOnlyActivateOnce()
		{
			sut.Perform();

			var warned = new ManualResetEventSlim(false);

			logger.Setup(l => l.Warn(It.Is<string>(m => m.Contains("DEBUG MODE")))).Callback(() => warned.Set());

			Press();
			Assert.IsTrue(warned.Wait(2000));

			Release();
			Press();

			// Give a possible (incorrect) second activation a moment to happen before asserting it did not.
			Thread.Sleep(300);

			clipboard.Verify(c => c.Initialize(ClipboardPolicy.Allow), Times.Once);
			clipboard.Verify(c => c.Terminate(), Times.Once);
			applicationMonitor.Verify(m => m.Whitelist(It.IsAny<WhitelistApplication>()), Times.Once);
			logger.Verify(l => l.Warn(It.Is<string>(m => m.Contains("DEBUG MODE"))), Times.Once);
		}

		[TestMethod]
		public void MustNotTouchAutoHotkeyWithoutNewDesktop()
		{
			context.Settings.Security.KioskMode = KioskMode.None;
			sut.Perform();

			var activated = new ManualResetEventSlim(false);

			logger.Setup(l => l.Warn(It.Is<string>(m => m.Contains("DEBUG MODE")))).Callback(() => activated.Set());

			Press();

			Assert.IsTrue(activated.Wait(2000), "Clipboard allowance must not depend on the kiosk mode.");

			clipboard.Verify(c => c.Initialize(ClipboardPolicy.Allow), Times.Once);
			applicationMonitor.Verify(m => m.Whitelist(It.IsAny<WhitelistApplication>()), Times.Never);
		}

		[TestMethod]
		public void MustNeverFailTheStartup()
		{
			nativeMethods.Setup(n => n.RegisterKeyboardHook(It.IsAny<KeyboardHookCallback>())).Throws<InvalidOperationException>();

			var result = sut.Perform();

			Assert.AreEqual(OperationResult.Success, result);
			logger.Verify(l => l.Error(It.IsAny<string>(), It.IsAny<Exception>()), Times.Once);
		}

		[TestMethod]
		public void MustNeverFailActivation()
		{
			context.Settings = null;
			sut.Perform();

			var errored = new ManualResetEventSlim(false);

			logger.Setup(l => l.Error(It.IsAny<string>(), It.IsAny<Exception>())).Callback(() => errored.Set());

			Press();

			Assert.IsTrue(errored.Wait(2000));
		}

		[TestMethod]
		public void MustNeverFailTheShutdown()
		{
			nativeMethods.Setup(n => n.DeregisterKeyboardHook(It.IsAny<Guid>())).Throws<InvalidOperationException>();
			sut.Perform();

			Assert.AreEqual(OperationResult.Success, sut.Revert());
		}

		[TestMethod]
		public void MustSurviveRevertWithoutPerform()
		{
			Assert.AreEqual(OperationResult.Success, sut.Revert());
		}

		[TestMethod]
		public void MustDeregisterHotkeyOnRevert()
		{
			sut.Perform();

			var result = sut.Revert();

			Assert.AreEqual(OperationResult.Success, result);
			nativeMethods.Verify(n => n.DeregisterKeyboardHook(hookId), Times.Once);
		}

#if !DEBUG
		[TestMethod]
		public void MustNotEvenRegisterTheHotkeyWhenDebugModeIsDisabled()
		{
			DebugModeConfiguration.ReadRegistryValue = () => 0;

			var result = sut.Perform();

			Assert.AreEqual(OperationResult.Success, result);
			nativeMethods.Verify(n => n.RegisterKeyboardHook(It.IsAny<KeyboardHookCallback>()), Times.Never);
		}
#endif

		private void Press()
		{
			keyPress(LEFT_CTRL, KeyModifier.None, KeyState.Pressed);
			keyPress(LEFT_SHIFT, KeyModifier.None, KeyState.Pressed);
			keyPress(LEFT_ALT, KeyModifier.None, KeyState.Pressed);
			keyPress(F12, KeyModifier.None, KeyState.Pressed);
		}

		private void Release()
		{
			keyPress(F12, KeyModifier.None, KeyState.Released);
			keyPress(LEFT_ALT, KeyModifier.None, KeyState.Released);
			keyPress(LEFT_SHIFT, KeyModifier.None, KeyState.Released);
			keyPress(LEFT_CTRL, KeyModifier.None, KeyState.Released);
		}
	}
}
