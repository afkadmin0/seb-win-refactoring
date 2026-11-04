using System;
using System.Windows;
using System.Windows.Input;
using SafeExamBrowser.DebugStream.Protocol;
using SafeExamBrowser.Logging.Contracts;

namespace SafeExamBrowser.DebugStream.Viewer
{
	/// <summary>
	/// The entry point for the client: Shows and hides a window containing the stream of a Firefox window of
	/// the original desktop, which is served by the <see cref="Host.StreamHost"/> of the runtime. All members can be called from
	/// any thread, the window itself lives on the user interface thread of the application.
	/// </summary>
	public sealed class StreamViewer : IDisposable
	{
		/// <summary>
		/// Shows and hides the viewer. Must match the sequence detected by the keyboard activator of the client.
		/// </summary>
		public const string ToggleGesture = "Ctrl+Shift+Alt+F12";

		private readonly object @lock = new object();
		private readonly ILogger logger;
		private readonly string pipeName;

		private bool disposed;
		private StreamViewerWindow window;

		public StreamViewer(ILogger logger, Guid runtimeId)
		{
			this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
			this.pipeName = StreamProtocol.GetPipeName(runtimeId);
		}

		public static bool IsToggleGesture(Key key, ModifierKeys modifiers)
		{
			var all = ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Alt;

			return key == Key.F12 && (modifiers & all) == all;
		}

		public void Toggle()
		{
			Run(w => w.Toggle());
		}

		public void Show()
		{
			Run(w => w.ShowViewer());
		}

		public void Hide()
		{
			Run(w => w.HideViewer());
		}

		public void Dispose()
		{
			StreamViewerWindow current;

			lock (@lock)
			{
				disposed = true;
				current = window;
				window = default;
			}

			current?.Terminate();
		}

		private void Run(Action<StreamViewerWindow> action)
		{
			var dispatcher = Application.Current?.Dispatcher;

			if (dispatcher == null)
			{
				logger.Warn("Cannot show the debug stream viewer, as there is no application user interface.");

				return;
			}

			dispatcher.BeginInvoke(new Action(() =>
			{
				try
				{
					lock (@lock)
					{
						if (disposed)
						{
							return;
						}

						if (window == null)
						{
							window = new StreamViewerWindow(logger, pipeName);
						}
					}

					action(window);
				}
				catch (Exception e)
				{
					logger.Error("Failed to operate the debug stream viewer!", e);
				}
			}));
		}
	}
}
