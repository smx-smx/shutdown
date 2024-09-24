#region License
/*
 * Copyright (C) 2024 Stefano Moioli <smxdev4@gmail.com>
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
#endregion
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ShutdownLib;

namespace Shutdown
{
    public class NtQueryNameIpc : INtQueryNameWorker, IDisposable
    {

        private (NtQueryNameWorker worker, Task startTask)[] StartWorkersImpl()
        {
            var workersInfo = Enumerable.Range(0, 2)
                            .Select(_ =>
                            {
                                var w = new NtQueryNameWorker();
                                w.OnReady += (w) =>
                                {
                                    _workerAvailable.Set();
                                };
                                var startTask = w.StartAsync();
                                return (w, startTask);
                            })
                            .ToArray();
            return workersInfo;

        }

        private AutoResetEvent _workerAvailable;
        private NtQueryNameWorker[]? _workers;

        private void Stop()
        {
            if (_workers == null) return;
            foreach (var worker in _workers)
            {
                worker.Dispose();
            }
        }

        public void Start()
        {
            Stop();
            var workersStartup = StartWorkersImpl();
            Task.WaitAll(workersStartup.Select(x => x.startTask).ToArray());
            _workers = workersStartup.Select(x => x.worker).ToArray();
        }

        public NtQueryNameIpc()
        {
            _workerAvailable = new AutoResetEvent(false);
            _workers = null;
        }

        public string? GetName(Span<SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX> handles, int index, int timeoutMs = 100)
        {
            if (_workers == null) throw new InvalidOperationException("Workers not started");

            string? name;
            while (true)
            {
                var avail = _workers.FirstOrDefault(w => !w.IsBusy());
                if (avail == null)
                {
                    _workerAvailable.WaitOne(200);
                    continue;
                }
                name = avail.GetName(handles, index);
                break;
            }
            return name;
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
