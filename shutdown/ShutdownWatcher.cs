#region License
/*
 * Copyright (C) 2024 Stefano Moioli <smxdev4@gmail.com>
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
#endregion
using Smx.SharpIO.Memory;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Win32.UI.WindowsAndMessaging;
using Windows.Win32;
using Windows.Win32.Foundation;
using System.Threading;
using Microsoft.Extensions.Logging;
using Windows.Win32.System.RemoteDesktop;
using Shutdown;
using Microsoft.Win32.SafeHandles;
using Smx.Winter;
using Windows.Win32.System.StationsAndDesktops;
using ShutdownLib;
using System.Diagnostics;
using System.IO.Pipes;

namespace Shutdown;

public enum ShutdownMode
{
    /// <summary>
    /// Pre-Shutdown actions
    /// </summary>
    PreShutdown,
    /// <summary>
    /// Shutdown actions
    /// </summary>
    Shutdown
}

public class ShutdownWatcherFactory
{
    private readonly ShutDownActions _actions;
    private ILoggerFactory _logFactory;

    public ShutdownWatcherFactory(
        ShutDownActions actions,
        ILoggerFactory factory
    )
    {
        _actions = actions;
        _logFactory = factory;
    }

    public ShutdownWatcher CreateWatcher(ShutdownMode mode)
    {
        return new ShutdownWatcher(
            _actions,
            _logFactory.CreateLogger<ShutdownWatcher>(),
            mode);
    }
}

public class ShutdownWatcher
{
    private readonly ShutDownActions _actions;
    private readonly ILogger<ShutdownWatcher> _logger;
    private FreeLibrarySafeHandle _hInstance;
    private readonly ShutdownMode _mode;

    public ShutdownWatcher(
        ShutDownActions actions,
        ILogger<ShutdownWatcher> logger,
        ShutdownMode mode
    )
    {
        _mode = mode;
        _actions = actions;
        _logger = logger;
        _hInstance = PInvoke.GetModuleHandle("");
    }


    private LRESULT WndProc(HWND hWnd, uint uMsg, WPARAM wParam, LPARAM lParam)
    {
        switch (uMsg)
        {
            case PInvoke.WM_CREATE:
                // register block reason for later
                PInvoke.ShutdownBlockReasonCreate(hWnd, "ShutdownTool running");
                return PInvoke.DefWindowProc(hWnd, uMsg, wParam, lParam);
            case PInvoke.WM_CLOSE:
                PInvoke.DestroyWindow(hWnd);
                return new LRESULT(0);
            case PInvoke.WM_DESTROY:
                PInvoke.PostQuitMessage(0);
                return new LRESULT(0);
            case PInvoke.WM_QUERYENDSESSION:
                if (_mode == ShutdownMode.PreShutdown)
                {
                    Task.Run(() =>
                    {
                        _actions.Run(_mode, _hWnd);
                        _logger.LogInformation("Pre-Shutdown completed");
                        // we can't clear the shutdown block reason from here, since we're in another thread
                        // let's be brutal, for now
                        Environment.Exit(0);
                    });
                    // Doesn't block shutdown, but stop signal propagation (undocumented?)
                    // This way Notepad, MSPaint, etc. won't know we're shutting down and we can end them properly
                    return new LRESULT(0);
                }
                // Don't block shutdown. we'll postpone it in WM_ENDSESSION
                // (actually we can't block shutdown anymore since Windows Vista)
                return new LRESULT(1);
            case PInvoke.WM_ENDSESSION:
                if (_mode == ShutdownMode.PreShutdown)
                {
                    return new LRESULT(1);
                }
                if (wParam == 0)
                {
                    // not processed
                    return new LRESULT(1);
                }

                _actions.Run(_mode, _hWnd);
                PInvoke.ShutdownBlockReasonDestroy(hWnd);
                return new LRESULT(0);
#if false
            case PInvoke.WM_POWERBROADCAST:
                switch (wParam.Value)
                {
                    case PInvoke.PBT_APMSUSPEND:
                    case PInvoke.PBT_APMRESUMEAUTOMATIC:
                    default:
                        break;
                }
                return new LRESULT(0);
                break;
#endif
            default:
                return PInvoke.DefWindowProc(hWnd, uMsg, wParam, lParam);
        }
    }

    private const string CLASS_NAME = "ShutdownTool";

    private HWND CreateDummyWindow()
    {
        using var className = MemoryHGlobal.Allocator.AllocString(CLASS_NAME, Encoding.Unicode);

        using var currentInstance = PInvoke.GetModuleHandle("");
        if (PInvoke.RegisterClassEx(new WNDCLASSEXW
        {
            cbSize = (uint)Unsafe.SizeOf<WNDCLASSEXW>(),
            hInstance = new HINSTANCE(_hInstance.DangerousGetHandle()),
            lpszClassName = className.ToPWSTR(),
            lpfnWndProc = WndProc
        }) == 0)
        {
            throw new Win32Exception();
        }

        HWND hWnd;
        unsafe
        {
            hWnd = PInvoke.CreateWindowEx(
                WINDOW_EX_STYLE.WS_EX_OVERLAPPEDWINDOW,
                CLASS_NAME, "",
                WINDOW_STYLE.WS_OVERLAPPEDWINDOW,
                0, 0, 0, 0,
                HWND.Null, null, currentInstance,
                nint.Zero.ToPointer()
            );
        }
        if (hWnd.IsNull)
        {
            throw new Win32Exception();
        }

#if false
        var hPowerNotify = PInvoke.RegisterSuspendResumeNotification(hWnd, REGISTER_NOTIFICATION_FLAGS.DEVICE_NOTIFY_WINDOW_HANDLE);
        if (hPowerNotify.IsNull)
        {
            throw new Win32Exception();
        }
#endif
        return hWnd;
    }

    private void MessageLoop()
    {
        _logger.LogInformation("Starting Shutdown Watcher");
        while (PInvoke.GetMessage(out var msg, HWND.Null, 0, 0))
        {
            PInvoke.DispatchMessage(msg);
        }

        if (!PInvoke.UnregisterClass(CLASS_NAME, _hInstance))
        {
            _logger.LogError($"UnregisterClass failed: {Marshal.GetLastPInvokeError():X}");
        }
    }

    private BOOL ConsoleCtrlHandler(uint fdwCtrlType)
    {
        switch (fdwCtrlType)
        {
            case PInvoke.CTRL_LOGOFF_EVENT:
            case PInvoke.CTRL_SHUTDOWN_EVENT:
                return true;
            case PInvoke.CTRL_C_EVENT:
            case PInvoke.CTRL_BREAK_EVENT:
            default:
                return false;
        }
    }

    private void InstallHandler()
    {
        PInvoke.SetConsoleCtrlHandler(ConsoleCtrlHandler, true);
    }

    private const int VMWARE_SHUTDOWN_PRIO = 0x101;

    private void SetShutdownPriority()
    {
        if (_mode == ShutdownMode.Shutdown)
        {
            _logger.LogInformation("Setting shutdown parameter for normal shutdown");
            /**
             * The priority must be the lowest possible so that if there is unsaved work and the user cancels the shutdown,
             * the iSCSI drive won't be unmounted.
             * Still, we must run BEFORE VMWare does, or it will try to suspend running VMs in arbitrary order,
             * thus breaking the ones that depend on TrueNAS (they run off a network share exposed by the TrueNAS VM itself)
             * 
             * The VMWare priority is hardcoded in vmware-vmx's WinMain method
             **/
            if (!PInvoke.SetProcessShutdownParameters(VMWARE_SHUTDOWN_PRIO + 1, 0))
            {
                throw new Win32Exception();
            }
        } else
        {
            _logger.LogInformation("Setting shutdown parameter for pre-shutdown");
            /**
             * Set maximum shutdown priority
             **/
            if (!PInvoke.SetProcessShutdownParameters(0x3FF, 0))
            {
                throw new Win32Exception();
            }
        }
    }

    private bool IsWindowStationVisible(SafeHandle hWinSta)
    {
        USEROBJECTFLAGS flags = default;
        unsafe
        {
            uint neededBytes;
            if (!PInvoke.GetUserObjectInformation(
                hWinSta,
                USER_OBJECT_INFORMATION_INDEX.UOI_FLAGS,
                &flags, (uint)sizeof(USEROBJECTFLAGS), &neededBytes
            ))
            {
                throw new Win32Exception();
            }
        }

        var isVisible = (flags.dwFlags & PInvoke.WSF_VISIBLE) != 0;
        return isVisible;
    }

    private void ImpersonateWindowStation()
    {
        var sid = ElevationService.GetActiveWindowStation();
        if (sid == null)
        {
            _logger.LogError("No Active Window Station found");
            return;
        }
        using var currentStation = PInvoke.GetProcessWindowStation_SafeHandle();

        using var buf = Helpers.Win32CallWithGrowableBuffer(buf =>
        {
            uint neededBytes;
            BOOL res;
            unsafe
            {
                res = PInvoke.GetUserObjectInformation(
                    currentStation,
                    USER_OBJECT_INFORMATION_INDEX.UOI_NAME,
                    buf.Address.ToPointer(), (uint)buf.Size, &neededBytes);
            }
            if (!res && neededBytes > buf.Size)
            {
                buf.Realloc(neededBytes);
            }
            return (uint)Marshal.GetLastPInvokeError();
        });

        USEROBJECTFLAGS flags = default;
        unsafe
        {
            uint neededBytes;
            if (!PInvoke.GetUserObjectInformation(
                currentStation,
                USER_OBJECT_INFORMATION_INDEX.UOI_FLAGS,
                &flags, (uint)sizeof(USEROBJECTFLAGS), &neededBytes
            ))
            {
                throw new Win32Exception();
            }
        }

        _logger.LogDebug($"Current WinSTA: {buf.ToPWSTR()}");

        var isVisible = (flags.dwFlags & PInvoke.WSF_VISIBLE) != 0;
        _logger.LogDebug($"Winsta flags: 0x{flags.dwFlags:X8}");

        if (isVisible)
        {
            //Helpers.AllocConsole();
        }

    }

    private HWND _hWnd = HWND.Null;

    public void Stop()
    {
        if (!PInvoke.PostMessage(_hWnd, PInvoke.WM_QUIT, 0, 0))
        {
            _logger.LogError($"PostMessage() failed {Marshal.GetLastPInvokeError():X}");
        }
    }

    public void Execute()
    {
        SetShutdownPriority();
        ImpersonateWindowStation();
        //InstallHandler();
        _hWnd = CreateDummyWindow();
        MessageLoop();
    }
}
