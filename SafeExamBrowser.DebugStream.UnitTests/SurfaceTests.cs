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
	public class SurfaceTests
	{
		[TestMethod]
		public void MustProvideAccessToPixels()
		{
			using (var sut = new Surface(7, 5))
			{
				Assert.AreEqual(7, sut.Width);
				Assert.AreEqual(5, sut.Height);
				Assert.AreEqual(28, sut.Stride);
				Assert.AreNotEqual(IntPtr.Zero, sut.Bits);
				Assert.AreNotEqual(IntPtr.Zero, sut.Dc);

				// The last pixel must be addressable (and the surface is top-down, i.e. row 0 is the first one in memory).
				Marshal.WriteInt32(sut.Bits, 4 * (7 * 5 - 1), 0x12345678);

				Assert.AreEqual(0x12345678, Marshal.ReadInt32(sut.Bits, 4 * (7 * 5 - 1)));
			}
		}

		[TestMethod]
		public void MustClear()
		{
			using (var sut = new Surface(9, 4))
			{
				for (var i = 0; i < 9 * 4; i++)
				{
					Marshal.WriteInt32(sut.Bits, i * 4, -1);
				}

				sut.Clear();

				for (var i = 0; i < 9 * 4; i++)
				{
					Assert.AreEqual(0, Marshal.ReadInt32(sut.Bits, i * 4));
				}
			}
		}

		[TestMethod]
		public void MustCopyOnlyTheGivenArea()
		{
			using (var source = new Surface(8, 8))
			using (var sut = new Surface(8, 8))
			{
				for (var i = 0; i < 8 * 8; i++)
				{
					Marshal.WriteInt32(source.Bits, i * 4, 0x00AABBCC);
				}

				sut.CopyFrom(source, 2, 3, 4, 2);

				for (var y = 0; y < 8; y++)
				{
					for (var x = 0; x < 8; x++)
					{
						var inside = x >= 2 && x < 6 && y >= 3 && y < 5;

						Assert.AreEqual(inside ? 0x00AABBCC : 0, Marshal.ReadInt32(sut.Bits, 4 * (y * 8 + x)), $"Pixel {x},{y}");
					}
				}
			}
		}

		[TestMethod]
		public void MustBeDisposableMultipleTimes()
		{
			var sut = new Surface(2, 2);

			sut.Dispose();
			sut.Dispose();

			Assert.AreEqual(IntPtr.Zero, sut.Bits);
			Assert.AreEqual(IntPtr.Zero, sut.Dc);
		}

		[TestMethod]
		public void MustRejectInvalidSize()
		{
			Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new Surface(0, 10));
			Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new Surface(10, 0));
			Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new Surface(-1, 10));
		}
	}
}
