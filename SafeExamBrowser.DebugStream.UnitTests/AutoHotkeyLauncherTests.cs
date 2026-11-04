/*
 * Copyright (c) 2026 ETH Zürich, IT Services
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using SafeExamBrowser.DebugStream.Tools;
using SafeExamBrowser.Logging.Contracts;

namespace SafeExamBrowser.DebugStream.UnitTests
{
	[TestClass]
	public class AutoHotkeyLauncherTests
	{
		[TestMethod]
		public void MustSplitQuotedCommandLine()
		{
			Assert.IsTrue(AutoHotkeyLauncher.TrySplitCommandLine("\"C:\\Program Files\\AutoHotkey\\v2\\AutoHotkey64.exe\" \"C:\\my scripts\\a.ahk\" /ErrorStdOut", out var executable, out var arguments));
			Assert.AreEqual("C:\\Program Files\\AutoHotkey\\v2\\AutoHotkey64.exe", executable);
			Assert.AreEqual("\"C:\\my scripts\\a.ahk\" /ErrorStdOut", arguments);
		}

		[TestMethod]
		public void MustSplitUnquotedCommandLine()
		{
			Assert.IsTrue(AutoHotkeyLauncher.TrySplitCommandLine("  C:\\ahk\\AutoHotkey.exe   script.ahk ", out var executable, out var arguments));
			Assert.AreEqual("C:\\ahk\\AutoHotkey.exe", executable);
			Assert.AreEqual("script.ahk", arguments);
		}

		[TestMethod]
		public void MustSplitCommandLineWithoutArguments()
		{
			Assert.IsTrue(AutoHotkeyLauncher.TrySplitCommandLine("\"C:\\ahk\\Script.exe\"", out var executable, out var arguments));
			Assert.AreEqual("C:\\ahk\\Script.exe", executable);
			Assert.AreEqual(string.Empty, arguments);

			Assert.IsTrue(AutoHotkeyLauncher.TrySplitCommandLine("C:\\ahk\\Script.exe", out executable, out arguments));
			Assert.AreEqual(string.Empty, arguments);
		}

		[TestMethod]
		public void MustRejectInvalidCommandLines()
		{
			foreach (var line in new[] { null, "", "   ", "\"unterminated", "\"\" args" })
			{
				Assert.IsFalse(AutoHotkeyLauncher.TrySplitCommandLine(line, out _, out _), $"'{line}'");
			}
		}

		[TestMethod]
		public void MustParseMultipleConfiguredCommands()
		{
			var commands = AutoHotkeyLauncher.ParseConfiguration("\"C:\\a\\AutoHotkey64.exe\" one.ahk||  |\"C:\\b\\AutoHotkey32.exe\" two.ahk");

			Assert.HasCount(2, commands);
			Assert.AreEqual("one.ahk", commands[0].Arguments);
			Assert.AreEqual("C:\\b\\AutoHotkey32.exe", commands[1].Executable);
		}

		[TestMethod]
		public void MustPreferConfigurationOverDiscovery()
		{
			var sut = new AutoHotkeyLauncher(new Mock<ILogger>().Object, name => name == "SEB_DEBUG_AHK" ? "\"C:\\a\\AutoHotkey64.exe\" configured.ahk" : null);
			var commands = sut.Discover();

			Assert.HasCount(1, commands);
			Assert.AreEqual("configured.ahk", commands[0].Arguments);
		}

		[TestMethod]
		public void MustRecognizeInterpreters()
		{
			Assert.IsTrue(AutoHotkeyLauncher.IsAutoHotkeyInterpreter("AutoHotkey.exe"));
			Assert.IsTrue(AutoHotkeyLauncher.IsAutoHotkeyInterpreter("AutoHotkey64.exe"));
			Assert.IsTrue(AutoHotkeyLauncher.IsAutoHotkeyInterpreter("autohotkeyU32.EXE"));
			Assert.IsFalse(AutoHotkeyLauncher.IsAutoHotkeyInterpreter("notepad.exe"));
			Assert.IsFalse(AutoHotkeyLauncher.IsAutoHotkeyInterpreter("MyAutoHotkey.exe"));
			Assert.IsFalse(AutoHotkeyLauncher.IsAutoHotkeyInterpreter(null));
		}

		[TestMethod]
		public void MustNeverThrowWhenStarting()
		{
			var sut = new AutoHotkeyLauncher(new Mock<ILogger>().Object, _ => null);

			sut.Start(new[] { new AutoHotkeyCommand { Executable = "C:\\does\\not\\exist\\AutoHotkey.exe", Arguments = "x.ahk" } });
			sut.Start(null);
			sut.Dispose();
		}
	}
}
