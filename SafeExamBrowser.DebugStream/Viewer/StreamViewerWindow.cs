using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SafeExamBrowser.DebugStream.Protocol;
using SafeExamBrowser.Logging.Contracts;
using MouseAction = SafeExamBrowser.DebugStream.Protocol.MouseAction;

namespace SafeExamBrowser.DebugStream.Viewer
{
	/// <summary>
	/// Shows the stream of a Firefox window of the original desktop and forwards mouse and keyboard input to it.
	/// </summary>
	internal sealed class StreamViewerWindow : Window
	{
		private const int MAX_PENDING_FRAMES = 4;
		private const int MOUSE_MOVE_INTERVAL_MS = 8;
		private const int SC_KEYMENU = 0xF100;
		private const int WM_SYSCOMMAND = 0x0112;

		private readonly ViewerConnection connection;
		private readonly SemaphoreSlim frameSlots = new SemaphoreSlim(MAX_PENDING_FRAMES, MAX_PENDING_FRAMES);
		private readonly Image image = new Image();
		private readonly Border inputHost = new Border();
		private readonly ILogger logger;
		private readonly Stopwatch mouseMoveTimer = Stopwatch.StartNew();
		private readonly TextBlock overlay = new TextBlock();
		private readonly TextBlock statusText = new TextBlock();
		private readonly ComboBox windowList = new ComboBox();

		private bool allowClose;
		private bool disposed;
		private string hostStatus = string.Empty;
		private bool updatingList;
		private WriteableBitmap bitmap;

		internal StreamViewerWindow(ILogger logger, string pipeName)
		{
			this.logger = logger;
			this.connection = new ViewerConnection(logger, pipeName);

			InitializeLayout();
			InitializeInput();
			InitializeConnection();
		}

		/// <summary>
		/// Closes the window for good, whereas closing it by the user only hides it.
		/// </summary>
		internal void Terminate()
		{
			disposed = true;
			connection.Dispose();

			if (Dispatcher.CheckAccess())
			{
				allowClose = true;
				Close();
			}
			else
			{
				Dispatcher.BeginInvoke(new Action(() =>
				{
					allowClose = true;
					Close();
				}));
			}
		}

		internal void ShowViewer()
		{
			connection.Start();

			if (!IsVisible)
			{
				Show();
			}

			if (WindowState == WindowState.Minimized)
			{
				WindowState = WindowState.Normal;
			}

			Activate();
			Keyboard.Focus(inputHost);
			connection.Send(c => c.SendActive(true));
		}

		internal void HideViewer()
		{
			connection.Send(c => c.SendFocus(false));
			connection.Send(c => c.SendActive(false));
			Hide();
		}

		internal void Toggle()
		{
			if (IsVisible)
			{
				HideViewer();
			}
			else
			{
				ShowViewer();
			}
		}

		protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
		{
			if (!allowClose)
			{
				e.Cancel = true;
				HideViewer();
			}

			base.OnClosing(e);
		}

		protected override void OnSourceInitialized(EventArgs e)
		{
			base.OnSourceInitialized(e);

			(PresentationSource.FromVisual(this) as HwndSource)?.AddHook(WindowProcedure);
		}

		private void ApplyFrame(Message message)
		{
			var header = message.ParseFrame();

			if (bitmap == null || bitmap.PixelWidth != header.FullWidth || bitmap.PixelHeight != header.FullHeight)
			{
				// Matching the DPI of the window makes one pixel of the stream appear as one pixel on the screen.
				var dpi = VisualTreeHelper.GetDpi(this);

				bitmap = new WriteableBitmap(header.FullWidth, header.FullHeight, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Bgr32, null);
				image.Source = bitmap;
				overlay.Visibility = Visibility.Collapsed;
				UpdateStatus();
			}

			if (header.Width > 0 && header.Height > 0)
			{
				var pixels = GCHandle.Alloc(message.Payload, GCHandleType.Pinned);

				try
				{
					var start = IntPtr.Add(pixels.AddrOfPinnedObject(), FrameHeader.Size);

					bitmap.WritePixels(new Int32Rect(0, 0, header.Width, header.Height), start, header.PixelByteCount, header.Width * 4, header.X, header.Y);
				}
				finally
				{
					pixels.Free();
				}
			}
		}

		private static int GetMouseFlags(MouseEventArgs e)
		{
			var flags = 0;

			flags |= e.LeftButton == MouseButtonState.Pressed ? 0x0001 : 0; // MK_LBUTTON
			flags |= e.RightButton == MouseButtonState.Pressed ? 0x0002 : 0; // MK_RBUTTON
			flags |= Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0x0004 : 0; // MK_SHIFT
			flags |= Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? 0x0008 : 0; // MK_CONTROL
			flags |= e.MiddleButton == MouseButtonState.Pressed ? 0x0010 : 0; // MK_MBUTTON

			return flags;
		}

		private static KeyModifiers GetModifiers()
		{
			var modifiers = KeyModifiers.None;

			modifiers |= Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? KeyModifiers.Ctrl : KeyModifiers.None;
			modifiers |= Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? KeyModifiers.Shift : KeyModifiers.None;
			modifiers |= Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) ? KeyModifiers.Alt : KeyModifiers.None;

			return modifiers;
		}

		private static Key GetKey(KeyEventArgs e)
		{
			switch (e.Key)
			{
				case Key.System:
					return e.SystemKey;
				case Key.ImeProcessed:
					return e.ImeProcessedKey;
				case Key.DeadCharProcessed:
					return e.DeadCharProcessedKey;
				default:
					return e.Key;
			}
		}

		private void InitializeConnection()
		{
			connection.Connected += () =>
			{
				connection.Send(c => c.SendActive(IsVisible));
				connection.Send(c => c.SendFocus(inputHost.IsKeyboardFocusWithin));
				Dispatcher.BeginInvoke(new Action(() => SetHostStatus("Connected.")));
			};

			connection.Disconnected += reason =>
			{
				Dispatcher.BeginInvoke(new Action(() =>
				{
					bitmap = null;
					image.Source = null;
					windowList.ItemsSource = null;
					overlay.Text = reason;
					overlay.Visibility = Visibility.Visible;
					SetHostStatus(string.Empty);
				}));
			};

			connection.MessageReceived += OnMessageReceived;
		}

		private void InitializeInput()
		{
			inputHost.Focusable = true;
			inputHost.FocusVisualStyle = null;

			inputHost.GotKeyboardFocus += (o, e) => connection.Send(c => c.SendFocus(true));
			inputHost.LostKeyboardFocus += (o, e) => connection.Send(c => c.SendFocus(false));
			inputHost.MouseDown += InputHost_MouseDown;
			inputHost.MouseLeave += (o, e) => SendMouse(MouseAction.Leave, e, force: true);
			inputHost.MouseMove += InputHost_MouseMove;
			inputHost.MouseUp += InputHost_MouseUp;
			inputHost.MouseWheel += (o, e) => SendMouse(MouseAction.Wheel, e, e.Delta, true);
			inputHost.PreviewKeyDown += (o, e) => SendKey(KeyAction.Down, e);
			inputHost.PreviewKeyUp += (o, e) => SendKey(KeyAction.Up, e);
			inputHost.PreviewTextInput += InputHost_PreviewTextInput;

			Deactivated += (o, e) => connection.Send(c => c.SendFocus(false));
			Activated += (o, e) =>
			{
				if (inputHost.IsKeyboardFocusWithin)
				{
					connection.Send(c => c.SendFocus(true));
				}
			};
		}

		private void InitializeLayout()
		{
			Background = Brushes.Black;
			Height = Math.Max(480, SystemParameters.WorkArea.Height * 0.85);
			ShowInTaskbar = true;
			Title = $"DEBUG - Firefox of the original desktop ({StreamViewer.ToggleGesture} to show / hide)";
			Topmost = true;
			Width = Math.Max(640, SystemParameters.WorkArea.Width * 0.85);
			WindowStartupLocation = WindowStartupLocation.CenterScreen;

			var banner = new TextBlock
			{
				FontWeight = FontWeights.Bold,
				Foreground = Brushes.Red,
				Margin = new Thickness(6, 0, 12, 0),
				Text = "DEBUG MODE",
				VerticalAlignment = VerticalAlignment.Center
			};

			windowList.MinWidth = 380;
			windowList.MaxWidth = 700;
			windowList.DisplayMemberPath = nameof(WindowEntry.Title);
			windowList.SelectionChanged += WindowList_SelectionChanged;
			windowList.ToolTip = "The Firefox windows of the original desktop";

			var newWindow = new Button { Content = "New window", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(8, 2, 8, 2), Focusable = false };
			var refresh = new Button { Content = "Refresh", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(8, 2, 8, 2), Focusable = false };

			newWindow.Click += (o, e) => connection.Send(c => c.SendNewWindow());
			refresh.Click += (o, e) => connection.Send(c => c.SendRefreshWindows());
			statusText.Margin = new Thickness(12, 0, 0, 0);
			statusText.VerticalAlignment = VerticalAlignment.Center;

			var toolbar = new ToolBar();

			toolbar.Items.Add(banner);
			toolbar.Items.Add(windowList);
			toolbar.Items.Add(newWindow);
			toolbar.Items.Add(refresh);
			toolbar.Items.Add(statusText);

			image.HorizontalAlignment = HorizontalAlignment.Center;
			image.SnapsToDevicePixels = true;
			image.Stretch = Stretch.Uniform;
			image.StretchDirection = StretchDirection.DownOnly;
			image.VerticalAlignment = VerticalAlignment.Center;
			RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);

			overlay.Foreground = Brushes.White;
			overlay.HorizontalAlignment = HorizontalAlignment.Center;
			overlay.IsHitTestVisible = false;
			overlay.Text = "Connecting to the debug stream host...";
			overlay.TextAlignment = TextAlignment.Center;
			overlay.TextWrapping = TextWrapping.Wrap;
			overlay.VerticalAlignment = VerticalAlignment.Center;

			var content = new Grid();

			content.Children.Add(image);
			content.Children.Add(overlay);
			inputHost.Background = Brushes.Black;
			inputHost.Child = content;

			var layout = new DockPanel();

			DockPanel.SetDock(toolbar, Dock.Top);
			layout.Children.Add(toolbar);
			layout.Children.Add(inputHost);
			Content = layout;
		}

		private void InputHost_MouseDown(object sender, MouseButtonEventArgs e)
		{
			inputHost.Focus();
			Keyboard.Focus(inputHost);
			inputHost.CaptureMouse();

			switch (e.ChangedButton)
			{
				case MouseButton.Left:
					SendMouse(MouseAction.LeftDown, e, force: true);
					break;
				case MouseButton.Middle:
					SendMouse(MouseAction.MiddleDown, e, force: true);
					break;
				case MouseButton.Right:
					SendMouse(MouseAction.RightDown, e, force: true);
					break;
			}

			e.Handled = true;
		}

		private void InputHost_MouseMove(object sender, MouseEventArgs e)
		{
			if (mouseMoveTimer.ElapsedMilliseconds >= MOUSE_MOVE_INTERVAL_MS)
			{
				mouseMoveTimer.Restart();
				SendMouse(MouseAction.Move, e);
			}
		}

		private void InputHost_MouseUp(object sender, MouseButtonEventArgs e)
		{
			switch (e.ChangedButton)
			{
				case MouseButton.Left:
					SendMouse(MouseAction.LeftUp, e, force: true);
					break;
				case MouseButton.Middle:
					SendMouse(MouseAction.MiddleUp, e, force: true);
					break;
				case MouseButton.Right:
					SendMouse(MouseAction.RightUp, e, force: true);
					break;
			}

			if (e.LeftButton == MouseButtonState.Released && e.MiddleButton == MouseButtonState.Released && e.RightButton == MouseButtonState.Released)
			{
				inputHost.ReleaseMouseCapture();
			}

			e.Handled = true;
		}

		private void InputHost_PreviewTextInput(object sender, TextCompositionEventArgs e)
		{
			var modifiers = GetModifiers();

			foreach (var character in e.Text ?? string.Empty)
			{
				connection.Send(c => c.SendKey(new KeyEvent { Action = KeyAction.Char, Character = character, Modifiers = modifiers }));
			}

			e.Handled = true;
		}

		private void OnMessageReceived(Message message)
		{
			try
			{
				switch (message.Type)
				{
					case MessageType.Frame:
						// Frames only contain the changed part of the image, so none of them may be dropped. Limiting the number of
						// frames in flight prevents a slow user interface from being flooded.
						while (!disposed && !frameSlots.Wait(100))
						{
						}

						if (!disposed)
						{
							Dispatcher.BeginInvoke(new Action(() =>
							{
								try
								{
									ApplyFrame(message);
								}
								catch (Exception e)
								{
									logger.Warn($"Failed to display a frame: {e.Message}");
								}
								finally
								{
									frameSlots.Release();
								}
							}), DispatcherPriority.Render);
						}

						break;
					case MessageType.Status:
						var status = message.ParseStatus();

						Dispatcher.BeginInvoke(new Action(() => SetHostStatus(status)));
						break;
					case MessageType.WindowList:
						var windows = message.ParseWindowList();

						Dispatcher.BeginInvoke(new Action(() => UpdateWindowList(windows)));
						break;
				}
			}
			catch (Exception e)
			{
				logger.Warn($"Failed to process message of type {message.Type}: {e.Message}");
			}
		}

		private void SendKey(KeyAction action, KeyEventArgs e)
		{
			var key = GetKey(e);
			var modifiers = GetModifiers();

			// The gesture is handled globally, so it must not end up in Firefox.
			if (StreamViewer.IsToggleGesture(key, Keyboard.Modifiers))
			{
				e.Handled = true;

				return;
			}

			var virtualKey = KeyInterop.VirtualKeyFromKey(key);

			if (virtualKey != 0)
			{
				connection.Send(c => c.SendKey(new KeyEvent { Action = action, VirtualKey = virtualKey, Modifiers = modifiers, IsRepeat = e.IsRepeat }));
			}

			// Prevents the viewer itself from reacting, e.g. by moving the focus on Tab or by opening the system menu on Alt.
			e.Handled = true;
		}

		private void SendMouse(MouseAction action, MouseEventArgs e, int delta = 0, bool force = false)
		{
			var position = e.GetPosition(image);

			if (bitmap == null || image.ActualWidth <= 0 || image.ActualHeight <= 0)
			{
				return;
			}

			var x = (int) Math.Floor(position.X / image.ActualWidth * bitmap.PixelWidth);
			var y = (int) Math.Floor(position.Y / image.ActualHeight * bitmap.PixelHeight);
			var inside = x >= 0 && y >= 0 && x < bitmap.PixelWidth && y < bitmap.PixelHeight;

			// While dragging (mouse captured), the position is clamped to the image so that e.g. selections continue to work.
			if (!inside && !inputHost.IsMouseCaptured && action != MouseAction.Leave)
			{
				return;
			}

			x = Math.Max(0, Math.Min(bitmap.PixelWidth - 1, x));
			y = Math.Max(0, Math.Min(bitmap.PixelHeight - 1, y));

			connection.Send(c => c.SendMouse(new MouseEvent { Action = action, X = x, Y = y, Delta = delta, Flags = GetMouseFlags(e) }));
		}

		private void SetHostStatus(string text)
		{
			hostStatus = text ?? string.Empty;
			UpdateStatus();
		}

		private void UpdateStatus()
		{
			var size = bitmap != null ? $"{bitmap.PixelWidth}x{bitmap.PixelHeight}" : string.Empty;

			statusText.Text = string.Join("  |  ", new[] { hostStatus, size }.Where(s => !string.IsNullOrWhiteSpace(s)));

			if (!string.IsNullOrWhiteSpace(hostStatus) && bitmap == null)
			{
				overlay.Text = hostStatus;
				overlay.Visibility = Visibility.Visible;
			}
		}

		private void UpdateWindowList(IList<WindowEntry> windows)
		{
			updatingList = true;

			try
			{
				windowList.ItemsSource = windows;
				windowList.SelectedItem = windows.FirstOrDefault(w => w.IsSelected);
			}
			finally
			{
				updatingList = false;
			}
		}

		private void WindowList_SelectionChanged(object sender, SelectionChangedEventArgs e)
		{
			if (!updatingList && windowList.SelectedItem is WindowEntry entry)
			{
				connection.Send(c => c.SendSelectWindow(entry.Handle));
			}
		}

		/// <summary>
		/// Keeps the keyboard menu of the window (activated by Alt or F10) from stealing the input meant for Firefox.
		/// </summary>
		private IntPtr WindowProcedure(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
		{
			if (message == WM_SYSCOMMAND && (wParam.ToInt64() & 0xFFF0) == SC_KEYMENU && inputHost.IsKeyboardFocusWithin)
			{
				handled = true;
			}

			return IntPtr.Zero;
		}
	}
}
