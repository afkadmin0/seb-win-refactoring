using System;

namespace SafeExamBrowser.DebugStream.Protocol
{
	public enum MessageType : byte
	{
		// Host -> viewer
		Hello = 1,
		WindowList = 2,
		Frame = 3,
		Status = 4,

		// Viewer -> host
		SelectWindow = 20,
		NewWindow = 21,
		RefreshWindows = 22,
		Mouse = 23,
		Key = 24,
		Focus = 25,
		Active = 26,
		Bye = 27
	}

	public enum MouseAction : byte
	{
		Move = 0,
		LeftDown = 1,
		LeftUp = 2,
		RightDown = 3,
		RightUp = 4,
		MiddleDown = 5,
		MiddleUp = 6,
		Wheel = 7,
		HorizontalWheel = 8,
		Leave = 9
	}

	public enum KeyAction : byte
	{
		Down = 0,
		Up = 1,
		Char = 2
	}

	[Flags]
	public enum KeyModifiers : byte
	{
		None = 0,
		Ctrl = 1,
		Shift = 2,
		Alt = 4
	}

	/// <summary>
	/// Describes a (dirty) rectangle of a frame. <see cref="FullWidth"/> and <see cref="FullHeight"/> are the dimensions of the whole
	/// streamed image, the remaining values the sub-rectangle contained in the message.
	/// </summary>
	public struct FrameHeader
	{
		public const int Size = 6 * sizeof(int);

		public int FullWidth;
		public int FullHeight;
		public int X;
		public int Y;
		public int Width;
		public int Height;

		public int PixelByteCount => Width * Height * 4;
	}

	public struct MouseEvent
	{
		public MouseAction Action;

		/// <summary>
		/// The position in pixels of the streamed image (not of the screen!).
		/// </summary>
		public int X;
		public int Y;

		/// <summary>
		/// The wheel delta, only relevant for the wheel actions.
		/// </summary>
		public int Delta;

		/// <summary>
		/// The state of the mouse buttons and modifiers as defined by the <c>MK_*</c> flags of the <c>WM_MOUSE*</c> messages.
		/// </summary>
		public int Flags;
	}

	public struct KeyEvent
	{
		public KeyAction Action;

		/// <summary>
		/// The Windows virtual key code, only relevant for <see cref="KeyAction.Down"/> and <see cref="KeyAction.Up"/>.
		/// </summary>
		public int VirtualKey;

		/// <summary>
		/// The character, only relevant for <see cref="KeyAction.Char"/>.
		/// </summary>
		public char Character;

		public KeyModifiers Modifiers;

		/// <summary>
		/// Indicates whether the event is a repetition caused by holding down the key.
		/// </summary>
		public bool IsRepeat;
	}

	public sealed class WindowEntry
	{
		public long Handle { get; set; }
		public bool IsSelected { get; set; }
		public string Title { get; set; }

		public override string ToString()
		{
			return string.IsNullOrWhiteSpace(Title) ? $"[{Handle:X}]" : Title;
		}
	}
}
