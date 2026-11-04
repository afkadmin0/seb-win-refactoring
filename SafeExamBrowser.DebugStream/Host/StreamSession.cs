using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using SafeExamBrowser.DebugStream.Interop;
using SafeExamBrowser.DebugStream.Protocol;
using SafeExamBrowser.Logging.Contracts;

namespace SafeExamBrowser.DebugStream.Host
{
	/// <summary>
	/// A connection to a single viewer. While it is running, the selected Firefox window is captured and sent to the viewer,
	/// whereas the input of the viewer is forwarded to the window.
	///
	/// IMPORTANT: Both the thread running the session and the capture thread must live on the desktop of the Firefox windows
	/// (the original desktop), must not own any windows and must be MTA threads (the windows of an STA thread prevent it from
	/// working with the windows of other processes in the required way).
	/// </summary>
	internal sealed class StreamSession : IDisposable
	{
		private const int ACTIVE_INTERVAL_MS = 33;
		private const int IDLE_INTERVAL_MS = 100;
		private const int INACTIVE_INTERVAL_MS = 500;
		private const int LAUNCH_COOLDOWN_MS = 15000;
		private const int WINDOW_LIST_INTERVAL_MS = 1000;

		private readonly ProtocolChannel channel;
		private readonly FirefoxWindowFinder finder;
		private readonly InputInjector injector;
		private readonly ILogger logger;
		private readonly object requestLock = new object();

		private Thread captureThread;
		private bool newWindowRequested;
		private bool refreshRequested;
		private long? selectionRequested;
		private string status;
		private volatile bool running;
		private volatile bool viewerActive = true;
		private volatile bool viewerFocused;

		internal StreamSession(Stream stream, ILogger logger)
		{
			this.channel = new ProtocolChannel(stream);
			this.finder = new FirefoxWindowFinder();
			this.injector = new InputInjector(logger);
			this.logger = logger;
		}

		/// <summary>
		/// Serves the viewer until it disconnects. Blocks the calling thread.
		/// </summary>
		internal void Run()
		{
			running = true;

			try
			{
				Native.MakeThreadDpiAware();
				channel.SendHello(Process.GetCurrentProcess().Id, "SEB debug stream host");

				captureThread = new Thread(CaptureLoop) { IsBackground = true, Name = "DebugStream.Capture" };
				captureThread.SetApartmentState(ApartmentState.MTA);
				captureThread.Start();

				ReadLoop();
			}
			catch (Exception e) when (e is IOException || e is ObjectDisposedException)
			{
				logger.Debug($"The viewer has disconnected ({e.GetType().Name}).");
			}
			finally
			{
				running = false;
			}
		}

		public void Dispose()
		{
			running = false;

			if (captureThread != null && captureThread.IsAlive && !captureThread.Join(3000))
			{
				logger.Warn("The capture thread did not stop in time!");
			}

			// Leave the window in the state the viewer found it in: neither keys nor modifiers pressed, and no pretended focus.
			if (viewerFocused)
			{
				injector.Activate(false);
			}

			injector.ReleaseAll();
		}

		private void CaptureLoop()
		{
			Native.MakeThreadDpiAware();

			var state = new CaptureState();

			using (var capturer = new WindowCapturer())
			{
				while (running)
				{
					var started = Stopwatch.StartNew();
					var interval = ACTIVE_INTERVAL_MS;

					try
					{
						ProcessRequests(state);
						UpdateWindows(state);

						if (state.Target == IntPtr.Zero)
						{
							interval = INACTIVE_INTERVAL_MS / 2;
						}
						else if (!viewerActive)
						{
							interval = INACTIVE_INTERVAL_MS;
						}
						else if (CaptureAndSend(capturer, state))
						{
							state.IdleSince.Restart();
						}
						else if (state.IdleSince.ElapsedMilliseconds > 2000)
						{
							interval = IDLE_INTERVAL_MS;
						}
					}
					catch (Exception e) when (e is IOException || e is ObjectDisposedException)
					{
						running = false;
					}
					catch (Exception e)
					{
						logger.Error("Unexpected error while capturing the window!", e);
						interval = 1000;
					}

					var remaining = interval - (int) started.ElapsedMilliseconds;

					if (running && remaining > 0)
					{
						Thread.Sleep(remaining);
					}
				}
			}

			state.Previous?.Dispose();
		}

		private bool CaptureAndSend(WindowCapturer capturer, CaptureState state)
		{
			var target = state.Target;

			// The window may be minimized by the user (on the original desktop) at any time, in which case it has no content.
			if (Native.IsIconic(target))
			{
				Native.ShowWindowAsync(target, Native.SW_RESTORE);
			}

			var popups = finder.FindPopups(target);

			if (!capturer.TryCapture(target, popups.Reverse().ToList()))
			{
				SetStatus(capturer.Failure);

				return false;
			}

			SetStatus(null);
			injector.UpdateGeometry(capturer.Origin, popups);

			var frame = capturer.Frame;
			var rect = new DirtyRect { X = 0, Y = 0, Width = frame.Width, Height = frame.Height };

			if (state.Previous == null || state.Previous.Width != frame.Width || state.Previous.Height != frame.Height)
			{
				state.Previous?.Dispose();
				state.Previous = new Surface(frame.Width, frame.Height);
			}
			else if (!FrameDiffer.TryGetDirtyRect(state.Previous, frame, out rect))
			{
				return false;
			}

			var header = new FrameHeader { FullWidth = frame.Width, FullHeight = frame.Height, X = rect.X, Y = rect.Y, Width = rect.Width, Height = rect.Height };

			if (state.Buffer.Length < header.PixelByteCount)
			{
				state.Buffer = new byte[header.PixelByteCount];
			}

			for (var row = 0; row < rect.Height; row++)
			{
				Marshal.Copy(IntPtr.Add(frame.Bits, (rect.Y + row) * frame.Stride + rect.X * 4), state.Buffer, row * rect.Width * 4, rect.Width * 4);
			}

			channel.SendFrame(header, state.Buffer);
			state.Previous.CopyFrom(frame, rect.X, rect.Y, rect.Width, rect.Height);

			return true;
		}

		private void OpenNewWindow(CaptureState state)
		{
			state.Launched = true;
			state.LastLaunch.Restart();
			FirefoxLocator.TryOpenNewWindow(logger);
		}

		private void ProcessRequests(CaptureState state)
		{
			bool newWindow, refresh;
			long? selection;

			lock (requestLock)
			{
				newWindow = newWindowRequested;
				refresh = refreshRequested;
				selection = selectionRequested;
				newWindowRequested = false;
				refreshRequested = false;
				selectionRequested = default;
			}

			if (newWindow)
			{
				SetStatus("Opening a new Firefox window on the original desktop...");
				OpenNewWindow(state);
			}

			if (selection.HasValue)
			{
				var window = finder.FindMainWindows().FirstOrDefault(w => w.Handle.ToInt64() == selection.Value);

				if (window != null)
				{
					Select(state, window);
				}
			}

			if (refresh || selection.HasValue || newWindow)
			{
				// Makes sure the list is determined and sent right away, even if it did not change.
				state.ListDue = true;
				state.LastListKey = null;
			}
		}

		private void ReadLoop()
		{
			while (running && channel.TryReceive(out var message))
			{
				try
				{
					switch (message.Type)
					{
						case MessageType.Active:
							viewerActive = message.ParseActive();
							break;
						case MessageType.Bye:
							return;
						case MessageType.Focus:
							viewerFocused = message.ParseFocus();
							injector.Activate(viewerFocused);
							break;
						case MessageType.Key:
							injector.Inject(message.ParseKey());
							break;
						case MessageType.Mouse:
							injector.Inject(message.ParseMouse());
							break;
						case MessageType.NewWindow:
							lock (requestLock) { newWindowRequested = true; }
							break;
						case MessageType.RefreshWindows:
							lock (requestLock) { refreshRequested = true; }
							break;
						case MessageType.SelectWindow:
							lock (requestLock) { selectionRequested = message.ParseSelectWindow(); }
							break;
						default:
							logger.Warn($"Ignoring unexpected message of type {message.Type}.");
							break;
					}
				}
				catch (InvalidDataException e)
				{
					logger.Warn($"Ignoring malformed message of type {message.Type}: {e.Message}");
				}
			}
		}

		private void Select(CaptureState state, StreamWindowInfo window)
		{
			logger.Info($"Streaming window {window}.");

			state.Previous?.Dispose();
			state.Previous = default;
			state.Target = window.Handle;

			// Browsers pause rendering of windows they consider hidden. As the window is not visible to anyone on its desktop
			// anyway, it can safely be put to the top without disturbing anyone, which prevents it from being considered covered.
			if (Native.IsIconic(window.Handle))
			{
				Native.ShowWindowAsync(window.Handle, Native.SW_RESTORE);
			}

			Native.SetWindowPos(window.Handle, Native.HWND_TOP, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
			injector.SetTarget(window.Handle);
		}

		private void SetStatus(string text)
		{
			if (status != text)
			{
				status = text;
				channel.SendStatus(text ?? string.Empty);
			}
		}

		/// <summary>
		/// Periodically determines the available windows, makes sure one of them is streamed and informs the viewer about changes.
		/// </summary>
		private void UpdateWindows(CaptureState state)
		{
			if (!state.ListDue && state.LastList.ElapsedMilliseconds < WINDOW_LIST_INTERVAL_MS)
			{
				return;
			}

			var windows = finder.FindMainWindows();

			state.ListDue = false;
			state.LastList.Restart();

			if (state.Target == IntPtr.Zero || windows.All(w => w.Handle != state.Target))
			{
				var next = windows.FirstOrDefault();

				if (next != null)
				{
					Select(state, next);
				}
				else
				{
					if (state.Target != IntPtr.Zero)
					{
						logger.Info("The streamed window is gone.");
					}

					state.Previous?.Dispose();
					state.Previous = default;
					state.Target = IntPtr.Zero;
					injector.SetTarget(IntPtr.Zero);

					// Without any window there is nothing to look at, so open one - but only once in a while, as starting Firefox is slow.
					if (!state.Launched || state.LastLaunch.ElapsedMilliseconds > LAUNCH_COOLDOWN_MS)
					{
						SetStatus("No Firefox window found on the original desktop, opening one...");
						OpenNewWindow(state);
					}
					else
					{
						SetStatus("Waiting for a Firefox window on the original desktop...");
					}
				}
			}

			var entries = windows.Select(w => new WindowEntry { Handle = w.Handle.ToInt64(), IsSelected = w.Handle == state.Target, Title = w.Title }).ToList();
			var key = string.Join("\n", entries.Select(e => $"{e.Handle}|{e.IsSelected}|{e.Title}"));

			if (key != state.LastListKey)
			{
				state.LastListKey = key;
				channel.SendWindowList(entries);
			}
		}

		/// <summary>
		/// The state which is only used by the capture thread.
		/// </summary>
		private sealed class CaptureState
		{
			internal byte[] Buffer { get; set; } = new byte[0];
			internal Stopwatch IdleSince { get; } = Stopwatch.StartNew();
			internal Stopwatch LastLaunch { get; } = Stopwatch.StartNew();
			internal Stopwatch LastList { get; } = Stopwatch.StartNew();
			internal string LastListKey { get; set; }
			internal bool Launched { get; set; }
			internal bool ListDue { get; set; } = true;
			internal Surface Previous { get; set; }
			internal IntPtr Target { get; set; }
		}
	}
}
