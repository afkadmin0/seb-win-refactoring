/*
 * Copyright (c) 2026 ETH Zürich, IT Services
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SafeExamBrowser.DebugStream;

namespace SafeExamBrowser.DebugStream.UnitTests
{
	/// <summary>
	/// Exercises <see cref="DebugModeConfiguration.IsEnabledByInstallation"/> against an injected registry read, not the real
	/// registry (writing HKLM requires administrator rights, which a test run must not depend on). This only covers the
	/// configuration-independent "was the installer flag set" check; <see cref="DebugModeConfiguration.IsEnabled"/> itself hard-codes
	/// <c>true</c> in a Debug build (by design - see its documentation), which is exactly the configuration these tests run in, so it
	/// cannot be meaningfully exercised here.
	/// </summary>
	[TestClass]
	public class DebugModeConfigurationTests
	{
		[TestCleanup]
		public void Cleanup()
		{
			// The delegate is a static, shared gate - every test must restore it, or it leaks into unrelated tests.
			DebugModeConfiguration.ReadRegistryValue = () => Microsoft.Win32.Registry.GetValue(DebugModeConfiguration.REGISTRY_KEY, DebugModeConfiguration.REGISTRY_VALUE, 0);
		}

		[TestMethod]
		public void MustBeEnabledWhenFlagIsOne()
		{
			DebugModeConfiguration.ReadRegistryValue = () => 1;

			Assert.IsTrue(DebugModeConfiguration.IsEnabledByInstallation());
		}

		[TestMethod]
		public void MustBeEnabledForAnyNonZeroInteger()
		{
			DebugModeConfiguration.ReadRegistryValue = () => 42;

			Assert.IsTrue(DebugModeConfiguration.IsEnabledByInstallation());
		}

		[TestMethod]
		public void MustBeDisabledWhenFlagIsZero()
		{
			DebugModeConfiguration.ReadRegistryValue = () => 0;

			Assert.IsFalse(DebugModeConfiguration.IsEnabledByInstallation());
		}

		[TestMethod]
		public void MustBeDisabledWhenValueIsMissing()
		{
			// Registry.GetValue returns the given default (0, an int) when the value does not exist - never null for this call.
			DebugModeConfiguration.ReadRegistryValue = () => 0;

			Assert.IsFalse(DebugModeConfiguration.IsEnabledByInstallation());
		}

		[TestMethod]
		public void MustBeDisabledForUnexpectedValueTypes()
		{
			foreach (object value in new object[] { "1", 1L, 1.0, true, new byte[] { 1 }, null })
			{
				DebugModeConfiguration.ReadRegistryValue = () => value;

				Assert.IsFalse(DebugModeConfiguration.IsEnabledByInstallation(), $"value: {value ?? "null"} ({value?.GetType().Name ?? "null"})");
			}
		}

		[TestMethod]
		public void MustBeDisabledWhenRegistryAccessThrows()
		{
			DebugModeConfiguration.ReadRegistryValue = () => throw new UnauthorizedAccessException("denied");

			Assert.IsFalse(DebugModeConfiguration.IsEnabledByInstallation());
		}

		[TestMethod]
		public void MustMatchTheRegistryLocationWrittenByTheInstaller()
		{
			// Setup\Components\DebugMode.wxs must write to exactly this key and value name.
			Assert.AreEqual(@"HKEY_LOCAL_MACHINE\SOFTWARE\SafeExamBrowser", DebugModeConfiguration.REGISTRY_KEY);
			Assert.AreEqual("DebugMode", DebugModeConfiguration.REGISTRY_VALUE);
		}

#if DEBUG
		[TestMethod]
		public void MustAlwaysBeEnabledInADebugBuild()
		{
			DebugModeConfiguration.ReadRegistryValue = () => 0;

			Assert.IsTrue(DebugModeConfiguration.IsEnabled, "A Debug build must enable the tools regardless of the installation flag.");
		}
#endif
	}
}
