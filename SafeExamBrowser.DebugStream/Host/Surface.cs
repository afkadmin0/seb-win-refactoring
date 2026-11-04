using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using SafeExamBrowser.DebugStream.Interop;

namespace SafeExamBrowser.DebugStream.Host
{
	/// <summary>
	/// A 32 bit per pixel top-down DIB section together with a memory device context, i.e. a bitmap windows can be painted into
	/// whose pixels can be accessed directly (BGRA, <see cref="Stride"/> bytes per row).
	/// </summary>
	internal sealed class Surface : IDisposable
	{
		private IntPtr bitmap;
		private IntPtr original;

		internal IntPtr Bits { get; private set; }
		internal IntPtr Dc { get; private set; }
		internal int Height { get; }
		internal int Stride => Width * 4;
		internal int Width { get; }

		internal Surface(int width, int height)
		{
			if (width <= 0 || height <= 0)
			{
				throw new ArgumentOutOfRangeException(nameof(width), "A surface must have a positive size!");
			}

			var info = new BITMAPINFOHEADER
			{
				Size = (uint) Marshal.SizeOf(typeof(BITMAPINFOHEADER)),
				Width = width,
				Height = -height, // negative: top-down
				Planes = 1,
				BitCount = 32
			};

			Height = height;
			Width = width;
			Dc = Native.CreateCompatibleDC(IntPtr.Zero);

			if (Dc == IntPtr.Zero)
			{
				throw new Win32Exception(Marshal.GetLastWin32Error());
			}

			bitmap = Native.CreateDIBSection(Dc, ref info, 0, out var bits, IntPtr.Zero, 0);

			if (bitmap == IntPtr.Zero || bits == IntPtr.Zero)
			{
				var error = Marshal.GetLastWin32Error();

				Dispose();

				throw new Win32Exception(error);
			}

			Bits = bits;
			original = Native.SelectObject(Dc, bitmap);
		}

		/// <summary>
		/// Sets all pixels to transparent black.
		/// </summary>
		internal void Clear()
		{
			if (Bits != IntPtr.Zero)
			{
				var zero = new byte[Stride];

				for (var row = 0; row < Height; row++)
				{
					Marshal.Copy(zero, 0, IntPtr.Add(Bits, row * Stride), Stride);
				}
			}
		}

		/// <summary>
		/// Copies the given area of the source surface (which must have the same dimensions) to this surface.
		/// </summary>
		internal void CopyFrom(Surface source, int x, int y, int width, int height)
		{
			for (var row = y; row < y + height; row++)
			{
				var offset = row * Stride + x * 4;

				Native.MemCopy(IntPtr.Add(Bits, offset), IntPtr.Add(source.Bits, offset), (UIntPtr) (width * 4));
			}
		}

		public void Dispose()
		{
			if (Dc != IntPtr.Zero)
			{
				if (original != IntPtr.Zero)
				{
					Native.SelectObject(Dc, original);
				}

				Native.DeleteDC(Dc);
			}

			if (bitmap != IntPtr.Zero)
			{
				Native.DeleteObject(bitmap);
			}

			Bits = IntPtr.Zero;
			Dc = IntPtr.Zero;
			bitmap = IntPtr.Zero;
			original = IntPtr.Zero;
		}
	}
}
