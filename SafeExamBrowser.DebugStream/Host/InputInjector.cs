using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using SafeExamBrowser.DebugStream.Interop;
using SafeExamBrowser.DebugStream.Protocol;
using SafeExamBrowser.Logging.Contracts;

namespace SafeExamBrowser.DebugStream.Host
{
	/// <summary>
	/// Forwards mouse and keyboard input of the viewer to the streamed window.
	///
	/// <c>SendInput</c> cannot be used for this, as it always targets the desktop currently receiving input - which is the isolated
	/// desktop, not the one the streamed window lives on. Instead, window messages are posted directly to the streamed window. As
	/// the window never receives real input, it neither is active nor knows which modifier keys are pressed, so both need to be
	/// emulated (see <see cref="Activate"/> and <see cref="ApplyModifiers"/>).
	///
	/// Except for <see cref="SetTarget"/> and <see cref="UpdateGeometry"/>, which may be called from any thread, all members must
	/// be called by the same thread (the one reading the input of the viewer), which must live on the desktop of the streamed window.
	/// </summary>
	internal sealed class InputInjector
	{
		private const int SETTLE_DELAY_MS = 20;

		private readonly object @lock = new object();
		private readonly ILogger logger;

		private bool attached;
		private uint attachedThread;
		private bool attachFailureLogged;
		private int lastKeyParameter;
		private IntPtr mainWindow;
		private uint mainWindowThread;
		private KeyModifiers modifiers;
		private POINT origin;
		private KeyEvent? pendingKeyDown;
		private IList<IntPtr> popups = new List<IntPtr>();
		private int targetVersion;
		private int syncedTargetVersion;

		internal InputInjector(ILogger logger)
		{
			this.logger = logger;
		}

		/// <summary>
		/// Informs the injector which window to control. Must be called whenever the streamed window changes.
		/// </summary>
		internal void SetTarget(IntPtr window)
		{
			lock (@lock)
			{
				mainWindow = window;
				mainWindowThread = window == IntPtr.Zero ? 0 : Native.GetThreadIdOf(window);
				targetVersion++;
			}
		}

		/// <summary>
		/// Informs the injector where the streamed image is located on the screen, which is needed to translate positions of the
		/// image to window coordinates. The popups must be ordered from top to bottom.
		/// </summary>
		internal void UpdateGeometry(POINT frameOrigin, IList<IntPtr> popupsTopToBottom)
		{
			lock (@lock)
			{
				origin = frameOrigin;
				popups = popupsTopToBottom ?? new List<IntPtr>();
			}
		}

		/// <summary>
		/// Makes the window believe it is (not) the active window with keyboard focus. Browsers suppress caret blinking, key events
		/// and more as long as they consider themselves inactive, which they do on a desktop which is not receiving input.
		/// </summary>
		internal void Activate(bool active)
		{
			var window = SyncTarget();

			if (window == IntPtr.Zero)
			{
				return;
			}

			if (active)
			{
				Native.PostMessage(window, Native.WM_ACTIVATEAPP, new IntPtr(1), new IntPtr((int) mainWindowThread));
				Native.PostMessage(window, Native.WM_NCACTIVATE, new IntPtr(1), IntPtr.Zero);
				Native.PostMessage(window, Native.WM_ACTIVATE, new IntPtr(1), IntPtr.Zero);
				Native.PostMessage(window, Native.WM_SETFOCUS, IntPtr.Zero, IntPtr.Zero);
			}
			else
			{
				ReleaseAll();

				Native.PostMessage(window, Native.WM_KILLFOCUS, IntPtr.Zero, IntPtr.Zero);
				Native.PostMessage(window, Native.WM_ACTIVATE, IntPtr.Zero, IntPtr.Zero);
				Native.PostMessage(window, Native.WM_NCACTIVATE, IntPtr.Zero, IntPtr.Zero);
				Native.PostMessage(window, Native.WM_ACTIVATEAPP, IntPtr.Zero, new IntPtr((int) mainWindowThread));
			}
		}

		internal void Inject(MouseEvent e)
		{
			var window = SyncTarget();

			if (window == IntPtr.Zero)
			{
				return;
			}

			FlushPendingKeyDown();

			IntPtr target;
			POINT screen;

			lock (@lock)
			{
				screen = new POINT { X = origin.X + e.X, Y = origin.Y + e.Y };
				target = HitTest(screen);
			}

			if (e.Action == MouseAction.Leave)
			{
				Native.PostMessage(target, Native.WM_MOUSELEAVE, IntPtr.Zero, IntPtr.Zero);

				return;
			}

			var client = screen;

			Native.ScreenToClient(target, ref client);

			var position = Native.MakeParameter(client.X, client.Y);
			var flags = e.Flags & 0xFFFF;

			switch (e.Action)
			{
				case MouseAction.Move:
					Native.PostMessage(target, Native.WM_MOUSEMOVE, new IntPtr(flags), position);
					break;
				case MouseAction.LeftDown:
					Native.PostMessage(target, Native.WM_LBUTTONDOWN, new IntPtr(flags), position);
					break;
				case MouseAction.LeftUp:
					Native.PostMessage(target, Native.WM_LBUTTONUP, new IntPtr(flags), position);
					break;
				case MouseAction.RightDown:
					Native.PostMessage(target, Native.WM_RBUTTONDOWN, new IntPtr(flags), position);
					break;
				case MouseAction.RightUp:
					Native.PostMessage(target, Native.WM_RBUTTONUP, new IntPtr(flags), position);
					break;
				case MouseAction.MiddleDown:
					Native.PostMessage(target, Native.WM_MBUTTONDOWN, new IntPtr(flags), position);
					break;
				case MouseAction.MiddleUp:
					Native.PostMessage(target, Native.WM_MBUTTONUP, new IntPtr(flags), position);
					break;
				case MouseAction.Wheel:
					// Wheel messages carry the delta in the high word and the position in screen (!) coordinates.
					Native.PostMessage(target, Native.WM_MOUSEWHEEL, Native.MakeParameter(flags, e.Delta), Native.MakeParameter(screen.X, screen.Y));
					break;
				case MouseAction.HorizontalWheel:
					Native.PostMessage(target, Native.WM_MOUSEHWHEEL, Native.MakeParameter(flags, e.Delta), Native.MakeParameter(screen.X, screen.Y));
					break;
			}
		}

		/// <summary>
		/// A real keyboard produces a key message followed by a character message. Browsers derive the character from the key
		/// message on their own (and handle an additional character message as a second input), so character messages must only be
		/// posted if the browser would not derive the very same character itself - e.g. if the keyboard layouts differ.
		///
		/// For this reason, key presses which may produce a character are held back until the viewer reports the resulting
		/// character (or the release of the key), which happens immediately after the key press.
		/// </summary>
		internal void Inject(KeyEvent e)
		{
			var window = SyncTarget();

			if (window == IntPtr.Zero)
			{
				return;
			}

			switch (e.Action)
			{
				case KeyAction.Down:
					FlushPendingKeyDown();

					// A character which follows must carry the scan code of its key, even if the key press is not posted itself.
					lastKeyParameter = GetKeyParameter(e);

					if (MayProduceCharacter(e))
					{
						pendingKeyDown = e;
					}
					else
					{
						PostKey(window, e);
					}

					break;
				case KeyAction.Char:
					InjectCharacter(window, e);
					break;
				case KeyAction.Up:
					FlushPendingKeyDown();
					PostKey(window, e);
					break;
			}
		}

		/// <summary>
		/// Releases all keys and modifiers which are considered pressed by the streamed window, and detaches from its input queue.
		/// </summary>
		internal void ReleaseAll()
		{
			pendingKeyDown = null;
			ApplyModifiers(KeyModifiers.None);
		}

		/// <summary>
		/// A window determines the state of modifier keys like Ctrl via <c>GetKeyState</c>, which is derived from the real input
		/// its thread received - i.e. none. As a workaround, the state table of the window's input queue is manipulated directly
		/// by attaching to its thread. This is required for any shortcut (Ctrl+T, Ctrl+L, ...) to work.
		///
		/// IMPORTANT: The state is only retained while attached, as Windows resets it from the real keyboard state upon detaching.
		/// For this reason, the thread stays attached for as long as at least one modifier is pressed.
		/// </summary>
		private void ApplyModifiers(KeyModifiers newModifiers)
		{
			uint thread;

			lock (@lock)
			{
				thread = mainWindowThread;
			}

			if (newModifiers == modifiers && (attached || newModifiers == KeyModifiers.None))
			{
				return;
			}

			if (!attached && newModifiers != KeyModifiers.None && thread != 0)
			{
				attached = Native.AttachThreadInput(Native.GetCurrentThreadId(), thread, true);
				attachedThread = thread;

				if (!attached && !attachFailureLogged)
				{
					attachFailureLogged = true;
					logger.Warn("Failed to attach to the input queue of the streamed window, modifier keys will not work!");
				}
			}

			if (attached)
			{
				if (newModifiers == KeyModifiers.None)
				{
					// The key messages posted before are processed asynchronously and still need the modifiers to be set.
					Thread.Sleep(SETTLE_DELAY_MS);
				}

				var state = new byte[256];

				if (Native.GetKeyboardState(state))
				{
					Set(state, newModifiers.HasFlag(KeyModifiers.Ctrl), Native.VK_CONTROL, Native.VK_LCONTROL);
					Set(state, newModifiers.HasFlag(KeyModifiers.Shift), Native.VK_SHIFT, Native.VK_LSHIFT);
					Set(state, newModifiers.HasFlag(KeyModifiers.Alt), Native.VK_MENU, Native.VK_LMENU);
					Set(state, false, Native.VK_RCONTROL, Native.VK_RSHIFT, Native.VK_RMENU);

					if (!Native.SetKeyboardState(state))
					{
						logger.Warn("Failed to set the keyboard state of the streamed window, modifier keys will not work!");
					}
				}
				else
				{
					logger.Warn("Failed to read the keyboard state of the streamed window, modifier keys will not work!");
				}

				if (newModifiers == KeyModifiers.None)
				{
					Detach();
				}
			}

			modifiers = newModifiers;
		}

		private void Detach()
		{
			if (attached)
			{
				Native.AttachThreadInput(Native.GetCurrentThreadId(), attachedThread, false);
				attached = false;
				attachedThread = 0;
			}
		}

		private char? DeriveCharacter(int virtualKey, KeyModifiers keyModifiers)
		{
			var layout = Native.GetKeyboardLayout(mainWindowThread);
			var scanCode = Native.MapVirtualKeyEx((uint) virtualKey, Native.MAPVK_VK_TO_VSC, layout);
			var state = new byte[256];
			var buffer = new StringBuilder(8);

			Set(state, keyModifiers.HasFlag(KeyModifiers.Shift), Native.VK_SHIFT);
			Set(state, keyModifiers.HasFlag(KeyModifiers.Ctrl), Native.VK_CONTROL);
			Set(state, keyModifiers.HasFlag(KeyModifiers.Alt), Native.VK_MENU);

			// Flag 0x4: Do not modify the keyboard state, i.e. do not consume pending dead keys.
			var count = Native.ToUnicodeEx((uint) virtualKey, scanCode, state, buffer, buffer.Capacity, 0x4, layout);

			return count == 1 ? buffer[0] : (char?) null;
		}

		/// <summary>
		/// The scan code and extended key flag of a key as they are part of the <c>LPARAM</c> of key and character messages.
		/// </summary>
		private int GetKeyParameter(KeyEvent e)
		{
			var scanCode = (int) Native.MapVirtualKeyEx((uint) e.VirtualKey, Native.MAPVK_VK_TO_VSC, Native.GetKeyboardLayout(mainWindowThread));

			return (scanCode << 16) | (IsExtended(e.VirtualKey) ? 1 << 24 : 0);
		}

		private void FlushPendingKeyDown()
		{
			if (pendingKeyDown.HasValue)
			{
				var keyDown = pendingKeyDown.Value;

				pendingKeyDown = null;
				PostKey(SyncTarget(), keyDown);
			}
		}

		/// <summary>
		/// Finds the window which is displayed at the given screen position, popups being above the main window. All input is
		/// posted to top-level windows, as the (GPU-composited) child windows of a browser window are transparent to input.
		/// </summary>
		private IntPtr HitTest(POINT screen)
		{
			foreach (var popup in popups)
			{
				if (Native.GetWindowRect(popup, out var rect) && rect.Contains(screen.X, screen.Y))
				{
					return popup;
				}
			}

			return mainWindow;
		}

		private void InjectCharacter(IntPtr window, KeyEvent e)
		{
			// Control characters (Enter, Backspace, Tab, Escape, ...) are always derived from the key message.
			if (e.Character < 0x20 || e.Character == 0x7F)
			{
				FlushPendingKeyDown();

				return;
			}

			if (pendingKeyDown.HasValue)
			{
				var keyDown = pendingKeyDown.Value;

				pendingKeyDown = null;

				if (DeriveCharacter(keyDown.VirtualKey, keyDown.Modifiers) == e.Character)
				{
					PostKey(window, keyDown);

					return;
				}

				// The character cannot be derived from the key by the streamed window, e.g. because of a different keyboard
				// layout. In this case, only the character is sent (and the key press is dropped to avoid duplicate input).
				ApplyModifiers(keyDown.Modifiers);
				PostCharacter(window, e.Character, keyDown.Modifiers.HasFlag(KeyModifiers.Alt) && !keyDown.Modifiers.HasFlag(KeyModifiers.Ctrl));

				return;
			}

			PostCharacter(window, e.Character, false);
		}

		private void PostCharacter(IntPtr window, char character, bool isSystem)
		{
			var parameter = 1 | lastKeyParameter | (isSystem ? 1 << 29 : 0);

			Native.PostMessage(window, isSystem ? Native.WM_SYSCHAR : Native.WM_CHAR, new IntPtr(character), new IntPtr(parameter));
		}

		private void PostKey(IntPtr window, KeyEvent e)
		{
			ApplyModifiers(e.Modifiers);

			var alt = e.Modifiers.HasFlag(KeyModifiers.Alt);
			var keyParameter = GetKeyParameter(e);
			var virtualKey = Normalize(e.VirtualKey);
			var isSystem = alt || virtualKey == Native.VK_MENU || virtualKey == 0x79; // VK_F10
			var parameter = 1 | keyParameter | (alt ? 1 << 29 : 0);

			if (e.Action == KeyAction.Down)
			{
				lastKeyParameter = keyParameter;
				parameter |= e.IsRepeat ? 1 << 30 : 0;

				Native.PostMessage(window, isSystem ? Native.WM_SYSKEYDOWN : Native.WM_KEYDOWN, new IntPtr(virtualKey), new IntPtr(parameter));
			}
			else
			{
				lastKeyParameter = 0;
				parameter |= (1 << 30) | unchecked((int) (1u << 31));

				Native.PostMessage(window, isSystem ? Native.WM_SYSKEYUP : Native.WM_KEYUP, new IntPtr(virtualKey), new IntPtr(parameter));
			}
		}

		/// <summary>
		/// Picks up a change of the streamed window, which invalidates everything which is bound to the previous window.
		/// </summary>
		private IntPtr SyncTarget()
		{
			lock (@lock)
			{
				if (syncedTargetVersion != targetVersion)
				{
					syncedTargetVersion = targetVersion;
					pendingKeyDown = null;
					lastKeyParameter = 0;

					Detach();
					modifiers = KeyModifiers.None;
				}

				return mainWindow;
			}
		}

		private static bool IsExtended(int virtualKey)
		{
			switch (virtualKey)
			{
				case 0x21: // VK_PRIOR
				case 0x22: // VK_NEXT
				case 0x23: // VK_END
				case 0x24: // VK_HOME
				case 0x25: // VK_LEFT
				case 0x26: // VK_UP
				case 0x27: // VK_RIGHT
				case 0x28: // VK_DOWN
				case 0x2C: // VK_SNAPSHOT
				case 0x2D: // VK_INSERT
				case 0x2E: // VK_DELETE
				case 0x5B: // VK_LWIN
				case 0x5C: // VK_RWIN
				case 0x5D: // VK_APPS
				case 0x6F: // VK_DIVIDE
				case 0x90: // VK_NUMLOCK
				case Native.VK_RCONTROL:
				case Native.VK_RMENU:
					return true;
				default:
					return false;
			}
		}

		/// <summary>
		/// Whether the key press might result in a character the streamed window derives from the key itself, i.e. a printable
		/// key which is not part of a shortcut (Ctrl or Alt alone). Ctrl and Alt together are AltGr, which does produce characters.
		/// </summary>
		private static bool MayProduceCharacter(KeyEvent e)
		{
			var ctrl = e.Modifiers.HasFlag(KeyModifiers.Ctrl);
			var alt = e.Modifiers.HasFlag(KeyModifiers.Alt);

			if (ctrl != alt)
			{
				return false;
			}

			var key = e.VirtualKey;

			return key == 0x20 // VK_SPACE
				|| (key >= 0x30 && key <= 0x39) // 0-9
				|| (key >= 0x41 && key <= 0x5A) // A-Z
				|| (key >= 0x60 && key <= 0x6F) // numpad digits and operators
				|| (key >= 0xBA && key <= 0xC0) // OEM keys
				|| (key >= 0xDB && key <= 0xDF) // OEM keys
				|| key == 0xE2; // VK_OEM_102
		}

		/// <summary>
		/// Window messages carry the generic code (VK_CONTROL) of modifier keys, the side is encoded in the scan code.
		/// </summary>
		private static int Normalize(int virtualKey)
		{
			switch (virtualKey)
			{
				case Native.VK_LCONTROL:
				case Native.VK_RCONTROL:
					return Native.VK_CONTROL;
				case Native.VK_LSHIFT:
				case Native.VK_RSHIFT:
					return Native.VK_SHIFT;
				case Native.VK_LMENU:
				case Native.VK_RMENU:
					return Native.VK_MENU;
				default:
					return virtualKey;
			}
		}

		private static void Set(byte[] state, bool pressed, params int[] keys)
		{
			foreach (var key in keys)
			{
				state[key] = (byte) (pressed ? 0x80 : 0x00);
			}
		}
	}
}
