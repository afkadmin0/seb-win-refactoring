using System;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using SafeExamBrowser.DebugStream.Protocol;
using SafeExamBrowser.Logging.Contracts;

namespace SafeExamBrowser.DebugStream.Host
{
	/// <summary>
	/// Lets a viewer on a different desktop (usually the isolated desktop of a running session) use Firefox windows of the original
	/// desktop, by streaming them via a named pipe: Frames of the window are captured and sent to the viewer, whereas the mouse and
	/// keyboard input of the viewer is forwarded to the window.
	///
	/// The host must live in a process on the original desktop, i.e. the runtime. It accepts one viewer at a time and is independent
	/// of the sessions the runtime starts and stops, so Firefox is available in any session.
	///
	/// <see cref="Start"/> does nothing unless <see cref="SafeExamBrowser.DebugStream.DebugModeConfiguration.IsEnabled"/> - this is the choke point that keeps a
	/// plain, unmodified installation from ever opening the pipe.
	/// </summary>
	public sealed class StreamHost : IDisposable
	{
		private readonly ILogger logger;
		private readonly string pipeName;

		private Thread acceptThread;
		private volatile bool running;

		public StreamHost(ILogger logger, Guid runtimeId)
		{
			this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
			this.pipeName = StreamProtocol.GetPipeName(runtimeId);
		}

		public void Start()
		{
			if (running)
			{
				return;
			}

			if (!SafeExamBrowser.DebugStream.DebugModeConfiguration.IsEnabled)
			{
				logger.Debug("Debug mode is not enabled, the debug stream host will not be started.");

				return;
			}

			running = true;
			acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "DebugStream.Accept" };
			acceptThread.SetApartmentState(ApartmentState.MTA);
			acceptThread.Start();

			logger.Warn("*** DEBUG MODE: Firefox of the original desktop can be streamed into the session via the debug stream! ***");
			logger.Info($"Debug stream host is listening on '{pipeName}'.");
		}

		public void Stop()
		{
			running = false;

			if (acceptThread != null && acceptThread.IsAlive && !acceptThread.Join(5000))
			{
				logger.Warn("The debug stream host did not stop in time!");
			}

			acceptThread = default;
		}

		public void Dispose()
		{
			Stop();
		}

		private void AcceptLoop()
		{
			while (running)
			{
				NamedPipeServerStream pipe = null;

				try
				{
					pipe = CreatePipe();

					var connection = pipe.BeginWaitForConnection(null, null);

					while (running && !connection.AsyncWaitHandle.WaitOne(250))
					{
					}

					if (!running)
					{
						break;
					}

					pipe.EndWaitForConnection(connection);
					logger.Info("A debug stream viewer has connected.");

					using (var session = new StreamSession(pipe, logger))
					{
						session.Run();
					}

					logger.Info("The debug stream viewer has disconnected.");
				}
				catch (Exception e)
				{
					if (running)
					{
						logger.Error("Unexpected error in the debug stream host!", e);
						Thread.Sleep(1000);
					}
				}
				finally
				{
					pipe?.Dispose();
				}
			}
		}

		/// <summary>
		/// Only the user running the runtime may connect, and never via the network.
		/// </summary>
		private NamedPipeServerStream CreatePipe()
		{
			var security = new PipeSecurity();

			security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
			security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.FullControl, AccessControlType.Allow));

			// IMPORTANT: The pipe must be asynchronous. A synchronous pipe handle serializes all operations, i.e. a pending read
			// (waiting for the input of the viewer) would block every write (the frames) until the viewer sends something.
			return new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 64 * 1024, 64 * 1024, security);
		}
	}
}
