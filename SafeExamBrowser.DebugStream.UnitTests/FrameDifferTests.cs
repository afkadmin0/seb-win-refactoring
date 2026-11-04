/*
 * Copyright (c) 2026 ETH Zürich, IT Services
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SafeExamBrowser.DebugStream.Host;

namespace SafeExamBrowser.DebugStream.UnitTests
{
	[TestClass]
	public class FrameDifferTests
	{
		private const int HEIGHT = 12;
		private const int WIDTH = 16;

		private IntPtr current;
		private IntPtr previous;

		private int Stride => WIDTH * 4;

		[TestInitialize]
		public void Initialize()
		{
			current = Allocate();
			previous = Allocate();
		}

		[TestCleanup]
		public void Cleanup()
		{
			Marshal.FreeHGlobal(current);
			Marshal.FreeHGlobal(previous);
		}

		[TestMethod]
		public void MustNotReportIdenticalFrames()
		{
			Fill(previous, 0x11223344);
			Fill(current, 0x11223344);

			Assert.IsFalse(FrameDiffer.TryGetDirtyRect(previous, current, WIDTH, HEIGHT, Stride, out _));
		}

		[TestMethod]
		public void MustDetectSinglePixel()
		{
			Set(current, 5, 3, 0xFFFFFFFF);

			Assert.IsTrue(FrameDiffer.TryGetDirtyRect(previous, current, WIDTH, HEIGHT, Stride, out var rect));
			AssertRect(5, 3, 1, 1, rect);
		}

		[TestMethod]
		public void MustDetectBoundingBoxOfMultipleChanges()
		{
			Set(current, 2, 1, 1);
			Set(current, 13, 9, 1);

			Assert.IsTrue(FrameDiffer.TryGetDirtyRect(previous, current, WIDTH, HEIGHT, Stride, out var rect));
			AssertRect(2, 1, 12, 9, rect);
		}

		[TestMethod]
		public void MustDetectBoundingBoxOfRowsChangingInDifferentColumns()
		{
			Set(current, 10, 2, 1);
			Set(current, 3, 4, 1);
			Set(current, 7, 6, 1);

			Assert.IsTrue(FrameDiffer.TryGetDirtyRect(previous, current, WIDTH, HEIGHT, Stride, out var rect));
			AssertRect(3, 2, 8, 5, rect);
		}

		[TestMethod]
		public void MustDetectFirstAndLastPixel()
		{
			Set(current, 0, 0, 1);
			Set(current, WIDTH - 1, HEIGHT - 1, 1);

			Assert.IsTrue(FrameDiffer.TryGetDirtyRect(previous, current, WIDTH, HEIGHT, Stride, out var rect));
			AssertRect(0, 0, WIDTH, HEIGHT, rect);
		}

		[TestMethod]
		public void MustDetectChangesOfWholeFrame()
		{
			Fill(current, 0xCAFEBABE);

			Assert.IsTrue(FrameDiffer.TryGetDirtyRect(previous, current, WIDTH, HEIGHT, Stride, out var rect));
			AssertRect(0, 0, WIDTH, HEIGHT, rect);
		}

		[TestMethod]
		public void MustDetectChangeInFirstRowOnly()
		{
			Set(current, 4, 0, 1);

			Assert.IsTrue(FrameDiffer.TryGetDirtyRect(previous, current, WIDTH, HEIGHT, Stride, out var rect));
			AssertRect(4, 0, 1, 1, rect);
		}

		[TestMethod]
		public void MustDetectChangeInLastRowOnly()
		{
			Set(current, 4, HEIGHT - 1, 1);

			Assert.IsTrue(FrameDiffer.TryGetDirtyRect(previous, current, WIDTH, HEIGHT, Stride, out var rect));
			AssertRect(4, HEIGHT - 1, 1, 1, rect);
		}

		[TestMethod]
		public void MustDetectChangeInFirstAndLastColumn()
		{
			Set(current, 0, 5, 1);
			Set(current, WIDTH - 1, 5, 1);

			Assert.IsTrue(FrameDiffer.TryGetDirtyRect(previous, current, WIDTH, HEIGHT, Stride, out var rect));
			AssertRect(0, 5, WIDTH, 1, rect);
		}

		[TestMethod]
		public void MustDetectChangesOfASingleChannel()
		{
			// Only the alpha channel (the highest byte) differs.
			Set(previous, 8, 8, 0x00FFFFFF);
			Set(current, 8, 8, 0xFFFFFFFF);

			Assert.IsTrue(FrameDiffer.TryGetDirtyRect(previous, current, WIDTH, HEIGHT, Stride, out var rect));
			AssertRect(8, 8, 1, 1, rect);
		}

		[TestMethod]
		public void MustHandleSinglePixelFrame()
		{
			Marshal.WriteInt32(current, 1);

			Assert.IsTrue(FrameDiffer.TryGetDirtyRect(previous, current, 1, 1, 4, out var rect));
			AssertRect(0, 0, 1, 1, rect);
			Assert.IsFalse(FrameDiffer.TryGetDirtyRect(previous, previous, 1, 1, 4, out _));
		}

		[TestMethod]
		public void MustHandleSingleColumnFrame()
		{
			Marshal.WriteInt32(current, 3 * 4, 1);

			Assert.IsTrue(FrameDiffer.TryGetDirtyRect(previous, current, 1, 6, 4, out var rect));
			AssertRect(0, 3, 1, 1, rect);
		}

		[TestMethod]
		public void MustHandleSurfacesViaOverload()
		{
			using (var first = new Surface(WIDTH, HEIGHT))
			using (var second = new Surface(WIDTH, HEIGHT))
			{
				Assert.IsFalse(FrameDiffer.TryGetDirtyRect(first, second, out _));

				Marshal.WriteInt32(second.Bits, 4 * (WIDTH * 2 + 7), 1);

				Assert.IsTrue(FrameDiffer.TryGetDirtyRect(first, second, out var rect));
				AssertRect(7, 2, 1, 1, rect);
			}
		}

		[TestMethod]
		public void MustRejectSurfacesOfDifferentSize()
		{
			using (var first = new Surface(WIDTH, HEIGHT))
			using (var second = new Surface(WIDTH, HEIGHT + 1))
			{
				Assert.ThrowsExactly<ArgumentException>(() => FrameDiffer.TryGetDirtyRect(first, second, out _));
			}
		}

		private static void AssertRect(int x, int y, int width, int height, DirtyRect actual)
		{
			Assert.AreEqual(x, actual.X, "X");
			Assert.AreEqual(y, actual.Y, "Y");
			Assert.AreEqual(width, actual.Width, "Width");
			Assert.AreEqual(height, actual.Height, "Height");
		}

		private static IntPtr Allocate()
		{
			var pointer = Marshal.AllocHGlobal(WIDTH * HEIGHT * 4);

			Fill(pointer, 0);

			return pointer;
		}

		private static void Fill(IntPtr buffer, uint value)
		{
			for (var i = 0; i < WIDTH * HEIGHT; i++)
			{
				Marshal.WriteInt32(buffer, i * 4, unchecked((int) value));
			}
		}

		private static void Set(IntPtr buffer, int x, int y, uint value)
		{
			Marshal.WriteInt32(buffer, (y * WIDTH + x) * 4, unchecked((int) value));
		}
	}
}
