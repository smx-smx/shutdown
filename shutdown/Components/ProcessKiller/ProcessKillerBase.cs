#region License
/*
 * Copyright (C) 2024 Stefano Moioli <smxdev4@gmail.com>
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
#endregion
using ShutdownLib;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Shutdown.Components.ProcessKiller
{
    public class ProcessKillerBase
    {
        protected static bool TryGetMainModule(Process process, [MaybeNullWhen(false)] out ProcessModule mainModule)
        {
            try
            {
                mainModule = process.MainModule;
                return mainModule != null;
            }
            catch (Exception)
            {
                mainModule = null;
                return false;
            }
        }

        protected static string GetWindowClass(HWND hwnd)
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

        protected static bool GetCloseDialog(
            HWND hWnd,
            Func<HWND?> getDlgHandle,
            TimeSpan delay,
            [MaybeNullWhen(false)] out HWND dlgHandle)
        {
            dlgHandle = default;
            for (int i = 0; i < 2; i++)
            {
                var handle = getDlgHandle();
                if (handle.HasValue && !handle.Value.IsNull)
                {
                    dlgHandle = handle.Value;
                    return true;
                }
                else
                {
                    PInvoke.PostMessage(hWnd, PInvoke.WM_SYSCOMMAND, PInvoke.SC_CLOSE, 0);
                    Thread.Sleep(delay);
                }
            }

            return false;
        }

        protected static IList<HWND> FindWindows(HWND hwnd, Func<HWND, bool> evaluateHwnd, bool stopAfterFirst = false)
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
                    }
                    else
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

        protected static HWND? GetWindowByClass(Process process, string className)
        {
            return FindWindows(process, (hwnd) =>
            {
                var windowClass = GetWindowClass(hwnd);
                return windowClass == className;
            }, true).FirstOrDefault();
        }

        protected static HWND? GetWindowByTitle(Process process, string title)
        {
            return FindWindows(process, (hwnd) =>
            {
                var windowTitle = GetWindowTitle(hwnd);
                return windowTitle == title;
            }, true).FirstOrDefault();
        }

        protected static void SendAcceleratorKey(HWND hWnd, char key)
        {
            PInvoke.SendMessage(hWnd, PInvoke.WM_SYSKEYDOWN, (uint)VIRTUAL_KEY.VK_MENU, 0);
            PInvoke.SendMessage(hWnd, PInvoke.WM_KEYDOWN, key, 0);
            PInvoke.SendMessage(hWnd, PInvoke.WM_KEYUP, key, 0);
            PInvoke.SendMessage(hWnd, PInvoke.WM_SYSKEYUP, (uint)VIRTUAL_KEY.VK_MENU, 0);
        }
    }
}
