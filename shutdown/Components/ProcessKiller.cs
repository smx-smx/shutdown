#region License
/*
 * Copyright (C) 2024 Stefano Moioli <smxdev4@gmail.com>
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
#endregion
using Microsoft.Extensions.Logging;
using ShutdownLib;
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

    public ProcessKiller Create(ProcessKillerParams opts)
    {
        var logger = _factory.CreateLogger<ProcessKiller>();
        return new ProcessKiller(opts, logger);
    }
}

public class ProcessKiller : IAction
{
    private readonly ProcessKillerParams _opts;
    private readonly ILogger<ProcessKiller> _logger;
    private readonly string _processSignaler;

    public ProcessKiller(ProcessKillerParams opts, ILogger<ProcessKiller> logger)
    {
        _opts = opts;
        _logger = logger;

        var exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        if (exeDir == null) throw new InvalidOperationException(nameof(exeDir));
        _processSignaler = Path.Combine(exeDir, "ProcessSignaler.exe");
    }

    private static string GetWindowClass(HWND hwnd)
    {
        int res;
        using var natClassName = Helpers.Win32CallWithGrowableBuffer((mem) =>
        {
            res = PInvoke.GetClassName(hwnd, mem.ToPWSTR(), (int)(mem.Size / sizeof(char)));
            if (res > 0)
            {
                return (uint)Win32Error.ERROR_SUCCESS;
            }
            return (uint)res;
        });
        var windowClassName = natClassName.ToPWSTR().ToString();
        return windowClassName;
    }

    private static string GetWindowTitle(HWND hwnd)
    {
        int res;
        using var natTitle = Helpers.Win32CallWithGrowableBuffer((mem) =>
        {
            res = PInvoke.GetWindowText(hwnd, mem.ToPWSTR(), (int)(mem.Size / sizeof(char)));
            if (res > 0)
            {
                return (uint)Win32Error.ERROR_SUCCESS;
            }
            return (uint)res;
        });
        var windowTitle = natTitle.ToPWSTR().ToString();
        return windowTitle;
    }

    private static IList<HWND> FindWindows(HWND hwnd, Func<HWND, bool> evaluateHwnd, bool stopAfterFirst = false)
    {
        var result = new List<HWND>();

        // try with all child windows
        if (!PInvoke.EnumChildWindows(hwnd, (childHwnd, lparam) =>
        {
            // check if this HWND is the wanted one
            if (evaluateHwnd(childHwnd))
            {
                result.Add(childHwnd);
                if (stopAfterFirst)
                {
                    return false;
                }
            }

            // carry on
            return true;
        }, 0))
        {
            if (Marshal.GetLastPInvokeError() != (uint)Win32Error.ERROR_SUCCESS)
            {
                throw new Win32Exception();
            }
        }
        return result;
    }

    private static IList<HWND> FindWindows(Process process, Func<HWND, bool> evaluateHwnd, bool stopAfterFirst = false)
    {
        var result = new List<HWND>();

        if (!PInvoke.EnumWindows((hwnd, lparam) =>
        {
            uint tid = 0;
            unsafe
            {
                tid = PInvoke.GetWindowThreadProcessId(hwnd, null);
            }
            if (tid == 0) return true;

            using var hThread = PInvoke.OpenThread_SafeHandle(THREAD_ACCESS_RIGHTS.THREAD_QUERY_LIMITED_INFORMATION, false, tid);
            if (hThread == null) return true;

            var pid = PInvoke.GetProcessIdOfThread(hThread);
            if (pid == 0) return true;

            // check if the PID matches. otherwise don't bother, and continue search
            if (pid != process.Id) return true;

            // check if this HWND is the wanted one
            if (evaluateHwnd(hwnd))
            {
                result.Add(hwnd);
                if (stopAfterFirst)
                {
                    return false;
                }
            }

            // try with all child windows
            if (!PInvoke.EnumChildWindows(hwnd, (childHwnd, lparam) =>
            {
                // check if this HWND is the wanted one
                if (evaluateHwnd(childHwnd))
                {
                    result.Add(childHwnd);
                    if (stopAfterFirst)
                    {
                        return false;
                    }
                }

                // carry on
                return true;
            }, 0))
            {
                if (Marshal.GetLastPInvokeError() == (uint)Win32Error.ERROR_SUCCESS)
                {
                    // successfully found child, stop search
                    if (stopAfterFirst && result.Count > 0)
                    {
                        return false;
                    }
                    return true;
                } else
                {
                    throw new Win32Exception();
                }
            }

            // carry on
            return true;
        }, 0))
        {
            if (Marshal.GetLastPInvokeError() != (uint)Win32Error.ERROR_SUCCESS)
            {
                throw new Win32Exception();
            }
        }
        return result;
    }

    private static HWND? GetWindowByClass(Process process, string className)
    {
        return FindWindows(process, (hwnd) =>
        {
            var windowClass = GetWindowClass(hwnd);
            return windowClass == className;
        }, true).FirstOrDefault();
    }

    private static HWND? GetWindowByTitle(Process process, string title)
    {
        return FindWindows(process, (hwnd) =>
        {
            var windowTitle = GetWindowTitle(hwnd);
            return windowTitle == title;
        }, true).FirstOrDefault();
    }

    private static void SendAcceleratorKey(HWND hWnd, char key)
    {
        PInvoke.SendMessage(hWnd, PInvoke.WM_SYSKEYDOWN, (uint)VIRTUAL_KEY.VK_MENU, 0);
        PInvoke.SendMessage(hWnd, PInvoke.WM_KEYDOWN, key, 0);
        PInvoke.SendMessage(hWnd, PInvoke.WM_KEYUP, key, 0);
        PInvoke.SendMessage(hWnd, PInvoke.WM_SYSKEYUP, (uint)VIRTUAL_KEY.VK_MENU, 0);
    }

    private bool KillIda(Process process, HWND hWnd, PerProcessSettings settings)
    {
        PInvoke.PostMessage(hWnd, PInvoke.WM_SYSCOMMAND, PInvoke.SC_CLOSE, 0);
        Thread.Sleep(100);

        var boxedSaveHwnd = GetWindowByTitle(process, "Save database");
        if (boxedSaveHwnd == null) return false;

        var saveHwnd = boxedSaveHwnd.Value;

        SendAcceleratorKey(saveHwnd, 'K');
        process.WaitForExit(settings.Timeout);
        return process.HasExited;
    }

    private bool KillGuiSaveDialog(Process process, HWND hWnd, PerProcessSettings settings)
    {
        PInvoke.PostMessage(hWnd, PInvoke.WM_SYSCOMMAND, PInvoke.SC_CLOSE, 0);
        Thread.Sleep(100);

        var boxedSaveHwnd = GetWindowByClass(process, "#32770");
        if (boxedSaveHwnd == null) return false;

        var saveHwnd = boxedSaveHwnd.Value;
        var buttons = FindWindows(saveHwnd, (hwnd) =>
        {
            return GetWindowClass(hwnd) == "Button";
        });

        if (buttons.Count != 3) return false;
        // [Save] - [Dont Save] - [Cancel]
        PInvoke.SendMessage(buttons[1], PInvoke.BM_CLICK, 0, 0);

        process.WaitForExit(settings.Timeout);
        return true;
    }

    private bool KillGuiApp(Process process, HWND hWnd, PerProcessSettings settings)
    {
        if (!TryGetMainModule(process, out var mainModule))
        {
            return false;
        }

        if (settings.Flags.HasFlag(ProcessKillFlags.UI_AttemptSave))
        {

            if (mainModule.ModuleName.Equals("ida64.exe", StringComparison.InvariantCultureIgnoreCase)
            || mainModule.ModuleName.Equals("ida.exe", StringComparison.InvariantCultureIgnoreCase))
            {
                if (_opts.DryRun) return false;
                _logger.LogInformation($"Killing {mainModule.ModuleName} ({process.Id})");
                return KillIda(process, hWnd, settings);
            }

            if (mainModule.ModuleName.Equals("notepad.exe", StringComparison.InvariantCultureIgnoreCase)
            || mainModule.ModuleName.Equals("mspaint.exe", StringComparison.InvariantCultureIgnoreCase)
            || mainModule.ModuleName.Equals("HxD.exe", StringComparison.InvariantCultureIgnoreCase))
            {
                _logger.LogInformation($"Killing {mainModule.ModuleName} ({process.Id})");
                if (_opts.DryRun) return false;
                return KillGuiSaveDialog(process, hWnd, settings);
            }
        }

        return false;
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

    private void KillProcess(Process process)
    {
        //_logger.LogDebug($"Process Id: {process.Id}, {process.ProcessName}");
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

        var processList = new uint[128];
        for (int i = 0; i < 3; i++)
        {
            var nItems = PInvoke.GetConsoleProcessList(processList);
            if (nItems == 0)
            {
                throw new Win32Exception();
            }
            if (nItems <= processList.Length) break;
            processList = new uint[nItems];
        }

        bool hasConsole = processList.AsReadOnly().Contains((uint)process.Id);

        var hWnd = new HWND(process.MainWindowHandle);
        if (!hWnd.IsNull)
        {
            if (!KillGuiApp(process, hWnd, settings))
            {
                _logger.LogError($"Failed to kill GUI app: {process.Id}");
            }
        }

        if (hasConsole)
        {
            if (!KillConsoleApp(process, settings))
            {
                _logger.LogError($"Failed to kill Console app: {process.Id}");
            }
        }

        return;

    }

    public void Execute(ShutdownState actions)
    {
        var thisProc = Process.GetCurrentProcess();
        if (thisProc == null)
        {
            throw new InvalidOperationException("failed to get current process");
        }
        var procs = Process.GetProcesses();
        var tasks = new List<Task>();
        foreach (var proc in procs)
        {
            proc.Equals(proc);
            if (proc.Id == thisProc.Id) continue;
            if (!TryGetMainModule(proc, out var mainMod)) continue;
            if (mainMod.ModuleName.Equals("VsDebugConsole.exe", StringComparison.CurrentCultureIgnoreCase)) continue;
            if (mainMod.ModuleName.Equals("NtQueryNameWorker.exe", StringComparison.CurrentCultureIgnoreCase)) continue;
            if (mainMod.ModuleName.Equals("PRocessSignaler.exe", StringComparison.CurrentCultureIgnoreCase)) continue;
            tasks.Add(Task.Run(() =>
            {
                KillProcess(proc);
            }));
        }
        Task.WaitAll(tasks.ToArray(), TimeSpan.FromMinutes(2));
    }
}
