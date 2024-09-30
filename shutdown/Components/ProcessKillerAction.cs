#region License
/*
 * Copyright (C) 2024 Stefano Moioli <smxdev4@gmail.com>
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
#endregion
using Microsoft.Extensions.Logging;
using Shutdown.Components.ProcessKiller;
using Shutdown.Components.ProcessKiller.Modules;
using ShutdownLib;
using Smx.SharpIO.Memory;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using static ShutdownLib.Ntdll;

namespace Shutdown.Components;

[Flags]
public enum ProcessKillFlags
{
    Console_SendCtrlC = 1 << 0,
    UI_AttemptSave = 1 << 1,
    Default = Console_SendCtrlC | UI_AttemptSave
}

public class PerProcessSettings
{
    public required string MainModuleName { get; set; }
    public TimeSpan Timeout { get; set; }
    public ProcessKillFlags Flags { get; set; } = ProcessKillFlags.Default;
}

public class ProcessKillerParams
{
    public bool DryRun { get; set; } = false;
    public Dictionary<string, PerProcessSettings> ProcessSettings = new Dictionary<string, PerProcessSettings>(StringComparer.InvariantCultureIgnoreCase);
}

public class ProcessKillerFactory
{
    private ILoggerFactory _factory;
    public ProcessKillerFactory(ILoggerFactory factory)
    {
        _factory = factory;
    }

    public ProcessKillerAction Create(ProcessKillerParams opts)
    {
        var logger = _factory.CreateLogger<ProcessKillerAction>();
        return new ProcessKillerAction(opts, logger, _factory);
    }
}

public class ProcessKillerAction : IAction
{
    private readonly ProcessKillerParams _opts;
    private readonly ILogger<ProcessKillerAction> _logger;
    private readonly ILoggerFactory _factory;
    private readonly string _processSignaler;

    public ProcessKillerAction(
        ProcessKillerParams opts,
        ILogger<ProcessKillerAction> logger,
        ILoggerFactory factory)
    {
        _opts = opts;
        _logger = logger;
        _factory = factory;

        var exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        if (exeDir == null) throw new InvalidOperationException(nameof(exeDir));
        _processSignaler = Path.Combine(exeDir, "ProcessSignaler.exe");
    }

    private bool KillGuiApp(Process process, HWND hWnd, PerProcessSettings settings)
    {

        if (!TryGetMainModule(process, out var mainModule))
        {
            return false;
        }

        var modules = new IProcessKiller[]{
            new IdaKiller(_factory.CreateLogger<IdaKiller>()),
            new GenericSaveDialogKiller(_factory.CreateLogger<GenericSaveDialogKiller>()),
            new ArsenalImageMounter(_factory.CreateLogger<ArsenalImageMounter>())
        };

        var logPrefix = _opts.DryRun ? "[DRY] " : "";

        foreach (var mod in modules)
        {
            if (mod.IsProcessSupported(process, hWnd, settings))
            {
                _logger.LogInformation(logPrefix + $"Killing {mainModule.ModuleName} ({process.Id})");
                if (_opts.DryRun) continue;
                mod.KillProcess(process, hWnd, settings);
                break;
            }
        }

        // incompatible app, ignore
        return true;
    }

    private static bool TryGetMainModule(Process process, [MaybeNullWhen(false)] out ProcessModule mainModule)
    {
        try
        {
            mainModule = process.MainModule;
            return mainModule != null;
        } catch (Exception)
        {
            mainModule = null;
            return false;
        }
    }

    private bool KillConsoleApp(Process process, PerProcessSettings settings)
    {
        if (!_opts.DryRun && settings.Flags.HasFlag(ProcessKillFlags.Console_SendCtrlC))
        {
            _logger.LogInformation($"Killing console app: {process.Id}");
            var pi = new ProcessStartInfo
            {
                FileName = _processSignaler
            };
            pi.ArgumentList.Add(process.Id.ToString());
            var signaler = Process.Start(pi);
            if (signaler == null)
            {
                throw new InvalidOperationException($"failed to start process signaler: {_processSignaler}");
            }
            signaler.WaitForExit(TimeSpan.FromSeconds(10));
            process.WaitForExit(settings.Timeout);
        }
        return process.HasExited;
    }

    private PerProcessSettings? GetPerProcessSettings(Process process)
    {
        if (!TryGetMainModule(process, out var mainModule)) return null;
        if (!_opts.ProcessSettings.TryGetValue(mainModule.ModuleName, out var settings)) return null;
        return settings;
    }

    private static bool TryGetProcessPebData(uint dwProcessId,
        [MaybeNullWhen(false)]
        out PEB_unmanaged? outPebData,
        [MaybeNullWhen(false)]
        out RTL_USER_PROCESS_PARAMETERS? outProcessParameters
    )
    {
        outPebData = null;
        outProcessParameters = null;

        using var hProc = PInvoke.OpenProcess_SafeHandle(0
            | PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_INFORMATION
            | PROCESS_ACCESS_RIGHTS.PROCESS_VM_READ,
            false, dwProcessId
        );
        if (hProc.IsInvalid) return false;

        var info = MemoryHGlobal.Alloc<PROCESS_BASIC_INFORMATION>();
        var status = NtQueryInformationProcess(
            hProc.ToHandle(),
            (uint)PROCESS_INFORMATION_CLASS.ProcessBasicInformation,
            info.Address, (uint)Unsafe.SizeOf<PROCESS_BASIC_INFORMATION>(),
            out var returnLength
        );

        if (!NT_SUCCESS(status))
        {
            return false;
        }

        unsafe
        {
            var pPeb = info.Value.PebBaseAddress;
            if (pPeb == null) return false;


            /** phase 1 **/

            using var pebBuf = MemoryHGlobal.Alloc<PEB_unmanaged>();

            nuint nRead = 0;
            if (!PInvoke.ReadProcessMemory(hProc, pPeb,
                pebBuf.Address.ToPointer(), pebBuf.Memory.Size,
                &nRead
            ) || nRead != pebBuf.Memory.Size)
            {
                throw new Win32Exception();
            }
            outPebData = pebBuf.Value;

            nRead = 0;

            using var pebProcParams = MemoryHGlobal.Alloc<RTL_USER_PROCESS_PARAMETERS>();

            var peb = pebBuf.Value;
            if (peb.ProcessParameters == null) return false;

            if (!PInvoke.ReadProcessMemory(hProc, peb.ProcessParameters,
                pebProcParams.Address.ToPointer(), pebProcParams.Memory.Size,
                &nRead
            ) || nRead != pebProcParams.Memory.Size)
            {
                throw new Win32Exception();
            }
            outProcessParameters = pebProcParams.Value;
            nRead = 0;
        }
        return true;
    }

    private bool ProcessHasConsole(Process process)
    {
        if (!TryGetProcessPebData((uint)process.Id, out var boxPebData, out var boxProcessParams)
            || boxPebData == null || boxProcessParams == null
            || !boxPebData.HasValue || !boxProcessParams.HasValue)
        {
            _logger.LogError($"Failed to read PEB for pid {process.Id}");
            return false;
        }
        var processParams = boxProcessParams.Value;

        RTL_USER_PROCESS_PARAMETERS_INTERNAL procParams;
        unsafe
        {
            procParams = new TypedPointer<RTL_USER_PROCESS_PARAMETERS_INTERNAL>(
                new nint(Unsafe.AsPointer(ref processParams))
            ).Value;
        }
        var consoleHandle = procParams.ConsoleHandle.Value;
        return consoleHandle != 0 && consoleHandle != -1;
    }

    private void KillProcess(Process process)
    {
        //_logger.LogDebug($"--> Process Id: {process.Id}, {process.ProcessName}");
        if (!TryGetMainModule(process, out var mainModule))
        {
            //_logger.LogDebug($"Skipping {process.Id} (cannot query modules)");
            return;
        }
        //_logger.LogInformation($"Main Module: {mainModule.FileName}");

        var settings = GetPerProcessSettings(process) ?? new PerProcessSettings
        {
            MainModuleName = mainModule.ModuleName,
            Timeout = TimeSpan.FromSeconds(90)
        };

        var hWnd = new HWND(process.MainWindowHandle);
        if (!hWnd.IsNull)
        {
            if (!KillGuiApp(process, hWnd, settings))
            {
                _logger.LogError($"Failed to kill GUI app: {process.Id}");
            }
        }

#if false
        bool hasConsole = ProcessHasConsole(process);
        if (hasConsole)
        {
            if (!KillConsoleApp(process, settings))
            {
                _logger.LogError($"Failed to kill Console app: {process.Id}");
            }
        }
#endif

        //_logger.LogDebug($"<-- Process Id: {process.Id}, {process.ProcessName}");
        return;

    }

    public void Execute(ShutdownState actions)
    {
        var thisProc = Process.GetCurrentProcess();
        if (thisProc == null)
        {
            throw new InvalidOperationException("failed to get current process");
        }
        if (!TryGetMainModule(thisProc, out var thisMainMod))
        {
            throw new InvalidOperationException("failed to get current main module");
        }

        var procs = Process.GetProcesses();
        var tasks = new List<Task>();

        // $TODO: move to JSON configuration
        var exclusions = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase)
        {
            "VsDebugConsole.exe",
            "NtQueryNameWorker.exe",
            "ProcessSignaler.exe",
            thisMainMod.ModuleName
        };

        foreach (var proc in procs)
        {
            if (proc.Id == thisProc.Id) continue;
            if (!TryGetMainModule(proc, out var mainMod)) continue;

            if (exclusions.Contains(mainMod.ModuleName))
            {
                _logger.LogInformation($"Skipping excluded process: {mainMod.ModuleName} ({proc.Id})");
                continue;
            }
#if DEBUG
            KillProcess(proc);
#else
            tasks.Add(Task.Run(() =>
            {
                KillProcess(proc);
            }));
#endif
        }
        _logger.LogInformation("Waiting for killer tasks to complete");
        var timer = new Stopwatch();
        timer.Start();
        Task.WaitAll(tasks.ToArray(), TimeSpan.FromMinutes(2));
        timer.Stop();
        _logger.LogInformation($"All tasks completed. Elapsed: {timer.Elapsed.TotalSeconds}");
    }
}
