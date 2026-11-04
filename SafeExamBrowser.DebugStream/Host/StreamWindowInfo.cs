using System;

namespace SafeExamBrowser.DebugStream.Host
{
	/// <summary>
	/// A top-level window on the original desktop which can be streamed.
	/// </summary>
	internal sealed class StreamWindowInfo
	{
		internal IntPtr Handle { get; set; }
		internal int ProcessId { get; set; }
		internal string Title { get; set; }

		public override string ToString()
		{
			return $"'{Title}' [0x{Handle.ToInt64():X}, process {ProcessId}]";
		}
	}
}
