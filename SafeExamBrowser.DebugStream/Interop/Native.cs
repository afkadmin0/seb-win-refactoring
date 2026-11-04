using System;
using System.Runtime.InteropServices;
using System.Text;

namespace SafeExamBrowser.DebugStream.Interop
{
	[StructLayout(LayoutKind.Sequential)]
	internal struct RECT
	{
		public int Left;
		public int Top;
		public int Right;
		public int Bottom;

		public int Width => Right - Left;
		public int Height => Bottom - Top;

		public bool Contains(int x, int y)
		{
			return x >= Left && x < Right && y >= Top && y < Bottom;
		}

		public bool Intersects(RECT other)
		{
			return Left < other.Right && other.Left < Right && Top < other.Bottom && other.Top < Bottom;
		}

		public override string ToString()
		{
			return $"({Left},{Top} {Width}x{Height})";
		}
	}

	[StructLayout(LayoutKind.Sequential)]
	internal struct POINT
	{
		public int X;
		public int Y;
	}

	[StructLayout(LayoutKind.Sequential)]
	internal struct BITMAPINFOHEADER
	{
		public uint Size;
		public int Width;
		public int Height;
		public ushort Planes;
		public ushort BitCount;
		public uint Compression;
		public uint SizeImage;
		public int XPelsPerMeter;
		public int YPelsPerMeter;
		public uint ColorsUsed;
		public uint ColorsImportant;
	}

	internal delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

	/// <summary>
	/// The subset of the Windows API needed to capture and control windows on the desktop of the calling thread.
	/// </summary>
	internal static class Native
	{
		internal const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
		internal const int GWL_EXSTYLE = -20;
		internal const int GWL_STYLE = -16;
		internal const int GW_OWNER = 4;
		internal const int MAPVK_VK_TO_VSC = 0;
		internal const int SRCCOPY = 0x00CC0020;
		internal const int SW_RESTORE = 9;
		internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
		internal const uint PW_RENDERFULLCONTENT = 0x2;
		internal const uint SWP_NOACTIVATE = 0x0010;
		internal const uint SWP_NOMOVE = 0x0002;
		internal const uint SWP_NOSIZE = 0x0001;
		internal const long WS_EX_TOOLWINDOW = 0x00000080L;
		internal const long WS_POPUP = 0x80000000L;

		internal const uint WM_ACTIVATE = 0x0006;
		internal const uint WM_ACTIVATEAPP = 0x001C;
		internal const uint WM_CHAR = 0x0102;
		internal const uint WM_KEYDOWN = 0x0100;
		internal const uint WM_KEYUP = 0x0101;
		internal const uint WM_KILLFOCUS = 0x0008;
		internal const uint WM_LBUTTONDOWN = 0x0201;
		internal const uint WM_LBUTTONUP = 0x0202;
		internal const uint WM_MBUTTONDOWN = 0x0207;
		internal const uint WM_MBUTTONUP = 0x0208;
		internal const uint WM_MOUSEHWHEEL = 0x020E;
		internal const uint WM_MOUSELEAVE = 0x02A3;
		internal const uint WM_MOUSEMOVE = 0x0200;
		internal const uint WM_MOUSEWHEEL = 0x020A;
		internal const uint WM_NCACTIVATE = 0x0086;
		internal const uint WM_RBUTTONDOWN = 0x0204;
		internal const uint WM_RBUTTONUP = 0x0205;
		internal const uint WM_SETFOCUS = 0x0007;
		internal const uint WM_SYSCHAR = 0x0106;
		internal const uint WM_SYSKEYDOWN = 0x0104;
		internal const uint WM_SYSKEYUP = 0x0105;

		internal const int VK_CONTROL = 0x11;
		internal const int VK_LCONTROL = 0xA2;
		internal const int VK_LMENU = 0xA4;
		internal const int VK_LSHIFT = 0xA0;
		internal const int VK_MENU = 0x12;
		internal const int VK_RCONTROL = 0xA3;
		internal const int VK_RMENU = 0xA5;
		internal const int VK_RSHIFT = 0xA1;
		internal const int VK_SHIFT = 0x10;

		internal static readonly IntPtr HWND_TOP = IntPtr.Zero;

		/// <summary>
		/// <c>DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2</c>, makes sure window coordinates and captured pixels are physical pixels.
		/// </summary>
		internal static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

		[DllImport("user32.dll", SetLastError = true)]
		internal static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

		[DllImport("gdi32.dll")]
		internal static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, int operation);

		[DllImport("user32.dll")]
		internal static extern bool ClientToScreen(IntPtr window, ref POINT point);

		[DllImport("gdi32.dll")]
		internal static extern IntPtr CreateCompatibleDC(IntPtr dc);

		[DllImport("gdi32.dll", SetLastError = true)]
		internal static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER info, uint usage, out IntPtr bits, IntPtr section, uint offset);

		[DllImport("gdi32.dll")]
		internal static extern bool DeleteDC(IntPtr dc);

		[DllImport("gdi32.dll")]
		internal static extern bool DeleteObject(IntPtr @object);

		[DllImport("dwmapi.dll")]
		internal static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out RECT value, int size);

		[DllImport("user32.dll")]
		internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

		[DllImport("user32.dll", CharSet = CharSet.Unicode)]
		internal static extern int GetClassName(IntPtr window, StringBuilder name, int maxCount);

		[DllImport("kernel32.dll")]
		internal static extern uint GetCurrentThreadId();

		[DllImport("user32.dll")]
		internal static extern IntPtr GetKeyboardLayout(uint threadId);

		[DllImport("user32.dll", SetLastError = true)]
		internal static extern bool GetKeyboardState(byte[] state);

		[DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
		private static extern IntPtr GetWindowLongPtr64(IntPtr window, int index);

		[DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
		private static extern int GetWindowLong32(IntPtr window, int index);

		[DllImport("user32.dll")]
		internal static extern IntPtr GetWindow(IntPtr window, int command);

		[DllImport("user32.dll")]
		internal static extern bool GetWindowRect(IntPtr window, out RECT rect);

		[DllImport("user32.dll", CharSet = CharSet.Unicode)]
		internal static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);

		[DllImport("user32.dll")]
		internal static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);

		[DllImport("user32.dll")]
		internal static extern bool IsIconic(IntPtr window);

		[DllImport("user32.dll")]
		internal static extern bool IsWindow(IntPtr window);

		[DllImport("user32.dll")]
		internal static extern bool IsWindowVisible(IntPtr window);

		[DllImport("user32.dll")]
		internal static extern uint MapVirtualKeyEx(uint code, uint mapType, IntPtr layout);

		[DllImport("msvcrt.dll", EntryPoint = "memcmp", CallingConvention = CallingConvention.Cdecl)]
		internal static extern int MemCompare(IntPtr first, IntPtr second, UIntPtr count);

		[DllImport("msvcrt.dll", EntryPoint = "memcpy", CallingConvention = CallingConvention.Cdecl)]
		internal static extern IntPtr MemCopy(IntPtr destination, IntPtr source, UIntPtr count);

		[DllImport("kernel32.dll", SetLastError = true)]
		internal static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

		/// <summary>
		/// IMPORTANT: Must be the Unicode version. The ANSI version converts the character of <c>WM_CHAR</c> messages via the ANSI code
		/// page, which garbles every character outside of it (e.g. the Euro sign or anything non-Latin).
		/// </summary>
		[DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
		internal static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

		[DllImport("user32.dll")]
		internal static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);

		[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		internal static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);

		[DllImport("kernel32.dll")]
		internal static extern bool CloseHandle(IntPtr handle);

		[DllImport("user32.dll")]
		internal static extern bool ScreenToClient(IntPtr window, ref POINT point);

		[DllImport("user32.dll", CharSet = CharSet.Unicode)]
		internal static extern int ToUnicodeEx(uint virtualKey, uint scanCode, byte[] keyState, [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder buffer, int bufferSize, uint flags, IntPtr layout);

		[DllImport("gdi32.dll")]
		internal static extern IntPtr SelectObject(IntPtr dc, IntPtr @object);

		[DllImport("user32.dll", SetLastError = true)]
		internal static extern bool SetKeyboardState(byte[] state);

		[DllImport("user32.dll", SetLastError = true)]
		internal static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

		[DllImport("user32.dll", SetLastError = true)]
		internal static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

		[DllImport("user32.dll")]
		internal static extern bool ShowWindowAsync(IntPtr window, int command);

		internal static long GetWindowLongValue(IntPtr window, int index)
		{
			return IntPtr.Size == 8 ? GetWindowLongPtr64(window, index).ToInt64() : GetWindowLong32(window, index);
		}

		internal static string GetClassNameOf(IntPtr window)
		{
			var name = new StringBuilder(256);

			return GetClassName(window, name, name.Capacity) > 0 ? name.ToString() : string.Empty;
		}

		internal static string GetTitleOf(IntPtr window)
		{
			var title = new StringBuilder(512);

			return GetWindowText(window, title, title.Capacity) > 0 ? title.ToString() : string.Empty;
		}

		internal static int GetProcessIdOf(IntPtr window)
		{
			GetWindowThreadProcessId(window, out var processId);

			return processId;
		}

		internal static uint GetThreadIdOf(IntPtr window)
		{
			return GetWindowThreadProcessId(window, out _);
		}

		/// <summary>
		/// Makes the calling thread report and use physical pixels, independent of the manifest of the process. Not available on
		/// systems older than Windows 10 1703, where the process settings remain in effect.
		/// </summary>
		internal static void MakeThreadDpiAware()
		{
			try
			{
				SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
			}
			catch (EntryPointNotFoundException)
			{
			}
		}

		/// <summary>
		/// Packs two 16-bit values into an <c>LPARAM</c> / <c>WPARAM</c> the way <c>MAKELPARAM</c> does.
		/// </summary>
		internal static IntPtr MakeParameter(int low, int high)
		{
			var value = unchecked((uint) (((high & 0xFFFF) << 16) | (low & 0xFFFF)));

			return IntPtr.Size == 8 ? new IntPtr((long) value) : new IntPtr(unchecked((int) value));
		}
	}
}
