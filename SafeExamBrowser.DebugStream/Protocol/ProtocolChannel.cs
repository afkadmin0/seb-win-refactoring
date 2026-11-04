using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SafeExamBrowser.DebugStream.Protocol
{
	/// <summary>
	/// Frames the messages of the stream protocol on top of any bidirectional stream (a named pipe in production).
	///
	/// Wire format of each message: <c>[int32 payloadLength][byte type][payload]</c>, all integers little endian.
	///
	/// Sending is thread-safe (messages are never interleaved). Receiving must only be done by a single thread. For named pipes the
	/// underlying handle must have been opened with <c>PipeOptions.Asynchronous</c>, otherwise a pending read blocks all writes.
	/// </summary>
	public sealed class ProtocolChannel
	{
		private const int HEADER_SIZE = sizeof(int) + sizeof(byte);

		private readonly MemoryStream buffer = new MemoryStream();
		private readonly byte[] receiveHeader = new byte[HEADER_SIZE];
		private readonly Stream stream;
		private readonly object writeLock = new object();
		private readonly BinaryWriter writer;

		public ProtocolChannel(Stream stream)
		{
			this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
			this.writer = new BinaryWriter(buffer, Encoding.UTF8);
		}

		public void SendActive(bool active)
		{
			Send(MessageType.Active, w => w.Write(active));
		}

		public void SendBye()
		{
			Send(MessageType.Bye, w => { });
		}

		public void SendFocus(bool focused)
		{
			Send(MessageType.Focus, w => w.Write(focused));
		}

		/// <summary>
		/// Sends the given area of the pixels, which must be BGRA, tightly packed and exactly <see cref="FrameHeader.PixelByteCount"/> long.
		/// </summary>
		public void SendFrame(FrameHeader header, byte[] pixels)
		{
			if (pixels == null || pixels.Length < header.PixelByteCount)
			{
				throw new ArgumentException("The pixel buffer is smaller than the dimensions of the frame suggest!", nameof(pixels));
			}

			lock (writeLock)
			{
				BeginMessage(MessageType.Frame);
				writer.Write(header.FullWidth);
				writer.Write(header.FullHeight);
				writer.Write(header.X);
				writer.Write(header.Y);
				writer.Write(header.Width);
				writer.Write(header.Height);
				EndMessage(header.PixelByteCount);

				stream.Write(buffer.GetBuffer(), 0, (int) buffer.Length);
				stream.Write(pixels, 0, header.PixelByteCount);
			}
		}

		public void SendHello(int processId, string info)
		{
			Send(MessageType.Hello, w =>
			{
				w.Write(StreamProtocol.Magic);
				w.Write(StreamProtocol.Version);
				w.Write(processId);
				w.Write(info ?? string.Empty);
			});
		}

		public void SendKey(KeyEvent e)
		{
			Send(MessageType.Key, w =>
			{
				w.Write((byte) e.Action);
				w.Write(e.VirtualKey);
				w.Write((ushort) e.Character);
				w.Write((byte) e.Modifiers);
				w.Write(e.IsRepeat);
			});
		}

		public void SendMouse(MouseEvent e)
		{
			Send(MessageType.Mouse, w =>
			{
				w.Write((byte) e.Action);
				w.Write(e.X);
				w.Write(e.Y);
				w.Write(e.Delta);
				w.Write(e.Flags);
			});
		}

		public void SendNewWindow()
		{
			Send(MessageType.NewWindow, w => { });
		}

		public void SendRefreshWindows()
		{
			Send(MessageType.RefreshWindows, w => { });
		}

		public void SendSelectWindow(long handle)
		{
			Send(MessageType.SelectWindow, w => w.Write(handle));
		}

		public void SendStatus(string text)
		{
			Send(MessageType.Status, w => w.Write(text ?? string.Empty));
		}

		public void SendWindowList(IEnumerable<WindowEntry> entries)
		{
			var list = (entries ?? Enumerable.Empty<WindowEntry>()).ToList();

			Send(MessageType.WindowList, w =>
			{
				w.Write(list.Count);

				foreach (var entry in list)
				{
					w.Write(entry.Handle);
					w.Write(entry.IsSelected);
					w.Write(entry.Title ?? string.Empty);
				}
			});
		}

		/// <summary>
		/// Blocks until the next message was received. Returns <c>false</c> if the other side has closed the connection.
		/// </summary>
		public bool TryReceive(out Message message)
		{
			message = default;

			if (!TryReadExactly(receiveHeader, HEADER_SIZE))
			{
				return false;
			}

			var length = BitConverter.ToInt32(receiveHeader, 0);
			var type = (MessageType) receiveHeader[sizeof(int)];

			if (length < 0 || length > StreamProtocol.MaxPayloadSize)
			{
				throw new InvalidDataException($"Received message of type {type} with invalid payload size {length}!");
			}

			var payload = new byte[length];

			if (length > 0 && !TryReadExactly(payload, length))
			{
				return false;
			}

			message = new Message(type, payload);

			return true;
		}

		private void BeginMessage(MessageType type)
		{
			buffer.SetLength(0);
			writer.Write(0); // length placeholder
			writer.Write((byte) type);
		}

		private void EndMessage(int additionalPayloadLength = 0)
		{
			var payloadLength = (int) buffer.Length - HEADER_SIZE + additionalPayloadLength;

			writer.Flush();
			buffer.Position = 0;
			writer.Write(payloadLength);
			writer.Flush();
		}

		private void Send(MessageType type, Action<BinaryWriter> writePayload)
		{
			lock (writeLock)
			{
				BeginMessage(type);
				writePayload(writer);
				EndMessage();

				stream.Write(buffer.GetBuffer(), 0, (int) buffer.Length);
			}
		}

		private bool TryReadExactly(byte[] target, int count)
		{
			var offset = 0;

			while (offset < count)
			{
				var read = stream.Read(target, offset, count - offset);

				if (read <= 0)
				{
					return false;
				}

				offset += read;
			}

			return true;
		}
	}
}
