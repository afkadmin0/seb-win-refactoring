using System;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Threading;
using SafeExamBrowser.DebugStream.Protocol;
using SafeExamBrowser.Logging.Contracts;

namespace SafeExamBrowser.DebugStream.Viewer
{
	/// <summary>
	/// The connection from the viewer to the stream host. Connects (and reconnects, e.g. if the host is not yet ready) in the
	/// background and reports everything it receives via events, which are raised on a background thread.
	/// </summary>
	internal sealed class ViewerConnection : IDisposable
	{
		private const int CONNECT_TIMEOUT_MS = 1000;
		private const int RETRY_DELAY_MS = 1500;

		private readonly ManualResetEventSlim stopped = new ManualResetEventSlim(false);
		private readonly ILogger logger;
		private readonly string pipeName;

		private volatile ProtocolChannel channel;
		private Thread thread;

		internal bool IsConnected => channel != null;

		internal event Action Connected;
		internal event Action<string> Disconnected;
		internal event Action<Message> MessageReceived;

		internal ViewerConnection(ILogger logger, string pipeName)
		{
			this.logger = logger;
			this.pipeName = pipeName;
		}

		internal void Start()
		{
			if (thread == null)
			{
				thread = new Thread(Run) { IsBackground = true, Name = "DebugStream.Viewer" };
				thread.Start();
			}
		}

		public void Dispose()
		{
			stopped.Set();

			try
			{
				channel?.SendBye();
			}
			catch
			{
				// The host may already be gone, which is fine.
			}
		}

		internal void Send(Action<ProtocolChannel> send)
		{
			var current = channel;

			if (current != null)
			{
				try
				{
					send(current);
				}
				catch (Exception e) when (e is IOException || e is ObjectDisposedException)
				{
					// The read loop notices the broken connection and reports it.
				}
			}
		}

		private void Run()
		{
			var reason = "Connecting to the debug stream host...";

			while (!stopped.IsSet)
			{
				try
				{
					using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification))
					{
						pipe.Connect(CONNECT_TIMEOUT_MS);

						var current = new ProtocolChannel(pipe);

						if (!current.TryReceive(out var hello) || hello.Type != MessageType.Hello)
						{
							throw new InvalidDataException("The host did not start with a handshake!");
						}

						hello.ParseHello(out var version, out var processId, out _);

						if (version != StreamProtocol.Version)
						{
							throw new InvalidDataException($"The host speaks protocol version {version}, but version {StreamProtocol.Version} is required!");
						}

						logger.Info($"Connected to the debug stream host (process {processId}).");
						channel = current;
						Connected?.Invoke();

						while (!stopped.IsSet && current.TryReceive(out var message))
						{
							MessageReceived?.Invoke(message);
						}

						reason = "The debug stream host has closed the connection.";
					}
				}
				catch (TimeoutException)
				{
					reason = "The debug stream host is not available. Is debug mode enabled on this computer?";
				}
				catch (Exception e)
				{
					reason = $"The connection to the debug stream host failed: {e.Message}";

					if (!(e is IOException) && !(e is ObjectDisposedException))
					{
						logger.Warn(reason);
					}
				}
				finally
				{
					var wasConnected = channel != null;

					channel = null;

					if (!stopped.IsSet)
					{
						if (wasConnected)
						{
							logger.Info("Disconnected from the debug stream host.");
						}

						Disconnected?.Invoke(reason);
					}
				}

				stopped.Wait(RETRY_DELAY_MS);
			}
		}
	}
}
