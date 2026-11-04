using System;
using SafeExamBrowser.DebugStream.Interop;

namespace SafeExamBrowser.DebugStream.Host
{
	internal struct DirtyRect
	{
		internal int X;
		internal int Y;
		internal int Width;
		internal int Height;

		public override string ToString()
		{
			return $"({X},{Y} {Width}x{Height})";
		}
	}

	/// <summary>
	/// Determines the smallest rectangle containing all pixels which differ between two frames, so only that part needs to be sent.
	/// </summary>
	internal static class FrameDiffer
	{
		internal static bool TryGetDirtyRect(Surface previous, Surface current, out DirtyRect rect)
		{
			if (previous.Width != current.Width || previous.Height != current.Height)
			{
				throw new ArgumentException("Cannot compare surfaces of different size!");
			}

			return TryGetDirtyRect(previous.Bits, current.Bits, current.Width, current.Height, current.Stride, out rect);
		}

		/// <summary>
		/// Compares two 32 bit per pixel buffers of the same dimensions. Returns <c>false</c> if they are identical.
		/// </summary>
		internal static unsafe bool TryGetDirtyRect(IntPtr previous, IntPtr current, int width, int height, int stride, out DirtyRect rect)
		{
			var rowBytes = (UIntPtr) (width * 4);
			var top = -1;

			rect = default;

			for (var y = 0; y < height; y++)
			{
				if (RowsDiffer(previous, current, y, stride, rowBytes))
				{
					top = y;
					break;
				}
			}

			if (top < 0)
			{
				return false;
			}

			var bottom = top;

			for (var y = height - 1; y > top; y--)
			{
				if (RowsDiffer(previous, current, y, stride, rowBytes))
				{
					bottom = y;
					break;
				}
			}

			var left = width;
			var right = -1;

			for (var y = top; y <= bottom; y++)
			{
				var p = (uint*) ((byte*) previous + (long) y * stride);
				var c = (uint*) ((byte*) current + (long) y * stride);

				for (var x = 0; x < left; x++)
				{
					if (p[x] != c[x])
					{
						left = x;
						break;
					}
				}

				for (var x = width - 1; x > right; x--)
				{
					if (p[x] != c[x])
					{
						right = x;
						break;
					}
				}
			}

			// Every differing row contains at least one differing pixel, so left <= right is guaranteed.
			rect = new DirtyRect { X = left, Y = top, Width = right - left + 1, Height = bottom - top + 1 };

			return true;
		}

		private static unsafe bool RowsDiffer(IntPtr previous, IntPtr current, int row, int stride, UIntPtr rowBytes)
		{
			var offset = (long) row * stride;

			return Native.MemCompare(new IntPtr((byte*) previous + offset), new IntPtr((byte*) current + offset), rowBytes) != 0;
		}
	}
}
