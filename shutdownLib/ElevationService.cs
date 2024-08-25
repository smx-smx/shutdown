#region License
/*
 * Copyright (c) 2024 Stefano Moioli
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
#endregion
using Microsoft.Win32.SafeHandles;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.System.Services;
using Windows.Win32.System.Threading;
using Windows.Win32.System.StationsAndDesktops;
using Smx.SharpIO.Memory;
using ShutdownLib;
using System.ComponentModel;
using Windows.Win32.System.RemoteDesktop;
using System.Runtime.CompilerServices;

namespace Smx.Winter;

/**
 * Inspired by https://github.com/nfedera/run-as-trustedinstaller/blob/master/run-as-trustedinstaller/main.cpp
 **/

public class ElevationService
{
    public ElevationService()
    { }

    public static SafeFileHandle DuplicateTokenEx(
        SafeHandle hExistingToken,
        TOKEN_ACCESS_MASK dwDesiredAccess,
        SECURITY_ATTRIBUTES? lpTokenAttributes,
        SECURITY_IMPERSONATION_LEVEL ImpersonationLevel,
        TOKEN_TYPE TokenType
    )
    {
        PInvoke.DuplicateTokenEx(
            hExistingToken,
            dwDesiredAccess,
            lpTokenAttributes,
            ImpersonationLevel,
            TokenType,
            out var newToken
        );
        return newToken;
    }

    public static void RunAsProcess(uint dwProcessId, Action action)
    {
        RunAsProcess(dwProcessId, () =>
        {
            action();
            return 0;
        });
    }

    public static void RunAsWindowStation(uint sid, Action action)
    {
        RunAsWindowStation(sid, () =>
        {
            action();
            return 0;
        });
    }

    public static T RunAsWindowStation<T>(uint sid, Func<T> action)
    {
        using var hToken = Helpers.WTSQueryUserToken(sid);
        return RunAsToken(hToken, action);
    }

    public static void RunAsToken<T>(SafeFileHandle hToken, Action action)
    {
        RunAsToken(hToken, () =>
        {
            action();
            return 0;
        });
    }

    public static T RunAsToken<T>(SafeFileHandle hToken, Func<T> action)
    {
        if (!PInvoke.ImpersonateLoggedOnUser(hToken))
        {
            throw new Win32Exception();
        }

        var res = action();

        if (!PInvoke.RevertToSelf())
        {
            throw new Win32Exception();
        }
        return res;
    }

    public static T RunAsProcess<T>(uint dwProcessId, Func<T> action)
    {
        using var handle = GetProcessHandleForImpersonation(dwProcessId);
        using var token = ImpersonateProcess(handle);

        var res = action();

        if (!PInvoke.RevertToSelf())
        {
            throw new Win32Exception();
        }
        return res;
    }


    public static SafeHandle WtsQueryUserToken(uint sid)
    {
        var userToken = new HANDLE();
        if (!PInvoke.WTSQueryUserToken(sid, ref userToken))
        {
            throw new Win32Exception();
        }
        return new SafeFileHandle(userToken, true);
    }

    public static void ForeachWindowStation(Action<WTS_SESSION_INFOW> cb)
    {
        nint sessInfo;
        uint count;
        unsafe
        {
            if (!PInvoke.WTSEnumerateSessions(HANDLE.Null, 0, 1, out var pSessInfo, out count))
            {
                throw new Win32Exception();
            }
            sessInfo = new nint(pSessInfo);
        }

        using var ownedMem = new NativeMemoryHandle(
            new nint(sessInfo),
            (nuint)Unsafe.SizeOf<WTS_SESSION_INFOW>() * count,
            new WtsMemoryManager(),
            true
        );

        var sessions = new TypedPointer<WTS_SESSION_INFOW>(ownedMem.Address).AsSpan((int)count);
        for (var i = 0; i < count; i++)
        {
            var sess = sessions[i];
            cb(sess);
        }
    }

    public static uint? GetActiveWindowStation()
    {
        nint sessInfo;
        uint count;
        unsafe
        {
            if (!PInvoke.WTSEnumerateSessions(HANDLE.Null, 0, 1, out var pSessInfo, out count))
            {
                throw new Win32Exception();
            }
            sessInfo = new nint(pSessInfo);
        }

        using var ownedMem = new NativeMemoryHandle(
            new nint(sessInfo),
            (nuint)Unsafe.SizeOf<WTS_SESSION_INFOW>() * count,
            new WtsMemoryManager(),
            true
        );

        var sessions = new TypedPointer<WTS_SESSION_INFOW>(ownedMem.Address).AsSpan((int)count);
        for (var i = 0; i < count; i++)
        {
            var sess = sessions[i];
            if (sess.State == WTS_CONNECTSTATE_CLASS.WTSActive)
            {
                return sess.SessionId;
            }
        }
        return null;
    }

    public static SafeFileHandle ImpersonateProcess(SafeFileHandle hProc)
    {
        using var hSystemToken = Helpers.OpenProcessToken(hProc, (TOKEN_ACCESS_MASK)PInvoke.MAXIMUM_ALLOWED);

        var tokenAttributes = new SECURITY_ATTRIBUTES
        {
            bInheritHandle = false,
            nLength = (uint)Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
            lpSecurityDescriptor = null
        };

        var hDupToken = DuplicateTokenEx(
            hSystemToken,
            (TOKEN_ACCESS_MASK)PInvoke.MAXIMUM_ALLOWED,
            tokenAttributes,
            SECURITY_IMPERSONATION_LEVEL.SecurityImpersonation,
            TOKEN_TYPE.TokenImpersonation
        );
        if (hDupToken == null)
        {
            throw new Win32Exception();
        }

        if (!PInvoke.ImpersonateLoggedOnUser(hDupToken))
        {
            throw new Win32Exception();
        }

        return hDupToken;
    }

    private static string? GetCurrentWindowStation()
    {
        using var hWinsta = PInvoke.GetProcessWindowStation_SafeHandle();
        if (hWinsta == null) return null;


        uint size;
        unsafe
        {
            PInvoke.GetUserObjectInformation(
                hWinsta,
                USER_OBJECT_INFORMATION_INDEX.UOI_NAME,
                null, 0, &size);
        }
        using var buf = MemoryHGlobal.Alloc(size);
        unsafe
        {
            PInvoke.GetUserObjectInformation(
                hWinsta,
                USER_OBJECT_INFORMATION_INDEX.UOI_NAME,
                buf.Address.ToPointer(), size, null);
        }

        return Marshal.PtrToStringUni(buf.Address);
    }

    public static void RunAsTrustedInstaller(string commandLine)
    {
        var commandLineBuf = new char[commandLine.Length + 1];
        commandLine.CopyTo(commandLineBuf);

        var commandLineSpan = commandLineBuf.AsSpan();

        using var tiToken = ImpersonateTrustedInstaller();

        var winsta = GetCurrentWindowStation();
        var desktop = $@"{winsta}\Default";
        using var lpDesktop = MemoryHGlobal.Alloc(Encoding.Unicode.GetByteCount(desktop));
        Marshal.Copy(Encoding.Unicode.GetBytes(desktop), 0, lpDesktop.Address, (int)lpDesktop.Size);

        var si = new STARTUPINFOW();
        unsafe
        {
            si.lpDesktop = new PWSTR((char*)lpDesktop.Address.ToPointer());
            PInvoke.CreateProcessWithToken(
                tiToken,
                CREATE_PROCESS_LOGON_FLAGS.LOGON_WITH_PROFILE,
                null,
                ref commandLineSpan,
                PROCESS_CREATION_FLAGS.CREATE_UNICODE_ENVIRONMENT,
                null,
                null,
                si, out var pi
            );
        }
    }


    static uint StartTrustedInstallerService()
    {
        using var hSCManager = PInvoke.OpenSCManager(
            null,
            PInvoke.SERVICES_ACTIVE_DATABASE,
            (uint)GENERIC_ACCESS_RIGHTS.GENERIC_EXECUTE);

        using var hService = PInvoke.OpenService(
            hSCManager,
            "TrustedInstaller",
            (uint)(GENERIC_ACCESS_RIGHTS.GENERIC_READ | GENERIC_ACCESS_RIGHTS.GENERIC_EXECUTE));

        var pid = Helpers.StartService(hService);
        if (pid == null)
        {
            throw new InvalidOperationException("Failed to start TrustedInstaller");
        }
        return pid.Value;
    }

    public static SafeFileHandle GetProcessHandleForImpersonation(uint dwProcessId)
    {
        return PInvoke.OpenProcess_SafeHandle(0
            | PROCESS_ACCESS_RIGHTS.PROCESS_DUP_HANDLE
            | PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_INFORMATION,
            false, dwProcessId);
    }
    public static SafeHandle ImpersonateSystem()
    {
        using var winlogon = Process.GetProcessesByName("winlogon").FirstOrDefault();
        if (winlogon == null)
        {
            throw new InvalidOperationException("winlogon not found");
        }

        using var hProc = GetProcessHandleForImpersonation((uint)winlogon.Id);
        if (hProc == null)
        {
            throw new Win32Exception();
        }

        return ImpersonateProcess(hProc);
    }

    public static SafeHandle ImpersonateTrustedInstaller()
    {
        using var _ = ImpersonateSystem();

        var pid = StartTrustedInstallerService();
        using var hProc = GetProcessHandleForImpersonation(pid);
        return ImpersonateProcess(hProc);
    }

}
