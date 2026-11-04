/*
 * Copyright (c) 2026 ETH Zürich, IT Services
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using SafeExamBrowser.Configuration.Contracts;
using SafeExamBrowser.Logging.Contracts;
using SafeExamBrowser.SystemComponents.Contracts;

namespace SafeExamBrowser.Logging.UnitTests
{
	[TestClass]
	public class CrashLogTests
	{
		private AppConfig appConfig;
		private Mock<IFileSystem> fileSystem;
		private Mock<ILogger> logger;
		private CrashLog sut;

		[TestInitialize]
		public void Initialize()
		{
			appConfig = new AppConfig { TemporaryDirectory = @"C:\Users\Someone\AppData\Local\SafeExamBrowser\Temp" };
			fileSystem = new Mock<IFileSystem>();
			logger = new Mock<ILogger>();

			sut = new CrashLog(appConfig, fileSystem.Object, logger.Object);
		}

		[TestMethod]
		public void Set_MustCreateTemporaryDirectoryBeforeSaving()
		{
			// Set() runs during application bootstrap, which can happen before any session has ever run and thus before anything
			// else has created the temporary directory (e.g. a completely fresh installation) - it must not assume it already exists.
			var order = 0;
			var created = 0;
			var saved = 0;

			fileSystem.Setup(f => f.CreateDirectory(appConfig.TemporaryDirectory)).Callback(() => created = ++order);
			fileSystem.Setup(f => f.Save(It.IsAny<string>(), It.IsAny<string>())).Callback(() => saved = ++order);

			sut.Set("a.log", "b.log");

			fileSystem.Verify(f => f.CreateDirectory(appConfig.TemporaryDirectory), Times.Once);
			fileSystem.Verify(f => f.Save(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
			Assert.AreEqual(1, created);
			Assert.AreEqual(2, saved);
		}

		[TestMethod]
		public void Set_MustSaveAtCorrectPathWithJoinedContent()
		{
			sut.Set("First.log", "Second.log");

			fileSystem.Verify(f => f.Save($"First.log{System.Environment.NewLine}Second.log", $@"{appConfig.TemporaryDirectory}\CrashLog.txt"), Times.Once);
		}

		[TestMethod]
		public void Set_MustSucceedEvenIfDirectoryAlreadyExists()
		{
			// Directory.CreateDirectory() (which the real IFileSystem.CreateDirectory() implementation wraps) is a no-op if the
			// directory already exists, so this call must always be safe to make, not just on a fresh installation.
			sut.Set("a.log");

			fileSystem.Verify(f => f.CreateDirectory(It.IsAny<string>()), Times.Once);
		}

		[TestMethod]
		public void HasData_MustReflectFileSystem()
		{
			fileSystem.Setup(f => f.Exists(It.IsAny<string>())).Returns(true);
			Assert.IsTrue(sut.HasData);

			fileSystem.Setup(f => f.Exists(It.IsAny<string>())).Returns(false);
			Assert.IsFalse(sut.HasData);
		}

		[TestMethod]
		public void Clear_MustDeleteAtCorrectPath()
		{
			sut.Clear();

			fileSystem.Verify(f => f.Delete($@"{appConfig.TemporaryDirectory}\CrashLog.txt"), Times.Once);
		}

		[TestMethod]
		public void Clear_MustNotRequireDirectoryToExist()
		{
			// Unlike Set(), Clear() must not need to create the directory first - deleting something that was never written is fine.
			sut.Clear();

			fileSystem.Verify(f => f.CreateDirectory(It.IsAny<string>()), Times.Never);
		}

		[TestMethod]
		public void Get_MustReturnOnlyLogFileLines()
		{
			fileSystem.Setup(f => f.TryRead(It.IsAny<string>(), out It.Ref<string>.IsAny))
				.Returns(new TryReadCallback((string path, out string content) =>
				{
					content = string.Join(System.Environment.NewLine, "Runtime.log", "not a log line", "Client.log", "");
					return true;
				}));

			var data = sut.Get();

			CollectionAssert.AreEqual(new[] { "Runtime.log", "Client.log" }, data);
		}

		[TestMethod]
		public void Get_MustReturnEmptyArrayWithoutCrashData()
		{
			fileSystem.Setup(f => f.TryRead(It.IsAny<string>(), out It.Ref<string>.IsAny)).Returns(false);

			var data = sut.Get();

			Assert.IsEmpty(data);
		}

		private delegate bool TryReadCallback(string path, out string content);
	}
}
