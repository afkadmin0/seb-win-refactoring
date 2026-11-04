using System;
using System.Collections.Generic;
using SafeExamBrowser.DebugStream.Interop;
using SafeExamBrowser.DebugStream.Protocol;

namespace SafeExamBrowser.DebugStream.Host
{
	/// <summary>
	/// Captures a window (together with its popups, e.g. menus) via <c>PrintWindow</c>. Unlike a screen capture, this also yields
	/// the current content of windows which are covered by other windows or live on a desktop which is not receiving input.
	///
	/// The calling thread must live on the desktop of the captured window.
	/// </summary>
	internal sealed class WindowCapturer : IDisposable
	{
		private Surface frame;
		private Surface popupSurface;
		private Surface windowSurface;

		/// <summary>
		/// The most recently captured image. Only valid after a successful call of <see cref="TryCapture"/>.
		/// </summary>
		internal Surface Frame => frame;

		/// <summary>
		/// The reason the most recent call of <see cref="TryCapture"/> failed.
		/// </summary>
		internal string Failure { get; private set; }

		/// <summary>
		/// The screen coordinates of the top-left pixel of the <see cref="Frame"/>.
		/// </summary>
		internal POINT Origin { get; private set; }

		/// <summary>
		/// Captures the given window and paints the given popups (from bottom to top) over it.
		/// </summary>
		internal bool TryCapture(IntPtr window, IList<IntPtr> popupsBottomToTop)
		{
			Failure = default;

			if (!Native.GetWindowRect(window, out var windowRect) || windowRect.Width <= 0 || windowRect.Height <= 0)
			{
				Failure = "The window has no valid size.";

				return false;
			}

			// The window rectangle includes the invisible resize borders of the window, only the extended frame is actually visible.
			var visible = windowRect;

			if (Native.DwmGetWindowAttribute(window, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out var frameBounds, 16) == 0
				&& frameBounds.Width > 0
				&& frameBounds.Height > 0
				&& frameBounds.Left >= windowRect.Left
				&& frameBounds.Top >= windowRect.Top
				&& frameBounds.Right <= windowRect.Right
				&& frameBounds.Bottom <= windowRect.Bottom)
			{
				visible = frameBounds;
			}

			if (visible.Width > StreamProtocol.MaxDimension || visible.Height > StreamProtocol.MaxDimension)
			{
				Failure = $"The window is too large to be streamed ({visible.Width}x{visible.Height}).";

				return false;
			}

			Ensure(ref windowSurface, windowRect.Width, windowRect.Height);
			Ensure(ref frame, visible.Width, visible.Height);

			windowSurface.Clear();

			if (!Native.PrintWindow(window, windowSurface.Dc, Native.PW_RENDERFULLCONTENT))
			{
				Failure = "The window could not be painted into the capture surface.";

				return false;
			}

			Native.BitBlt(Frame.Dc, 0, 0, visible.Width, visible.Height, windowSurface.Dc, visible.Left - windowRect.Left, visible.Top - windowRect.Top, Native.SRCCOPY);

			foreach (var popup in popupsBottomToTop ?? new List<IntPtr>())
			{
				PaintPopup(popup, visible);
			}

			Origin = new POINT { X = visible.Left, Y = visible.Top };

			return true;
		}

		public void Dispose()
		{
			frame?.Dispose();
			popupSurface?.Dispose();
			windowSurface?.Dispose();

			frame = default;
			popupSurface = default;
			windowSurface = default;
		}

		private void PaintPopup(IntPtr popup, RECT visible)
		{
			if (!Native.GetWindowRect(popup, out var rect) || rect.Width <= 0 || rect.Height <= 0 || !rect.Intersects(visible))
			{
				return;
			}

			if (rect.Width > StreamProtocol.MaxDimension || rect.Height > StreamProtocol.MaxDimension)
			{
				return;
			}

			Ensure(ref popupSurface, rect.Width, rect.Height);
			popupSurface.Clear();

			if (Native.PrintWindow(popup, popupSurface.Dc, Native.PW_RENDERFULLCONTENT))
			{
				Native.BitBlt(Frame.Dc, rect.Left - visible.Left, rect.Top - visible.Top, rect.Width, rect.Height, popupSurface.Dc, 0, 0, Native.SRCCOPY);
			}
		}

		private static void Ensure(ref Surface surface, int width, int height)
		{
			if (surface == null || surface.Width != width || surface.Height != height)
			{
				surface?.Dispose();
				surface = new Surface(width, height);
			}
		}
	}
}
