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
using SafeExamBrowser.Core.Contracts.OperationModel;
using SafeExamBrowser.DebugStream;
using SafeExamBrowser.DebugStream.Host;
using SafeExamBrowser.Logging.Contracts;
using SafeExamBrowser.Runtime.Operations.Bootstrap;

namespace SafeExamBrowser.Runtime.UnitTests.Operations.Bootstrap
{
	[TestClass]
	public class DebugStreamHostOperationTests
	{
		private Mock<ILogger> logger;
		private StreamHost host;
		private DebugStreamHostOperation sut;

		[TestInitialize]
		public void Initialize()
		{
			logger = new Mock<ILogger>();
			host = new StreamHost(logger.Object, Guid.NewGuid());
			sut = new DebugStreamHostOperation(host, logger.Object);
		}

		[TestCleanup]
		public void Cleanup()
		{
			host.Stop();
			DebugModeConfiguration.ReadRegistryValue = () => Microsoft.Win32.Registry.GetValue(DebugModeConfiguration.REGISTRY_KEY, DebugModeConfiguration.REGISTRY_VALUE, 0);
		}

		[TestMethod]
		public void MustSucceedAndNeverThrow()
		{
			Assert.AreEqual(OperationResult.Success, sut.Perform());
			Assert.AreEqual(OperationResult.Success, sut.Revert());
		}

#if DEBUG
		[TestMethod]
		public void MustStartTheHostInADebugBuild()
		{
			sut.Perform();

			logger.Verify(l => l.Info(It.Is<string>(m => m.Contains("listening"))), Times.Once);
		}
#else
		[TestMethod]
		public void MustNotStartTheHostWhenDebugModeIsDisabled()
		{
			DebugModeConfiguration.ReadRegistryValue = () => 0;

			sut.Perform();

			logger.Verify(l => l.Warn(It.IsAny<string>()), Times.Never);
			logger.Verify(l => l.Info(It.Is<string>(m => m.Contains("listening"))), Times.Never);
		}

		[TestMethod]
		public void MustStartTheHostWhenDebugModeIsEnabledByInstallation()
		{
			DebugModeConfiguration.ReadRegistryValue = () => 1;

			sut.Perform();

			logger.Verify(l => l.Info(It.Is<string>(m => m.Contains("listening"))), Times.Once);
		}
#endif
	}
}
