using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SafeExamBrowser.DebugStream.Interop;

namespace SafeExamBrowser.DebugStream.Host
{
	/// <summary>
	/// Finds the Firefox windows on the desktop of the calling thread.
	/// </summary>
	internal sealed class FirefoxWindowFinder
	{
		private const string POPUP_CLASS = "MozillaDropShadowWindowClass";
		private const string WINDOW_CLASS = "MozillaWindowClass";

		private readonly Dictionary<int, bool> processCache = new Dictionary<int, bool>();

		/// <summary>
		/// Finds all visible top-level browser windows, the topmost (i.e. most recently used) window first.
		/// </summary>
		internal IList<StreamWindowInfo> FindMainWindows()
		{
			var windows = new List<StreamWindowInfo>();

			Native.EnumWindows((window, _) =>
			{
				if (IsMainWindow(window, out var title) && IsFirefoxWindow(window, out var processId))
				{
					windows.Add(new StreamWindowInfo { Handle = window, ProcessId = processId, Title = title });
				}

				return true;
			}, IntPtr.Zero);

			return windows;
		}

		/// <summary>
		/// Finds the visible popups (menus, panels, dropdowns, tooltips, ...) of the given browser window, the topmost popup first.
		/// Popups are top-level windows of the browser process without title, which are owned by the browser window (or not owned
		/// at all) and overlap with it.
		/// </summary>
		internal IList<IntPtr> FindPopups(IntPtr mainWindow)
		{
			var popups = new List<IntPtr>();
			var processId = Native.GetProcessIdOf(mainWindow);

			if (!Native.GetWindowRect(mainWindow, out var mainRect))
			{
				return popups;
			}

			Native.EnumWindows((window, _) =>
			{
				if (window != mainWindow && IsPopup(window, mainWindow, processId, mainRect))
				{
					popups.Add(window);
				}

				return true;
			}, IntPtr.Zero);

			return popups;
		}

		private bool IsFirefoxWindow(IntPtr window, out int processId)
		{
			processId = Native.GetProcessIdOf(window);

			if (!processCache.TryGetValue(processId, out var isFirefox))
			{
				isFirefox = string.Equals(GetImageName(processId), FirefoxLocator.EXECUTABLE_NAME, StringComparison.OrdinalIgnoreCase);
				processCache[processId] = isFirefox;
			}

			return isFirefox;
		}

		private static bool IsMainWindow(IntPtr window, out string title)
		{
			title = default;

			if (!Native.IsWindowVisible(window) || Native.GetClassNameOf(window) != WINDOW_CLASS)
			{
				return false;
			}

			// Main windows have a title and no owner, popups and helper windows have neither.
			var isTool = (Native.GetWindowLongValue(window, Native.GWL_EXSTYLE) & Native.WS_EX_TOOLWINDOW) != 0;

			if (isTool || Native.GetWindow(window, Native.GW_OWNER) != IntPtr.Zero)
			{
				return false;
			}

			title = Native.GetTitleOf(window);

			return !string.IsNullOrWhiteSpace(title);
		}

		private static bool IsPopup(IntPtr window, IntPtr mainWindow, int processId, RECT mainRect)
		{
			if (!Native.IsWindowVisible(window) || Native.GetProcessIdOf(window) != processId)
			{
				return false;
			}

			// Popups with a shadow (menus, dropdowns, ...) use a window class of their own.
			var className = Native.GetClassNameOf(window);

			if (className != WINDOW_CLASS && className != POPUP_CLASS)
			{
				return false;
			}

			var owner = Native.GetWindow(window, Native.GW_OWNER);

			if ((owner != IntPtr.Zero && owner != mainWindow) || !string.IsNullOrEmpty(Native.GetTitleOf(window)))
			{
				return false;
			}

			return Native.GetWindowRect(window, out var rect) && rect.Width > 0 && rect.Height > 0 && rect.Intersects(mainRect);
		}

		private static string GetImageName(int processId)
		{
			var handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);

			if (handle == IntPtr.Zero)
			{
				return default;
			}

			try
			{
				var path = new StringBuilder(1024);
				var size = path.Capacity;

				return Native.QueryFullProcessImageName(handle, 0, path, ref size) ? Path.GetFileName(path.ToString()) : default;
			}
			finally
			{
				Native.CloseHandle(handle);
			}
		}
	}
}
