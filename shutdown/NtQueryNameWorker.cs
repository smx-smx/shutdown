#region License
/*
 * Copyright (C) 2024 Stefano Moioli <smxdev4@gmail.com>
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
#endregion
using Shutdown;
using ShutdownLib;
using Smx.SharpIO.Extensions;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Runtime.CompilerServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;

namespace Shutdown
{
    public class NtQueryNameWorker : IDisposable
    {
        private SemaphoreSlim _sema = new SemaphoreSlim(1);
        private Process? _process = null;
        private AnonymousPipeServerStream? _pipeIn = null;
        private AnonymousPipeServerStream? _pipeOut = null;
        private StreamReader? _pipeReader = null;
        private StreamWriter? _pipeWriter = null;
        private CancellationTokenSource? _cts;

        private Task? _startTask = null;
        private Task<string?>? _fetchTask = null;

        private ManualResetEventSlim _serverReady = new ManualResetEventSlim(false);

        private static readonly string _workerPath;

        public delegate void ReadyDelegate(NtQueryNameWorker worker);
        public event ReadyDelegate? OnReady;

        static NtQueryNameWorker()
        {
            var exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (exeDir == null) throw new InvalidOperationException(nameof(exeDir));
            _workerPath = Path.Combine(exeDir, "NtQueryNameWorker.exe");
        }

        private void StopImpl()
        {
            _serverReady.Reset();
            StopPreviousTask();
            _pipeReader?.Dispose();
            _pipeWriter?.Dispose();
            _process?.Kill();

            _fetchTask?.Dispose();
        }

        public void Stop()
        {
            WithLock(StopImpl);
        }

        private void StopPreviousTask()
        {
            if (_cts != null && _fetchTask != null)
            {
                if (!_cts.IsCancellationRequested)
                {
                    _cts.Cancel();
                }
                _cts = null;
                _fetchTask = null;
            }
        }

        private async Task<string?> FetchResult()
        {
            try
            {
                if (_pipeReader == null || _cts == null) return null;
                return await _pipeReader.ReadLineAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        private void WaitReady()
        {
            if (_pipeReader == null)
            {
                throw new InvalidOperationException(nameof(_pipeReader));
            }
            var banner = _pipeReader.ReadLine();
            if (banner != ".")
            {
                throw new InvalidDataException(banner);
            }
            _serverReady.Set();
        }

        private Task RestartAsync()
        {
            _serverReady.Reset();
            return Task.Run(() =>
            {
                Stop();
                Start();
            });
        }

        public bool IsBusy()
        {
            if (!_serverReady.IsSet) return true;
            return TaskRunning(_fetchTask);
        }

        private void NotifyReady()
        {
            if (OnReady != null)
            {
                Task.Run(() =>
                {
                    OnReady?.Invoke(this);
                });
            }
        }

        private string? GetNameImpl(Span<SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX> handles, int index, int timeoutMs = 100)
        {
            if (_pipeOut == null)
            {
                throw new InvalidOperationException(nameof(_pipeOut));
            }
            if (_pipeIn == null)
            {
                throw new InvalidOperationException(nameof(_pipeIn));
            }
            var itemBytes = handles.Slice(index, 1).Cast<SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX, byte>();
            _pipeOut.Write(itemBytes);

            StopPreviousTask();

            _cts = new CancellationTokenSource();
            _fetchTask = Task.Run(FetchResult, _cts.Token);

            if (_fetchTask.Wait(timeoutMs))
            {
                var res = _fetchTask.Result;
                NotifyReady();
                return res;
            }
            else
            {
                RestartAsync();
                return null;
            }
        }

        public string? GetName(Span<SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX> handles, int index, int timeoutMs = 100)
        {
            _sema.Wait();
            try
            {
                return GetNameImpl(handles, index, timeoutMs);
            }
            finally
            {
                _sema.Release();
            }
        }

        private void WithLock(Action act)
        {
            _sema.Wait();
            try
            {
                act();
            }
            finally
            {
                _sema.Release();
            }
        }

        private void StartImpl()
        {
            _pipeIn = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
            _pipeOut = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
            _pipeReader = new StreamReader(_pipeIn);
            _pipeWriter = new StreamWriter(_pipeOut);

            var pi = new ProcessStartInfo
            {
                FileName = _workerPath,
                UseShellExecute = false
            };
            pi.ArgumentList.Add(_pipeOut.GetClientHandleAsString()); // we write, they read
            pi.ArgumentList.Add(_pipeIn.GetClientHandleAsString()); // we read, they write
            _process = Process.Start(pi);

            if (_process == null)
            {
                throw new InvalidOperationException(nameof(_process));
            }
            WaitReady();
            NotifyReady();
        }

        public void Start()
        {
            Stop();
            WithLock(StartImpl);
        }

        private static bool TaskRunning([MaybeNullWhen(false)] Task? t)
        {
            return t != null && t.Status == TaskStatus.Running;
        }

        public Task StartAsync()
        {
            if (TaskRunning(_startTask))
            {
                throw new InvalidOperationException(nameof(_startTask));
            }
            _startTask = Task.Run(() =>
            {
                Start();
                _serverReady.Wait();
            });
            return _startTask;
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
