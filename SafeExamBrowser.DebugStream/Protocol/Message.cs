using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SafeExamBrowser.DebugStream.Protocol
{
	/// <summary>
	/// A message received via a <see cref="ProtocolChannel"/>. The payload is only interpreted on demand via the parse methods, all
	/// of which throw an <see cref="InvalidDataException"/> if the payload does not match the type of the message.
	/// </summary>
	public sealed class Message
	{
		public byte[] Payload { get; }
		public MessageType Type { get; }

		public Message(MessageType type, byte[] payload)
		{
			Payload = payload ?? throw new ArgumentNullException(nameof(payload));
			Type = type;
		}

		public bool ParseActive()
		{
			return Parse(r => r.ReadBoolean());
		}

		public bool ParseFocus()
		{
			return Parse(r => r.ReadBoolean());
		}

		/// <summary>
		/// Parses the header of a frame message. The pixels (BGRA, tightly packed, rows from top to bottom) start at
		/// <see cref="FrameHeader.Size"/> in the <see cref="Payload"/>.
		/// </summary>
		public FrameHeader ParseFrame()
		{
			if (Type != MessageType.Frame)
			{
				throw new InvalidDataException($"Cannot parse message of type {Type} as frame!");
			}

			if (Payload.Length < FrameHeader.Size)
			{
				throw new InvalidDataException("The frame message is too short!");
			}

			var header = Parse(r => new FrameHeader
			{
				FullWidth = r.ReadInt32(),
				FullHeight = r.ReadInt32(),
				X = r.ReadInt32(),
				Y = r.ReadInt32(),
				Width = r.ReadInt32(),
				Height = r.ReadInt32()
			});

			var validFull = IsValidDimension(header.FullWidth) && IsValidDimension(header.FullHeight);
			var validRect = header.Width >= 0 && header.Height >= 0 && header.X >= 0 && header.Y >= 0
				&& (long) header.X + header.Width <= header.FullWidth
				&& (long) header.Y + header.Height <= header.FullHeight;

			if (!validFull || !validRect)
			{
				throw new InvalidDataException("The frame message contains invalid dimensions!");
			}

			if (Payload.Length != FrameHeader.Size + (long) header.Width * header.Height * 4)
			{
				throw new InvalidDataException("The size of the frame message does not match its dimensions!");
			}

			return header;
		}

		public void ParseHello(out ushort version, out int processId, out string info)
		{
			var hello = Parse(r => new { Magic = r.ReadUInt32(), Version = r.ReadUInt16(), ProcessId = r.ReadInt32(), Info = r.ReadString() });

			if (hello.Magic != StreamProtocol.Magic)
			{
				throw new InvalidDataException("The handshake does not start with the expected magic number!");
			}

			info = hello.Info;
			processId = hello.ProcessId;
			version = hello.Version;
		}

		public KeyEvent ParseKey()
		{
			return Parse(r => new KeyEvent
			{
				Action = (KeyAction) r.ReadByte(),
				VirtualKey = r.ReadInt32(),
				Character = (char) r.ReadUInt16(),
				Modifiers = (KeyModifiers) r.ReadByte(),
				IsRepeat = r.ReadBoolean()
			});
		}

		public MouseEvent ParseMouse()
		{
			return Parse(r => new MouseEvent
			{
				Action = (MouseAction) r.ReadByte(),
				X = r.ReadInt32(),
				Y = r.ReadInt32(),
				Delta = r.ReadInt32(),
				Flags = r.ReadInt32()
			});
		}

		public long ParseSelectWindow()
		{
			return Parse(r => r.ReadInt64());
		}

		public string ParseStatus()
		{
			return Parse(r => r.ReadString());
		}

		public IList<WindowEntry> ParseWindowList()
		{
			return Parse(r =>
			{
				var count = r.ReadInt32();
				var entries = new List<WindowEntry>();

				if (count < 0 || count > 1024)
				{
					throw new InvalidDataException("The window list contains an invalid number of entries!");
				}

				for (var i = 0; i < count; i++)
				{
					entries.Add(new WindowEntry { Handle = r.ReadInt64(), IsSelected = r.ReadBoolean(), Title = r.ReadString() });
				}

				return entries;
			});
		}

		private static bool IsValidDimension(int value)
		{
			return value > 0 && value <= StreamProtocol.MaxDimension;
		}

		private T Parse<T>(Func<BinaryReader, T> parse)
		{
			try
			{
				using (var stream = new MemoryStream(Payload, false))
				using (var reader = new BinaryReader(stream, Encoding.UTF8))
				{
					return parse(reader);
				}
			}
			catch (EndOfStreamException e)
			{
				throw new InvalidDataException($"The payload of the {Type} message is too short!", e);
			}
		}
	}
}
