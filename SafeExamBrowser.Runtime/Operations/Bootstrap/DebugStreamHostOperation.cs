/*
 * Copyright (c) 2026 ETH Zürich, IT Services
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System;
using SafeExamBrowser.Core.Contracts.OperationModel;
using SafeExamBrowser.Core.Contracts.OperationModel.Events;
using SafeExamBrowser.DebugStream.Host;
using SafeExamBrowser.Logging.Contracts;

namespace SafeExamBrowser.Runtime.Operations.Bootstrap
{
	/// <summary>
	/// Starts the host which streams Firefox windows of the original desktop into the sessions (see the documentation of the debug
	/// stream project). As the runtime always lives on the original desktop and exists independent of sessions, Firefox is available
	/// in any session. Always enqueued, but a no-op unless <see cref="SafeExamBrowser.DebugStream.DebugModeConfiguration.IsEnabled"/>
	/// (checked by <see cref="StreamHost.Start"/> itself).
	/// </summary>
	internal class DebugStreamHostOperation : IOperation
	{
		private readonly StreamHost host;
		private readonly ILogger logger;

		public event StatusChangedEventHandler StatusChanged { add { } remove { } }

		public DebugStreamHostOperation(StreamHost host, ILogger logger)
		{
			this.host = host;
			this.logger = logger;
		}

		public OperationResult Perform()
		{
			try
			{
				host.Start();
			}
			catch (Exception e)
			{
				// A diagnostic tool must never keep the application from starting.
				logger.Error("Failed to start the debug stream host!", e);
			}

			return OperationResult.Success;
		}

		public OperationResult Revert()
		{
			try
			{
				host.Stop();
			}
			catch (Exception e)
			{
				logger.Error("Failed to stop the debug stream host!", e);
			}

			return OperationResult.Success;
		}
	}
}
