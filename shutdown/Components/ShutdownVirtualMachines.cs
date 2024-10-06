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
using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ShutdownLib;
using Smx.SharpIO.Memory;
using Smx.Winter;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;
using static ShutdownLib.Ntdll;

namespace Shutdown.Components
{
    public class ShutdownOrderComparer : IComparer<int>
    {
        public int Compare(int a, int b)
        {
            if (Math.Sign(a) == Math.Sign(b))
            {
                return a.CompareTo(b);
            }
            // non-negative values should come before negative values
            return a >= 0 ? -1 : 1;
        }
    }

    public class ShutdownVirtualMachinesFactory
    {
        private readonly ILoggerFactory _factory;
        private readonly INtQueryNameWorkerProvider _workerProvider;

        public ShutdownVirtualMachinesFactory(
            ILoggerFactory factory,
            INtQueryNameWorkerProvider workerProvider
        )
        {
            _factory = factory;
            _workerProvider = workerProvider;
        }

        public ShutdownVirtualMachines Create(ShutdownVmParams opts)
        {
            return new ShutdownVirtualMachines(_workerProvider, _factory, opts);
        }
    }

    public class VmInstance
    {
        public required string VmxPath { get; set; }
        public required Process VmProcess { get; set; }
        public required IList<string> VmCommandLine { get; set; }
    }


    public class ShutdownVmParams
    {
        public static ShutdownVmOptions DefaultVmOptions => new ShutdownVmOptions
        {
            VmxPath = string.Empty,
            Order = 0,
            FirstAction = VmShutdownType.Shutdown,
            FirstActionMode = VmShutdownMode.Soft,
            SecondAction = VmShutdownType.Suspend,
            SecondActionMode = VmShutdownMode.Hard
        };


        public bool DryRun { get; set; } = false;
        public ShutdownVmOptions DefaultOptions { get; set; } = DefaultVmOptions;
        public IList<ShutdownVmOptions> Items { get; set; } = new List<ShutdownVmOptions>();
        public ShutdownVirtualMachinesFlags Flags { get; set; }
    }

    public class ShutdownVirtualMachines : IAction
    {
        private readonly ILogger<ShutdownVirtualMachines> _logger;

        private readonly Dictionary<string, ShutdownVmOptions> vmxToOpts;
        private readonly ShutdownVmParams _opts;
        private readonly CloseOpenHandlesFactory _closeHandlesFactory;
        private readonly ILoggerFactory _loggerFactory;
        private readonly List<CloseOpenHandlesItem> _closeHandleItems;

        public ShutdownVirtualMachines(
            INtQueryNameWorkerProvider workerProvider,
            ILoggerFactory loggerFactory,
            ShutdownVmParams opts
        )
        {
            _loggerFactory = loggerFactory;
            _closeHandleItems = new List<CloseOpenHandlesItem>();
            _logger = loggerFactory.CreateLogger<ShutdownVirtualMachines>();
            vmxToOpts = new Dictionary<string, ShutdownVmOptions>(StringComparer.InvariantCultureIgnoreCase);
            _opts = opts;

            foreach (var item in opts.Items)
            {
                if (string.IsNullOrWhiteSpace(item.VmxPath)) continue;
                vmxToOpts.Add(item.VmxPath, item);
            }

            _closeHandlesFactory = new CloseOpenHandlesFactory(_loggerFactory, workerProvider);
        }

        private Dictionary<string, string> ParseVmx(string vmxPath)
        {
            using var fh = new FileStream(vmxPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var sr = new StreamReader(fh);

            var kv = new Dictionary<string, string>();
            while (!sr.EndOfStream)
            {
                var line = sr.ReadLine();
                if (line == null) break;

                var equal = line.IndexOf('=');
                if (equal < 0) continue;
                var left = line.Substring(0, equal).Trim();
                var right = line.Substring(equal + 1).Trim();
                if (right.StartsWith("\"") && right.EndsWith("\""))
                {
                    right = right.Substring(1, right.Length - 2);
                }
                kv.Add(left, right);
            }
            return kv;
        }

        private Process? VmRun(params string[] argv)
        {
            if (_vmRunPath == null) throw new InvalidOperationException(nameof(_vmRunPath));
            var pi = new ProcessStartInfo()
            {
                FileName = _vmRunPath,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            pi.ArgumentList.Add("-T");
            pi.ArgumentList.Add("ws");
            foreach (var arg in argv)
            {
                pi.ArgumentList.Add(arg);
            }
            _logger.LogDebug(string.Join(" ", pi.ArgumentList));
            return Process.Start(pi);
        }

        private Process? SuspendVm(string vmxFilePath, bool hardMode)
        {
            var shutdownMode = hardMode ? "hard" : "soft";
            return VmRun("suspend", vmxFilePath, shutdownMode);
        }

        private Process? ShutdownVm(string vmxFilePath, bool hardMode)
        {
            var shutdownMode = hardMode ? "hard" : "soft";
            return VmRun("stop", vmxFilePath, shutdownMode);
        }

        private string? GetVmRunPath(string vmxExe)
        {
            if (!File.Exists(vmxExe)) return null;
            var vmwareBase = Path.GetDirectoryName(Path.GetDirectoryName(vmxExe));
            if (!Directory.Exists(vmwareBase)) return null;
            var vmRun = Path.Combine(vmwareBase, "vmrun.exe");
            if (File.Exists(vmRun)) return vmRun;
            return null;
        }

        private string? _vmRunPath = null;

        private Process? StartShutdown(ShutdownState state, VmInstance vm, VmShutdownType type, VmShutdownMode mode)
        {
            var sAction = Enum.GetName(type);
            var sMode = Enum.GetName(mode);
            _logger.LogInformation($"Execute {sAction},{sMode} on {vm.VmxPath}");

            var vmxFileName = Path.GetFileNameWithoutExtension(vm.VmxPath);
            state.SetShutdownStatusMessage($"{sAction} {sMode} - {vmxFileName}");

            switch (mode)
            {
                case VmShutdownMode.Soft:
                case VmShutdownMode.Hard:
                    break;
                default:
                    throw new NotSupportedException(sMode);
            }

            var hardMode = mode == VmShutdownMode.Hard ? true : false;

            switch (type)
            {
                case VmShutdownType.Suspend:
                    return SuspendVm(vm.VmxPath, hardMode);
                case VmShutdownType.Shutdown:
                    return ShutdownVm(vm.VmxPath, hardMode);
                case VmShutdownType.Kill:
                    vm.VmProcess.Kill();
                    break;
            }
            return null;
        }

        private bool ShutdownVmFlow(ShutdownState state, VmInstance vm, ShutdownVmOptions opts)
        {
            var vmProc = vm.VmProcess;

            if (vmProc.HasExited) return false;
            if (_opts.DryRun) return true;

            //var vmx = ParseVmx(vmxFilePath);
            //if(!vmx.TryGetValue("displayName", out var vmDisplayName)){
            //var vmDisplayName = Path.GetFileNameWithoutExtension(vmxFilePath) ?? vmxFilePath;

            /** phase 1 **/
            var shutdownProc = StartShutdown(state, vm, opts.FirstAction, opts.FirstActionMode);
            vmProc.WaitForExit(TimeSpan.FromSeconds(opts.FirstActionTimeoutSeconds));
            if (shutdownProc != null && !shutdownProc.HasExited)
            {
                shutdownProc.Kill();
            }

            if (vmProc.HasExited) return true;

            shutdownProc = StartShutdown(state, vm, opts.SecondAction, opts.SecondActionMode);
            vmProc.WaitForExit(TimeSpan.FromSeconds(opts.SecondActionTimeoutSeconds));
            if (shutdownProc != null && !shutdownProc.HasExited)
            {
                shutdownProc.Kill();
            }

            return vmProc.HasExited;
        }

        private VmInstance? ToVmInstance(Process proc)
        {
            var cmdline = ProcessUtil.GetCommandLine(proc);
            if (cmdline == null)
            {
                return null;
            }
            var parsedCmdline = ProcessUtil.ParseCommandLine(cmdline);
            if (parsedCmdline.Count < 2)
            {
                return null;
            }

            var vmxExe = parsedCmdline.First();
            if (!File.Exists(vmxExe))
            {
                return null;
            }

            var vmxFilePath = parsedCmdline.Last();


            if (!vmxFilePath.EndsWith(".vmx", StringComparison.InvariantCultureIgnoreCase))
            {
                return null;
            }
            if (!File.Exists(vmxFilePath))
            {
                return null;
            }

            var instance = new VmInstance
            {
                VmProcess = proc,
                VmxPath = vmxFilePath,
                VmCommandLine = parsedCmdline
            };
            return instance;
        }

        private Dictionary<string, VmInstance> GetRunningVms()
        {
            var vmInstances = new Dictionary<string, VmInstance>(StringComparer.InvariantCultureIgnoreCase);

            var vmProcs = Process.GetProcessesByName("vmware-vmx");
            foreach (var proc in vmProcs)
            {
                /** impersonate the VMWare process, so the VMX will be readable from network shares **/
                var inst = ElevationService.RunAsProcess((uint)proc.Id, () =>
                {
                    return ToVmInstance(proc);
                });
                if (inst == null)
                {
                    continue;
                }
                var vmx = inst.VmxPath;
                if (vmInstances.ContainsKey(vmx))
                {
                    throw new InvalidOperationException($"Same VM has more than one instance!? - {vmx}");
                }
                vmInstances.Add(vmx, inst);
            }
            return vmInstances;
        }

        private void ProcessVm(ShutdownState state, VmInstance vm, ShutdownVmOptions opts)
        {
            var vmxFilePath = vm.VmxPath;
            if (ShutdownVmFlow(state, vm, opts))
            {
                _logger.LogInformation($"{vmxFilePath}: Shutdown OK");
            } else
            {
                _logger.LogInformation($"{vmxFilePath}: Shutdown NOK");
            }
        }

        private Task ProcessVmAsync(ShutdownState state, VmInstance vm, ShutdownVmOptions opts)
        {
            return Task.Run(() =>
            {
                ProcessVm(state, vm, opts);
            });
        }

        public void Execute(ShutdownState state)
        {
            var runningVms = GetRunningVms();
            if (runningVms.Count < 1)
            {
                return;
            }
            var vmxExe = runningVms.Values.First()
                .VmProcess.MainModule?.FileName;

            if (string.IsNullOrEmpty(vmxExe))
            {
                return;
            }
            _vmRunPath = GetVmRunPath(vmxExe);
            if (_vmRunPath == null)
            {
                return;
            }

            var shutdownOrder = new SortedDictionary<int, Tuple<VmInstance, ShutdownVmOptions>>(new ShutdownOrderComparer());

            foreach (var vm in runningVms)
            {
                if (!vmxToOpts.TryGetValue(vm.Key, out var opts))
                {
                    opts = _opts.DefaultOptions;
                }
                var index = opts.Order == 0 ? (shutdownOrder.Keys.Max() + 1) : opts.Order;
                if (shutdownOrder.ContainsKey(index))
                {
                    throw new InvalidOperationException($"Invalid shutdown order: duplicate key in VM {opts.VmxPath}");
                }
                shutdownOrder.Add(index, Tuple.Create(vm.Value, opts));
            }

            _logger.LogInformation("Shutdown Order");
            foreach (var vm in shutdownOrder)
            {
                _logger.LogInformation($"- {vm.Value.Item1.VmxPath}");
            }

            if (_opts.Flags.HasFlag(ShutdownVirtualMachinesFlags.Normal))
            {
                var tasks = new List<Task>();
                var vmxPaths = new HashSet<string>();

                _logger.LogInformation("Shutting down Normal VMs in parallel");
                foreach (var vm in shutdownOrder.Where(itm => itm.Key >= 0))
                {
                    var vmxDir = Path.GetDirectoryName(vm.Value.Item1.VmxPath);
                    var vmxDrive = Path.GetPathRoot(vmxDir);
                    if (vmxDir != null && vmxDrive != null)
                    {
                        vmxPaths.Add(vmxDrive);
                        vmxPaths.Add(vmxDir);
                    }
                    tasks.Add(ProcessVmAsync(state, vm.Value.Item1, vm.Value.Item2));
                }
                Task.WaitAll(tasks.ToArray());
                tasks.Clear();

                foreach (var path in vmxPaths)
                {
                    _closeHandleItems.Add(new CloseOpenHandlesItem
                    {
                        IsVolume = false,
                        NameOrPath = path,
                        FlushObjects = true
                    });
                }

                /**
                 * we need to flush and close pending writes to \\truenas\VirtualMachines
                 * before we shutdown the VM providing the SMB server
                 * otherwise a delay write error will occur, causing shutdown to be aborted.
                 * In this case, instead of shutting down, we'll be brought back to the login screen
                 **/
                _logger.LogInformation("Flushing and closing open handles");
                foreach (var path in _closeHandleItems)
                {
                    _logger.LogInformation($" - path: {path.NameOrPath}");
                }
                _closeHandlesFactory.Create(new CloseOpenHandlesParams
                {
                    DryRun = _opts.DryRun,
                    Paths = _closeHandleItems
                }).Execute(state);
            }

            if (_opts.Flags.HasFlag(ShutdownVirtualMachinesFlags.Critical))
            {
                _logger.LogInformation("Shutting down Critical VMs, sequentially");
                foreach (var vm in shutdownOrder.Where(itm => itm.Key < 0))
                {
                    ProcessVm(state, vm.Value.Item1, vm.Value.Item2);
                }
            }
        }
    }
}
