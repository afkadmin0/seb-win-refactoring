using System;

namespace SafeExamBrowser.DebugStream.Protocol
{
	/// <summary>
	/// Constants shared by the stream host (runtime, original desktop) and the stream viewer (client, isolated desktop).
	/// </summary>
	public static class StreamProtocol
	{
		/// <summary>
		/// Marks the start of the handshake ("SEBD"), used to reject foreign data early.
		/// </summary>
		public const uint Magic = 0x44424553;

		/// <summary>
		/// Bumped whenever the wire format changes in an incompatible way.
		/// </summary>
		public const ushort Version = 1;

		/// <summary>
		/// The largest width or height of a streamed frame. Larger windows are not streamed.
		/// </summary>
		public const int MaxDimension = 8192;

		/// <summary>
		/// The largest accepted message payload (a full frame of maximum size plus its header).
		/// </summary>
		public const int MaxPayloadSize = FrameHeader.Size + MaxDimension * MaxDimension * 4;

		/// <summary>
		/// Determines the name of the pipe on which the host of the runtime with the given ID accepts viewers. Named pipes are not
		/// bound to a desktop, which is why they can be used to cross from the isolated desktop to the original desktop.
		/// </summary>
		public static string GetPipeName(Guid runtimeId)
		{
			return $"SafeExamBrowser_DebugStream_{runtimeId:N}";
		}
	}
}
