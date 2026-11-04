/*
 * Copyright (c) 2026 ETH Zürich, IT Services
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SafeExamBrowser.DebugStream.Protocol;

namespace SafeExamBrowser.DebugStream.UnitTests
{
	[TestClass]
	public class MessageTests
	{
		[TestMethod]
		public void MustRejectFrameOfDifferentType()
		{
			var sut = new Message(MessageType.Status, FramePayload(10, 10, 0, 0, 1, 1));

			Assert.ThrowsExactly<InvalidDataException>(() => sut.ParseFrame());
		}

		[TestMethod]
		public void MustRejectFrameWithInvalidDimensions()
		{
			var invalid = new[]
			{
				FramePayload(0, 10, 0, 0, 0, 0),
				FramePayload(10, 0, 0, 0, 0, 0),
				FramePayload(-5, 10, 0, 0, 0, 0),
				FramePayload(StreamProtocol.MaxDimension + 1, 10, 0, 0, 0, 0),
				FramePayload(10, 10, -1, 0, 1, 1),
				FramePayload(10, 10, 0, -1, 1, 1),
				FramePayload(10, 10, 0, 0, -1, 1),
				FramePayload(10, 10, 9, 0, 2, 1),
				FramePayload(10, 10, 0, 9, 1, 2),
				FramePayload(10, 10, 0, 0, 11, 1),
				FramePayload(int.MaxValue, int.MaxValue, int.MaxValue, 0, 1, 1)
			};

			foreach (var payload in invalid)
			{
				Assert.ThrowsExactly<InvalidDataException>(() => new Message(MessageType.Frame, payload).ParseFrame());
			}
		}

		[TestMethod]
		public void MustRejectFrameWithMismatchingPixelData()
		{
			var tooShort = FramePayload(10, 10, 0, 0, 2, 2, pixelBytes: 15);
			var tooLong = FramePayload(10, 10, 0, 0, 2, 2, pixelBytes: 17);

			Assert.ThrowsExactly<InvalidDataException>(() => new Message(MessageType.Frame, tooShort).ParseFrame());
			Assert.ThrowsExactly<InvalidDataException>(() => new Message(MessageType.Frame, tooLong).ParseFrame());
		}

		[TestMethod]
		public void MustAcceptFrameCoveringTheWholeImage()
		{
			var sut = new Message(MessageType.Frame, FramePayload(8, 6, 0, 0, 8, 6));
			var header = sut.ParseFrame();

			Assert.AreEqual(8, header.FullWidth);
			Assert.AreEqual(6, header.FullHeight);
			Assert.AreEqual(8 * 6 * 4, header.PixelByteCount);
		}

		[TestMethod]
		public void MustAcceptFrameAtTheBottomRightEdge()
		{
			var sut = new Message(MessageType.Frame, FramePayload(8, 6, 7, 5, 1, 1));

			Assert.AreEqual(7, sut.ParseFrame().X);
		}

		[TestMethod]
		public void MustRejectHelloWithWrongMagicNumber()
		{
			var sut = new Message(MessageType.Hello, Payload(w =>
			{
				w.Write(0xDEADBEEF);
				w.Write(StreamProtocol.Version);
				w.Write(1);
				w.Write("info");
			}));

			Assert.ThrowsExactly<InvalidDataException>(() => sut.ParseHello(out _, out _, out _));
		}

		[TestMethod]
		public void MustRejectTruncatedPayloads()
		{
			var empty = new byte[0];

			Assert.ThrowsExactly<InvalidDataException>(() => new Message(MessageType.Active, empty).ParseActive());
			Assert.ThrowsExactly<InvalidDataException>(() => new Message(MessageType.Focus, empty).ParseFocus());
			Assert.ThrowsExactly<InvalidDataException>(() => new Message(MessageType.Hello, empty).ParseHello(out _, out _, out _));
			Assert.ThrowsExactly<InvalidDataException>(() => new Message(MessageType.Key, empty).ParseKey());
			Assert.ThrowsExactly<InvalidDataException>(() => new Message(MessageType.Mouse, new byte[3]).ParseMouse());
			Assert.ThrowsExactly<InvalidDataException>(() => new Message(MessageType.SelectWindow, new byte[7]).ParseSelectWindow());
			Assert.ThrowsExactly<InvalidDataException>(() => new Message(MessageType.Status, empty).ParseStatus());
			Assert.ThrowsExactly<InvalidDataException>(() => new Message(MessageType.WindowList, empty).ParseWindowList());
			Assert.ThrowsExactly<InvalidDataException>(() => new Message(MessageType.Frame, new byte[FrameHeader.Size - 1]).ParseFrame());
		}

		[TestMethod]
		public void MustRejectWindowListWithInvalidCount()
		{
			foreach (var count in new[] { -1, 1025, int.MaxValue })
			{
				var sut = new Message(MessageType.WindowList, Payload(w => w.Write(count)));

				Assert.ThrowsExactly<InvalidDataException>(() => sut.ParseWindowList());
			}
		}

		[TestMethod]
		public void MustRejectWindowListWithMissingEntries()
		{
			var sut = new Message(MessageType.WindowList, Payload(w => w.Write(2)));

			Assert.ThrowsExactly<InvalidDataException>(() => sut.ParseWindowList());
		}

		[TestMethod]
		public void MustRejectMissingPayload()
		{
			Assert.ThrowsExactly<ArgumentNullException>(() => new Message(MessageType.Bye, null));
		}

		private static byte[] FramePayload(int fullWidth, int fullHeight, int x, int y, int width, int height, int? pixelBytes = default)
		{
			return Payload(w =>
			{
				w.Write(fullWidth);
				w.Write(fullHeight);
				w.Write(x);
				w.Write(y);
				w.Write(width);
				w.Write(height);

				// Exactly the amount of pixel data the dimensions suggest, unless explicitly requested otherwise. Invalid dimensions
				// are rejected before the pixel data is looked at, so these do not need to (and must not) allocate gigabytes here.
				var exact = (long) width * height * 4;
				var count = pixelBytes ?? (exact >= 0 && exact <= 1 << 20 ? (int) exact : 0);

				w.Write(new byte[count]);
			});
		}

		private static byte[] Payload(Action<BinaryWriter> write)
		{
			using (var stream = new MemoryStream())
			using (var writer = new BinaryWriter(stream))
			{
				write(writer);
				writer.Flush();

				return stream.ToArray();
			}
		}
	}
}
