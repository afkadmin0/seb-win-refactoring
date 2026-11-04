/*
 * Copyright (c) 2026 ETH Zürich, IT Services
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using SafeExamBrowser.DebugStream.Host;
using SafeExamBrowser.DebugStream.Interop;
using SafeExamBrowser.DebugStream.Protocol;
using SafeExamBrowser.Logging.Contracts;

namespace SafeExamBrowser.DebugStream.UnitTests
{
	/// <summary>
	/// The injector is tested against real (invisible) native windows which record the messages they receive - including the state
	/// of the modifier keys at the time the message is processed, which is what a browser looks at.
	/// </summary>
	[TestClass]
	public class InputInjectorTests
	{
		private const uint WM_ACTIVATE = 0x0006;
		private const uint WM_ACTIVATEAPP = 0x001C;
		private const uint WM_CHAR = 0x0102;
		private const uint WM_KEYDOWN = 0x0100;
		private const uint WM_KEYUP = 0x0101;
		private const uint WM_KILLFOCUS = 0x0008;
		private const uint WM_LBUTTONDOWN = 0x0201;
		private const uint WM_MOUSEMOVE = 0x0200;
		private const uint WM_MOUSEWHEEL = 0x020A;
		private const uint WM_NCACTIVATE = 0x0086;
		private const uint WM_SETFOCUS = 0x0007;
		private const uint WM_SYSKEYDOWN = 0x0104;
		private const uint WM_SYSKEYUP = 0x0105;

		private const int VK_BACK = 0x08;
		private const int VK_F5 = 0x74;
		private const int VK_LCONTROL = 0xA2;
		private const int VK_LEFT = 0x25;
		private const int VK_RMENU = 0xA5;
		private const int VK_RSHIFT = 0xA1;

		private RecordingWindow main;
		private RecordingWindow other;
		private InputInjector sut;

		[TestInitialize]
		public void Initialize()
		{
			main = new RecordingWindow(100, 100, 400, 300);
			sut = new InputInjector(new Mock<ILogger>().Object);
			sut.SetTarget(main.Handle);
			sut.UpdateGeometry(new POINT { X = 100, Y = 100 }, new List<IntPtr>());
		}

		[TestCleanup]
		public void Cleanup()
		{
			sut.ReleaseAll();
			main.Dispose();
			other?.Dispose();
		}

		[TestMethod]
		public void MustIgnoreInputWithoutTarget()
		{
			var injector = new InputInjector(new Mock<ILogger>().Object);

			injector.Activate(true);
			injector.Inject(new MouseEvent { Action = MouseAction.LeftDown });
			injector.Inject(new KeyEvent { Action = KeyAction.Down, VirtualKey = 'A' });
			injector.Inject(new KeyEvent { Action = KeyAction.Char, Character = 'a' });
			injector.ReleaseAll();

			sut.SetTarget(IntPtr.Zero);
			sut.Inject(new MouseEvent { Action = MouseAction.Move });
			sut.Inject(new KeyEvent { Action = KeyAction.Down, VirtualKey = VK_F5 });

			Assert.IsEmpty(main.Flush());
		}

		[TestMethod]
		public void MustMakeModifiersVisibleToTheTargetThread()
		{
			sut.Inject(new KeyEvent { Action = KeyAction.Down, VirtualKey = VK_LCONTROL, Modifiers = KeyModifiers.Ctrl });
			sut.Inject(new KeyEvent { Action = KeyAction.Down, VirtualKey = 'L', Modifiers = KeyModifiers.Ctrl });
			sut.Inject(new KeyEvent { Action = KeyAction.Up, VirtualKey = 'L', Modifiers = KeyModifiers.Ctrl });
			sut.Inject(new KeyEvent { Action = KeyAction.Up, VirtualKey = VK_LCONTROL, Modifiers = KeyModifiers.None });

			var records = main.Flush();

			Assert.HasCount(4, records);
			Assert.IsTrue(records.Take(3).All(r => r.Ctrl), "Ctrl must be pressed from the press of the modifier until the release of the shortcut");
			Assert.IsFalse(records[3].Ctrl, "Ctrl must be released again when its release is processed");
		}

		[TestMethod]
		public void MustMakeShiftAndAltVisibleToTheTargetThread()
		{
			sut.Inject(new KeyEvent { Action = KeyAction.Down, VirtualKey = 0x7B, Modifiers = KeyModifiers.Shift | KeyModifiers.Alt });

			var record = main.Flush().Single();

			Assert.IsTrue(record.Shift);
			Assert.IsTrue(record.Alt);
			Assert.IsFalse(record.Ctrl);
		}

		[TestMethod]
		public void MustNotKeepModifiersPressedAfterRelease()
		{
			sut.Inject(new KeyEvent { Action = KeyAction.Down, VirtualKey = VK_LCONTROL, Modifiers = KeyModifiers.Ctrl });
			sut.ReleaseAll();
			sut.Inject(new KeyEvent { Action = KeyAction.Down, VirtualKey = VK_F5 });

			var records = main.Flush();

			Assert.HasCount(2, records);
			Assert.IsTrue(records[0].Ctrl);
			Assert.IsFalse(records[1].Ctrl);
		}

		[TestMethod]
		public void MustNormalizeModifierKeys()
		{
			sut.Inject(new KeyEvent { Action = KeyAction.Down, VirtualKey = VK_LCONTROL, Modifiers = KeyModifiers.Ctrl });
			sut.Inject(new KeyEvent { Action = KeyAction.Down, VirtualKey = VK_RSHIFT, Modifiers = KeyModifiers.Ctrl | KeyModifiers.Shift });
			sut.Inject(new KeyEvent { Action = KeyAction.Down, VirtualKey = VK_RMENU, Modifiers = KeyModifiers.Ctrl | KeyModifiers.Shift | KeyModifiers.Alt });

			var records = main.Flush();

			Assert.AreEqual(0x11L, records[0].WParam);
			Assert.AreEqual(0x10L, records[1].WParam);
			Assert.AreEqual(0x12L, records[2].WParam);
			Assert.AreEqual(WM_SYSKEYDOWN, records[2].Message, "Alt is a system key");
			Assert.AreEqual(1L, (records[2].LParam >> 24) & 1, "Right Alt is an extended key");
		}

		[TestMethod]
		public void MustPostCharacterOnlyIfItDiffersFromTheKey()
		{
			var scanCode = ScanCodeOf('A');

			sut.Inject(new KeyEvent { Action = KeyAction.Down, VirtualKey = 'A' });
			sut.Inject(new KeyEvent { Action = KeyAction.Char, Character = '€' });
			sut.Inject(new KeyEvent { Action = KeyAction.Up, VirtualKey = 'A' });

			var records = main.Flush();

			Assert.HasCount(2, records);
			Assert.AreEqual(WM_CHAR, records[0].Message);
			Assert.AreEqual((long) '€', records[0].WParam);
			Assert.AreEqual(scanCode, (records[0].LParam >> 16) & 0xFF, "The character must carry the scan code of its key");
			Assert.AreEqual(WM_KEYUP, records[1].Message);
		}

		[TestMethod]
		public void MustPostCharacterWithoutKey()
		{
			sut.Inject(new KeyEvent { Action = KeyAction.Char, Character = 'ß' });

			var record = main.Flush().Single();

			Assert.AreEqual(WM_CHAR, record.Message);
			Assert.AreEqual((long) 'ß', record.WParam);
		}

		[TestMethod]
		public void MustPostKeyOnlyIfCharacterIsDerivedFromIt()
		{
			// Firefox (and others) derive the character from the key message and treat an additional character message as another
			// input, which results in doubled characters.
			var character = ExpectedCharacter('A');
			var scanCode = ScanCodeOf('A');

			sut.Inject(new KeyEvent { Action = KeyAction.Down, VirtualKey = 'A' });
			sut.Inject(new KeyEvent { Action = KeyAction.Char, Character = character });
			sut.Inject(new KeyEvent { Action = KeyAction.Up, VirtualKey = 'A' });

			var records = main.Flush();

			Assert.HasCount(2, records);
			Assert.AreEqual(WM_KEYDOWN, records[0].Message);
			Assert.AreEqual((long) 'A', records[0].WParam);
			Assert.AreEqual(scanCode, (records[0].LParam >> 16) & 0xFF);
			Assert.AreEqual(1L, records[0].LParam & 0xFFFF, "Repeat count");
			Assert.AreEqual(WM_KEYUP, records[1].Message);
			Assert.AreEqual(0b11L, (records[1].LParam >> 30) & 0b11, "A key release has the previous state and transition state set");
		}

		[TestMethod]
		public void MustPostPendingKeyBeforeMouseInput()
		{
			sut.Inject(new KeyEvent { Action = KeyAction.Down, VirtualKey = 'A' });
			sut.Inject(new MouseEvent { Action = MouseAction.Move, X = 1, Y = 1 });

			var records = main.Flush();

			Assert.HasCount(2, records);
			Assert.AreEqual(WM_KEYDOWN, records[0].Message);
			Assert.AreEqual(WM_MOUSEMOVE, records[1].Message);
		}

		[TestMethod]
		public void MustPostPendingKeyBeforeNextKey()
		{
			sut.Inject(new KeyEvent { Action = KeyAction.Down, VirtualKey = 'A' });
			sut.Inject(new KeyEvent { Action = KeyAction.Down, VirtualKey = 'B' });

			var records = main.Flush();

			Assert.HasCount(1, records, "The second key may still produce a character, so it is held back");
			Assert.AreEqual((long) 'A', records[0].WParam);

			sut.Inject(new KeyEvent { Action = KeyAction.Up, VirtualKey = 'B' });

			records = main.Flush();

			Assert.HasCount(2, records);
			Assert.AreEqual(WM_KEYDOWN, records[0].Message);
			Assert.AreEqual((long) 'B', records[0].WParam);
			Assert.AreEqual(WM_KEYUP, records[1].Message);
		}

		[TestMethod]
		public void MustPostKeyOnReleaseIfNoCharacterFollows()
		{
			sut.Inject(new KeyEvent { Action = KeyAction.Down, VirtualKey = 'A' });

			Assert.IsEmpty(main.Flush());

			sut.Inject(new KeyEvent { Action = KeyAction.Up, VirtualKey = 'A' });

			var records = main.Flush();

			Assert.HasCount(2, records);
			Assert.AreEqual(WM_KEYDOWN, records[0].Message);
			Assert.AreEqual(WM_KEYUP, records[1].Message);
		}

		[TestMethod]
		public void MustPostNonPrintableKeysImmediately()
		{
			sut.Inject(new KeyEvent { Action = KeyAction.Down, VirtualKey = VK_F5 });
			sut.Inject(new KeyEvent { Action = KeyAction.Down, VirtualKey = VK_LEFT, IsRepeat = true });

			var records = main.Flush();

			Assert.HasCount(2, records);
			Assert.AreEqual(0L, (records[0].LParam >> 24) & 1, "F5 is not an extended key");
			Assert.AreEqual(1L, (records[1].LParam >> 24) & 1, "The arrow keys are extended keys");
			Assert.AreEqual(1L, (records[1].LParam >> 30) & 1, "A repetition has the previous key state set");
		}

		[TestMethod]
		public void MustPostShortcutsImmediately()
		{
			sut.Inject(new KeyEvent { Action = KeyAction.Down, VirtualKey = 'L', Modifiers = KeyModifiers.Ctrl });

			var record = main.Flush().Single();

			Assert.AreEqual(WM_KEYDOWN, record.Message);
			Assert.AreEqual((long) 'L', record.WParam);
			Assert.IsTrue(record.Ctrl);
		}

		[TestMethod]
		public void MustIgnoreControlCharacters()
		{
			sut.Inject(new KeyEvent { Action = KeyAction.Down, VirtualKey = VK_BACK });
			sut.Inject(new KeyEvent { Action = KeyAction.Char, Character = '\b' });
			sut.Inject(new KeyEvent { Action = KeyAction.Up, VirtualKey = VK_BACK });
			sut.Inject(new KeyEvent { Action = KeyAction.Char, Character = '\r' });
			sut.Inject(new KeyEvent { Action = KeyAction.Char, Character = '\u007F' });

			var records = main.Flush();

			Assert.HasCount(2, records);
			Assert.AreEqual(WM_KEYDOWN, records[0].Message);
			Assert.AreEqual(WM_KEYUP, records[1].Message);
		}

		[TestMethod]
		public void MustUseSystemMessagesForAltCombinations()
		{
			sut.Inject(new KeyEvent { Action = KeyAction.Down, VirtualKey = VK_F5, Modifiers = KeyModifiers.Alt });
			sut.Inject(new KeyEvent { Action = KeyAction.Up, VirtualKey = VK_F5, Modifiers = KeyModifiers.Alt });

			var records = main.Flush();

			Assert.HasCount(2, records);
			Assert.AreEqual(WM_SYSKEYDOWN, records[0].Message);
			Assert.AreEqual(WM_SYSKEYUP, records[1].Message);
			Assert.AreEqual(1L, (records[0].LParam >> 29) & 1, "Context code (Alt pressed)");
		}

		[TestMethod]
		public void MustReportActivation()
		{
			sut.Activate(true);

			var activated = main.Flush().Select(r => r.Message).ToList();

			CollectionAssert.AreEqual(new[] { WM_ACTIVATEAPP, WM_NCACTIVATE, WM_ACTIVATE, WM_SETFOCUS }, activated);

			sut.Activate(false);

			var deactivated = main.Flush().Select(r => r.Message).ToList();

			CollectionAssert.AreEqual(new[] { WM_KILLFOCUS, WM_ACTIVATE, WM_NCACTIVATE, WM_ACTIVATEAPP }, deactivated);
		}

		[TestMethod]
		public void MustReleaseModifiersOnDeactivation()
		{
			sut.Inject(new KeyEvent { Action = KeyAction.Down, VirtualKey = VK_LCONTROL, Modifiers = KeyModifiers.Ctrl });
			sut.Activate(false);
			sut.Inject(new KeyEvent { Action = KeyAction.Down, VirtualKey = VK_F5 });

			var f5 = main.Flush().Last(r => r.Message == WM_KEYDOWN);

			Assert.IsFalse(f5.Ctrl);
		}

		[TestMethod]
		public void MustTranslateMousePositionToClientCoordinates()
		{
			sut.UpdateGeometry(new POINT { X = 110, Y = 120 }, new List<IntPtr>());
			sut.Inject(new MouseEvent { Action = MouseAction.LeftDown, X = 5, Y = 7, Flags = 0x0001 });

			var record = main.Flush().Single();

			// Screen position 115,127 is 15,27 inside the window which starts at 100,100.
			Assert.AreEqual(WM_LBUTTONDOWN, record.Message);
			Assert.AreEqual(0x0001L, record.WParam);
			Assert.AreEqual(15L, record.LParam & 0xFFFF);
			Assert.AreEqual(27L, (record.LParam >> 16) & 0xFFFF);
		}

		[TestMethod]
		public void MustPostWheelWithDeltaAndScreenCoordinates()
		{
			sut.UpdateGeometry(new POINT { X = 110, Y = 120 }, new List<IntPtr>());
			sut.Inject(new MouseEvent { Action = MouseAction.Wheel, X = 5, Y = 7, Delta = -120, Flags = 0x0008 });

			var record = main.Flush().Single();

			Assert.AreEqual(WM_MOUSEWHEEL, record.Message);
			Assert.AreEqual(-120, (short) ((record.WParam >> 16) & 0xFFFF));
			Assert.AreEqual(0x0008L, record.WParam & 0xFFFF);
			Assert.AreEqual(115L, record.LParam & 0xFFFF, "Wheel messages use screen coordinates");
			Assert.AreEqual(127L, (record.LParam >> 16) & 0xFFFF);
		}

		[TestMethod]
		public void MustPostMouseInputToPopupAboveWindow()
		{
			other = new RecordingWindow(600, 500, 100, 100);

			sut.UpdateGeometry(new POINT { X = 100, Y = 100 }, new List<IntPtr> { other.Handle });

			// Image position 520,420 is screen position 620,520, i.e. 20,20 inside of the popup.
			sut.Inject(new MouseEvent { Action = MouseAction.Move, X = 520, Y = 420 });
			sut.Inject(new MouseEvent { Action = MouseAction.Move, X = 50, Y = 50 });

			var popupRecords = other.Flush();
			var mainRecords = main.Flush();

			Assert.HasCount(1, popupRecords);
			Assert.AreEqual(20L, popupRecords[0].LParam & 0xFFFF);
			Assert.AreEqual(20L, (popupRecords[0].LParam >> 16) & 0xFFFF);
			Assert.HasCount(1, mainRecords);
			Assert.AreEqual(50L, mainRecords[0].LParam & 0xFFFF);
		}

		[TestMethod]
		public void MustForgetPendingInputWhenTargetChanges()
		{
			other = new RecordingWindow(600, 500, 100, 100);

			sut.Inject(new KeyEvent { Action = KeyAction.Down, VirtualKey = 'A' });
			sut.SetTarget(other.Handle);
			sut.Inject(new KeyEvent { Action = KeyAction.Up, VirtualKey = 'A' });

			var mainRecords = main.Flush();
			var otherRecords = other.Flush();

			Assert.IsEmpty(mainRecords);
			Assert.HasCount(1, otherRecords);
			Assert.AreEqual(WM_KEYUP, otherRecords[0].Message);
		}

		private static char ExpectedCharacter(int virtualKey)
		{
			var layout = Native.GetKeyboardLayout(0);
			var scanCode = Native.MapVirtualKeyEx((uint) virtualKey, Native.MAPVK_VK_TO_VSC, layout);
			var buffer = new StringBuilder(8);

			if (Native.ToUnicodeEx((uint) virtualKey, scanCode, new byte[256], buffer, buffer.Capacity, 0x4, layout) != 1)
			{
				Assert.Inconclusive($"The active keyboard layout does not produce a single character for the key '{(char) virtualKey}'.");
			}

			return buffer[0];
		}

		private static long ScanCodeOf(int virtualKey)
		{
			return Native.MapVirtualKeyEx((uint) virtualKey, Native.MAPVK_VK_TO_VSC, Native.GetKeyboardLayout(0));
		}

		/// <summary>
		/// What a window received, plus the state of the modifier keys when it was processed.
		/// </summary>
		private struct Record
		{
			internal bool Alt;
			internal bool Ctrl;
			internal long LParam;
			internal uint Message;
			internal bool Shift;
			internal long WParam;
		}

		/// <summary>
		/// An invisible window on its own thread, which records the input messages it receives. Posted messages are processed in
		/// the order they were sent, so posting a sentinel message and waiting for it guarantees that all messages posted before
		/// were processed - and that no unexpected ones are still on their way.
		/// </summary>
		private sealed class RecordingWindow : IDisposable
		{
			private const uint SENTINEL = 0x8000 + 0x123;
			private const uint WM_QUIT = 0x0012;

			private readonly ManualResetEventSlim ready = new ManualResetEventSlim(false);
			private readonly ConcurrentQueue<Record> records = new ConcurrentQueue<Record>();
			private readonly ManualResetEventSlim sentinelReceived = new ManualResetEventSlim(false);
			private readonly Thread thread;

			private uint threadId;

			internal IntPtr Handle { get; private set; }

			internal RecordingWindow(int x, int y, int width, int height)
			{
				thread = new Thread(() => Run(x, y, width, height)) { IsBackground = true, Name = nameof(RecordingWindow) };
				thread.SetApartmentState(ApartmentState.STA);
				thread.Start();

				Assert.IsTrue(ready.Wait(5000), "The recording window could not be created.");
			}

			public void Dispose()
			{
				PostThreadMessage(threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
				thread.Join(2000);
			}

			/// <summary>
			/// Waits until all messages posted so far were processed and returns them.
			/// </summary>
			internal IList<Record> Flush()
			{
				sentinelReceived.Reset();
				PostMessage(Handle, SENTINEL, IntPtr.Zero, IntPtr.Zero);

				Assert.IsTrue(sentinelReceived.Wait(5000), "The recording window did not process its messages.");

				var result = new List<Record>();

				while (records.TryDequeue(out var record))
				{
					result.Add(record);
				}

				return result;
			}

			private void Run(int x, int y, int width, int height)
			{
				var window = new Recorder(records, sentinelReceived);

				// WS_POPUP: No frame, i.e. client and window coordinates are identical.
				window.CreateHandle(new CreateParams { X = x, Y = y, Width = width, Height = height, Style = unchecked((int) 0x80000000), ExStyle = 0x00000080 });
				Handle = window.Handle;
				threadId = GetCurrentThreadId();
				ready.Set();

				// IMPORTANT: Messages are intentionally not translated, as this would generate character messages for key messages.
				while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
				{
					DispatchMessage(ref message);
				}

				window.DestroyHandle();
			}

			[DllImport("user32.dll")]
			private static extern IntPtr DispatchMessage(ref MSG message);

			[DllImport("kernel32.dll")]
			private static extern uint GetCurrentThreadId();

			[DllImport("user32.dll")]
			private static extern int GetMessage(out MSG message, IntPtr window, uint min, uint max);

			[DllImport("user32.dll")]
			private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

			[DllImport("user32.dll")]
			private static extern bool PostThreadMessage(uint thread, uint message, IntPtr wParam, IntPtr lParam);

			[StructLayout(LayoutKind.Sequential)]
			private struct MSG
			{
				public IntPtr Window;
				public uint Message;
				public IntPtr WParam;
				public IntPtr LParam;
				public uint Time;
				public int X;
				public int Y;
			}

			private sealed class Recorder : NativeWindow
			{
				private readonly ConcurrentQueue<Record> records;
				private readonly ManualResetEventSlim sentinelReceived;

				internal Recorder(ConcurrentQueue<Record> records, ManualResetEventSlim sentinelReceived)
				{
					this.records = records;
					this.sentinelReceived = sentinelReceived;
				}

				protected override void WndProc(ref System.Windows.Forms.Message m)
				{
					var message = (uint) m.Msg;

					if (message == SENTINEL)
					{
						sentinelReceived.Set();
					}
					else if (IsRecorded(message))
					{
						records.Enqueue(new Record
						{
							Alt = IsDown(0x12),
							Ctrl = IsDown(0x11),
							LParam = m.LParam.ToInt64(),
							Message = message,
							Shift = IsDown(0x10),
							WParam = m.WParam.ToInt64()
						});
					}
					else
					{
						base.WndProc(ref m);
					}
				}

				private static bool IsDown(int virtualKey)
				{
					return (GetKeyState(virtualKey) & 0x8000) != 0;
				}

				private static bool IsRecorded(uint message)
				{
					return message == 0x0006 // WM_ACTIVATE
						|| message == 0x0007 // WM_SETFOCUS
						|| message == 0x0008 // WM_KILLFOCUS
						|| message == 0x001C // WM_ACTIVATEAPP
						|| message == 0x0086 // WM_NCACTIVATE
						|| (message >= 0x0100 && message <= 0x0109) // key messages
						|| (message >= 0x0200 && message <= 0x020E) // mouse messages
						|| message == 0x02A3; // WM_MOUSELEAVE
				}

				[DllImport("user32.dll")]
				private static extern short GetKeyState(int virtualKey);
			}
		}
	}
}
