/*
 * Copyright (c) 2026 ETH Zürich, IT Services
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SafeExamBrowser.DebugStream.Protocol;

namespace SafeExamBrowser.DebugStream.UnitTests
{
	[TestClass]
	public class ProtocolChannelTests
	{
		[TestMethod]
		public void MustTransferActiveAndFocus()
		{
			foreach (var value in new[] { true, false })
			{
				Assert.AreEqual(value, Receive(c => c.SendActive(value)).ParseActive());
				Assert.AreEqual(value, Receive(c => c.SendFocus(value)).ParseFocus());
			}

			Assert.AreEqual(MessageType.Active, Receive(c => c.SendActive(true)).Type);
			Assert.AreEqual(MessageType.Focus, Receive(c => c.SendFocus(true)).Type);
		}

		[TestMethod]
		public void MustTransferEmptyWindowList()
		{
			var message = Receive(c => c.SendWindowList(new List<WindowEntry>()));

			Assert.AreEqual(MessageType.WindowList, message.Type);
			Assert.IsEmpty(message.ParseWindowList());
			Assert.IsEmpty(Receive(c => c.SendWindowList(null)).ParseWindowList());
		}

		[TestMethod]
		public void MustTransferFrameWithPixels()
		{
			var header = new FrameHeader { FullWidth = 20, FullHeight = 10, X = 3, Y = 2, Width = 4, Height = 5 };
			var pixels = Enumerable.Range(0, header.PixelByteCount).Select(i => (byte) (i * 7)).ToArray();
			var message = Receive(c => c.SendFrame(header, pixels));
			var parsed = message.ParseFrame();

			Assert.AreEqual(MessageType.Frame, message.Type);
			Assert.AreEqual(header.FullWidth, parsed.FullWidth);
			Assert.AreEqual(header.FullHeight, parsed.FullHeight);
			Assert.AreEqual(header.X, parsed.X);
			Assert.AreEqual(header.Y, parsed.Y);
			Assert.AreEqual(header.Width, parsed.Width);
			Assert.AreEqual(header.Height, parsed.Height);
			Assert.AreEqual(FrameHeader.Size + header.PixelByteCount, message.Payload.Length);
			CollectionAssert.AreEqual(pixels, message.Payload.Skip(FrameHeader.Size).ToArray());
		}

		[TestMethod]
		public void MustTransferFrameWithoutPixels()
		{
			var header = new FrameHeader { FullWidth = 20, FullHeight = 10 };
			var parsed = Receive(c => c.SendFrame(header, new byte[0])).ParseFrame();

			Assert.AreEqual(0, parsed.Width);
			Assert.AreEqual(0, parsed.Height);
		}

		[TestMethod]
		public void MustTransferHello()
		{
			var message = Receive(c => c.SendHello(4711, "Ünicode ✓ info"));

			message.ParseHello(out var version, out var processId, out var info);

			Assert.AreEqual(MessageType.Hello, message.Type);
			Assert.AreEqual(StreamProtocol.Version, version);
			Assert.AreEqual(4711, processId);
			Assert.AreEqual("Ünicode ✓ info", info);
		}

		[TestMethod]
		public void MustTransferKeys()
		{
			foreach (var character in new[] { 'a', 'ü', '€', '\r' })
			{
				var sent = new KeyEvent
				{
					Action = KeyAction.Char,
					Character = character,
					IsRepeat = true,
					Modifiers = KeyModifiers.Ctrl | KeyModifiers.Alt,
					VirtualKey = 0x41
				};
				var message = Receive(c => c.SendKey(sent));
				var received = message.ParseKey();

				Assert.AreEqual(MessageType.Key, message.Type);
				Assert.AreEqual(sent.Action, received.Action);
				Assert.AreEqual(sent.Character, received.Character);
				Assert.AreEqual(sent.IsRepeat, received.IsRepeat);
				Assert.AreEqual(sent.Modifiers, received.Modifiers);
				Assert.AreEqual(sent.VirtualKey, received.VirtualKey);
			}

			foreach (var action in new[] { KeyAction.Down, KeyAction.Up, KeyAction.Char })
			{
				Assert.AreEqual(action, Receive(c => c.SendKey(new KeyEvent { Action = action })).ParseKey().Action);
			}
		}

		[TestMethod]
		public void MustTransferMouse()
		{
			var sent = new MouseEvent { Action = MouseAction.Wheel, Delta = -120, Flags = 0x0009, X = 1234, Y = 567 };
			var message = Receive(c => c.SendMouse(sent));
			var received = message.ParseMouse();

			Assert.AreEqual(MessageType.Mouse, message.Type);
			Assert.AreEqual(sent.Action, received.Action);
			Assert.AreEqual(sent.Delta, received.Delta);
			Assert.AreEqual(sent.Flags, received.Flags);
			Assert.AreEqual(sent.X, received.X);
			Assert.AreEqual(sent.Y, received.Y);
		}

		[TestMethod]
		public void MustTransferMessagesWithoutPayload()
		{
			Assert.AreEqual(MessageType.Bye, Receive(c => c.SendBye()).Type);
			Assert.AreEqual(MessageType.NewWindow, Receive(c => c.SendNewWindow()).Type);
			Assert.AreEqual(MessageType.RefreshWindows, Receive(c => c.SendRefreshWindows()).Type);
			Assert.IsEmpty(Receive(c => c.SendBye()).Payload);
		}

		[TestMethod]
		public void MustTransferSelectedWindow()
		{
			Assert.AreEqual(0x7FFF_0000_1234L, Receive(c => c.SendSelectWindow(0x7FFF_0000_1234L)).ParseSelectWindow());
			Assert.AreEqual(MessageType.SelectWindow, Receive(c => c.SendSelectWindow(1)).Type);
		}

		[TestMethod]
		public void MustTransferStatus()
		{
			Assert.AreEqual("Waiting for Firefox... ✓", Receive(c => c.SendStatus("Waiting for Firefox... ✓")).ParseStatus());
			Assert.AreEqual(string.Empty, Receive(c => c.SendStatus(null)).ParseStatus());
		}

		[TestMethod]
		public void MustTransferWindowList()
		{
			var windows = new List<WindowEntry>
			{
				new WindowEntry { Handle = 0x1234_5678_9ABC, IsSelected = true, Title = "GitHub — Mozilla Firefox" },
				new WindowEntry { Handle = 42, IsSelected = false, Title = "Ünïcödé ✓" },
				new WindowEntry { Handle = 7, IsSelected = false, Title = null }
			};
			var message = Receive(c => c.SendWindowList(windows));
			var received = message.ParseWindowList();

			Assert.AreEqual(MessageType.WindowList, message.Type);
			Assert.HasCount(3, received);
			Assert.AreEqual(windows[0].Handle, received[0].Handle);
			Assert.IsTrue(received[0].IsSelected);
			Assert.AreEqual(windows[0].Title, received[0].Title);
			Assert.AreEqual(windows[1].Title, received[1].Title);
			Assert.IsFalse(received[1].IsSelected);
			Assert.AreEqual(string.Empty, received[2].Title);
		}

		[TestMethod]
		public void MustReceiveMessagesInOrder()
		{
			var stream = new MemoryStream();
			var sender = new ProtocolChannel(stream);

			sender.SendStatus("first");
			sender.SendFrame(new FrameHeader { FullWidth = 2, FullHeight = 1, Width = 2, Height = 1 }, new byte[8]);
			sender.SendStatus("second");
			stream.Position = 0;

			var sut = new ProtocolChannel(stream);

			Assert.IsTrue(sut.TryReceive(out var first));
			Assert.IsTrue(sut.TryReceive(out var frame));
			Assert.IsTrue(sut.TryReceive(out var second));
			Assert.IsFalse(sut.TryReceive(out _));

			Assert.AreEqual("first", first.ParseStatus());
			Assert.AreEqual(MessageType.Frame, frame.Type);
			Assert.AreEqual("second", second.ParseStatus());
		}

		[TestMethod]
		public void MustNotInterleaveConcurrentlySentMessages()
		{
			const int THREADS = 8;
			const int MESSAGES = 150;

			var stream = new MemoryStream();
			var sender = new ProtocolChannel(stream);
			var pixels = new byte[4 * 4 * 4];
			var threads = Enumerable.Range(0, THREADS).Select(t => new Thread(() =>
			{
				for (var i = 0; i < MESSAGES; i++)
				{
					sender.SendStatus($"thread {t}, message {i}");
					sender.SendFrame(new FrameHeader { FullWidth = 4, FullHeight = 4, Width = 4, Height = 4 }, pixels);
				}
			})).ToList();

			threads.ForEach(t => t.Start());
			threads.ForEach(t => t.Join());
			stream.Position = 0;

			var sut = new ProtocolChannel(stream);
			var statuses = new List<string>();
			var frames = 0;

			while (sut.TryReceive(out var message))
			{
				if (message.Type == MessageType.Status)
				{
					statuses.Add(message.ParseStatus());
				}
				else
				{
					message.ParseFrame();
					frames++;
				}
			}

			Assert.AreEqual(THREADS * MESSAGES, frames);
			Assert.HasCount(THREADS * MESSAGES, statuses);
			Assert.HasCount(THREADS * MESSAGES, statuses.Distinct());
		}

		[TestMethod]
		public void MustRejectMessageWithInvalidLength()
		{
			foreach (var length in new[] { -1, StreamProtocol.MaxPayloadSize + 1, int.MaxValue })
			{
				var sut = new ProtocolChannel(new MemoryStream(BitConverter.GetBytes(length).Concat(new byte[] { (byte) MessageType.Status }).ToArray()));

				Assert.ThrowsExactly<InvalidDataException>(() => sut.TryReceive(out _));
			}
		}

		[TestMethod]
		public void MustRejectTooSmallPixelBuffer()
		{
			var sut = new ProtocolChannel(new MemoryStream());
			var header = new FrameHeader { FullWidth = 10, FullHeight = 10, Width = 10, Height = 10 };

			Assert.ThrowsExactly<ArgumentException>(() => sut.SendFrame(header, new byte[header.PixelByteCount - 1]));
			Assert.ThrowsExactly<ArgumentException>(() => sut.SendFrame(header, null));
		}

		[TestMethod]
		public void MustRejectMissingStream()
		{
			Assert.ThrowsExactly<ArgumentNullException>(() => new ProtocolChannel(null));
		}

		[TestMethod]
		public void MustReportEndOfStream()
		{
			Assert.IsFalse(new ProtocolChannel(new MemoryStream()).TryReceive(out var message));
			Assert.IsNull(message);
		}

		[TestMethod]
		public void MustReportTruncatedHeader()
		{
			Assert.IsFalse(new ProtocolChannel(new MemoryStream(new byte[] { 1, 0, 0 })).TryReceive(out _));
		}

		[TestMethod]
		public void MustReportTruncatedPayload()
		{
			var stream = new MemoryStream();

			new ProtocolChannel(stream).SendStatus("a status which is cut off");
			stream.SetLength(stream.Length - 3);
			stream.Position = 0;

			Assert.IsFalse(new ProtocolChannel(stream).TryReceive(out _));
		}

		[TestMethod]
		public void MustWorkWithStreamsReturningFewBytesAtATime()
		{
			var stream = new MemoryStream();

			new ProtocolChannel(stream).SendStatus("delivered byte by byte");
			stream.Position = 0;

			var sut = new ProtocolChannel(new TrickleStream(stream));

			Assert.IsTrue(sut.TryReceive(out var message));
			Assert.AreEqual("delivered byte by byte", message.ParseStatus());
		}

		private static Message Receive(Action<ProtocolChannel> send)
		{
			var stream = new MemoryStream();

			send(new ProtocolChannel(stream));
			stream.Position = 0;

			Assert.IsTrue(new ProtocolChannel(stream).TryReceive(out var message));

			return message;
		}

		/// <summary>
		/// Behaves like a pipe, which may return less data than requested.
		/// </summary>
		private class TrickleStream : Stream
		{
			private readonly Stream stream;

			public override bool CanRead => true;
			public override bool CanSeek => false;
			public override bool CanWrite => false;
			public override long Length => stream.Length;
			public override long Position { get => stream.Position; set => stream.Position = value; }

			public TrickleStream(Stream stream)
			{
				this.stream = stream;
			}

			public override void Flush()
			{
			}

			public override int Read(byte[] buffer, int offset, int count)
			{
				return stream.Read(buffer, offset, Math.Min(1, count));
			}

			public override long Seek(long offset, SeekOrigin origin)
			{
				throw new NotSupportedException();
			}

			public override void SetLength(long value)
			{
				throw new NotSupportedException();
			}

			public override void Write(byte[] buffer, int offset, int count)
			{
				throw new NotSupportedException();
			}
		}
	}
}
