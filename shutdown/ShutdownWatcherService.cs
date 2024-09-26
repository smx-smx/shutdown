#region License
/*
 * Copyright (C) 2024 Stefano Moioli <smxdev4@gmail.com>
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
#endregion
using Microsoft.Extensions.Hosting;
using Shutdown.Components;
using ShutdownLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Shutdown
{
    public class ShutdownWatcherService : IHostedService, IDisposable
    {
        private readonly ShutdownWatcher _watcher;

        public ShutdownWatcherService(ShutdownWatcher watcher)
        {
            _watcher = watcher;
        }

        public void Dispose()
        {
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                Task.Run(() =>
                {
                    _watcher.Execute();
                });
            }, cancellationToken);
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _watcher.Stop();
            return Task.CompletedTask;
        }
    }
}
