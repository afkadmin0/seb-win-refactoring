/*
 * Copyright (c) 2026 ETH Zürich, IT Services
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SafeExamBrowser.DebugStream.Protocol;

namespace SafeExamBrowser.DebugStream.UnitTests
{
	[TestClass]
	public class StreamProtocolTests
	{
		[TestMethod]
		public void MustAllowFullFrameOfMaximumSize()
		{
			var header = new FrameHeader { FullWidth = StreamProtocol.MaxDimension, FullHeight = StreamProtocol.MaxDimension, Width = StreamProtocol.MaxDimension, Height = StreamProtocol.MaxDimension };

			Assert.AreEqual(StreamProtocol.MaxPayloadSize, FrameHeader.Size + header.PixelByteCount);
		}

		[TestMethod]
		public void MustDetermineDeterministicPipeName()
		{
			var id = Guid.NewGuid();

			Assert.AreEqual(StreamProtocol.GetPipeName(id), StreamProtocol.GetPipeName(id));
			Assert.AreEqual(StreamProtocol.GetPipeName(id), StreamProtocol.GetPipeName(new Guid(id.ToByteArray())));
		}

		[TestMethod]
		public void MustDeterminePipeNameBoundToRuntime()
		{
			Assert.AreNotEqual(StreamProtocol.GetPipeName(Guid.NewGuid()), StreamProtocol.GetPipeName(Guid.NewGuid()));
		}

		[TestMethod]
		public void MustDetermineValidPipeName()
		{
			var name = StreamProtocol.GetPipeName(Guid.NewGuid());

			Assert.StartsWith("SafeExamBrowser_DebugStream_", name);
			Assert.DoesNotContain("\\", name);
			Assert.DoesNotContain("-", name);
			Assert.IsLessThan(256, name.Length);
		}
	}
}
