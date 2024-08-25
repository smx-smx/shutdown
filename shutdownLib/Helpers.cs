#region License
/*
 * Copyright (C) 2024 Stefano Moioli <smxdev4@gmail.com>
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
#endregion
using Microsoft.Win32.SafeHandles;
using Smx.SharpIO.Extensions;
using Smx.SharpIO.Memory;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data.Common;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Windows.Wdk.Foundation;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.Storage.FileSystem;
using Windows.Win32.System.Services;
using Windows.Win32.System.Threading;
using static ShutdownLib.Ntdll;

namespace ShutdownLib
{
    public class Helpers
    {
        private const int SIZE_THRESHOLD = 256 * 1024 * 1024;

        public static nint AlignUp(nint addr, nint size)
        {
            return (addr + (size - 1)) & ~(size - 1);
        }

        public static void LaunchDebugger()
        {
            if (!Debugger.IsAttached)
            {
                if (!Debugger.Launch())
                {
                    return;
                }
                while (!Debugger.IsAttached)
                {
                    Thread.Sleep(200);
                }
            }
            Debugger.Break();
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="sid"></param>
        /// <exception cref="Win32Exception"></exception>
        /// <remarks>Only callable from Windows Services</remarks>
        public static SafeFileHandle WTSQueryUserToken(uint sid)
        {
            var hToken = new HANDLE();
            if (!PInvoke.WTSQueryUserToken(sid, ref hToken))
            {
                throw new Win32Exception();
            }
            return new SafeFileHandle(hToken, true);
        }

        public static void AllocConsole()
        {
            if (!PInvoke.AllocConsole())
            {
                throw new Win32Exception();
            }
            var conIn = PInvoke.CreateFile(
                "CONIN$",
                (uint)DesiredAccess.GENERIC_READ,
                FILE_SHARE_MODE.FILE_SHARE_WRITE,
                null,
                FILE_CREATION_DISPOSITION.OPEN_EXISTING,
                FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_NORMAL,
                null
            );
            if (conIn.IsInvalid)
            {
                throw new Win32Exception();
            }
            var conOut = PInvoke.CreateFile(
                "CONOUT$",
                (uint)DesiredAccess.GENERIC_WRITE,
                FILE_SHARE_MODE.FILE_SHARE_READ,
                null,
                FILE_CREATION_DISPOSITION.OPEN_EXISTING,
                FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_NORMAL,
                null
            );
            if (conOut.IsInvalid)
            {
                throw new Win32Exception();
            }


            var streamIn = new FileStream(conIn, FileAccess.Read);
            var streamOut = new FileStream(conOut, FileAccess.Write);

            var readerIn = new StreamReader(streamIn);
            var writerOut = new StreamWriter(streamOut);
            writerOut.AutoFlush = true;

            Console.SetIn(readerIn);
            Console.SetOut(writerOut);
            Console.SetError(writerOut);
        }

        public static uint? StartService(CloseServiceHandleSafeHandle hService, IList<string>? args = null)
        {
            var status = new SERVICE_STATUS_PROCESS();
            var pBuf = MemoryMarshal.Cast<SERVICE_STATUS_PROCESS, byte>(
                MemoryMarshal.CreateSpan(ref status, 1));

            NativeMemoryHandle? memArgs = null;
            try
            {
                if (args != null)
                {
                    var ptrSize = args.Count * nint.Size;
                    var argsSize = args.Sum(a =>
                    {
                        var nBytes = sizeof(char) * (a.Length + 1);
                        return AlignUp(nBytes, nint.Size);
                    });
                    memArgs = MemoryHGlobal.Alloc((nuint)(ptrSize + argsSize));
                    var pointers = memArgs.Span.Cast<nint>();
                    var strings = memArgs.Span.Slice(ptrSize);
                    var p = 0;
                    for (var i = 0; i < args.Count; i++)
                    {
                        var arg = args[i];
                        var argSz = sizeof(char) * (arg.Length + 1);
                        var argMem = strings.Slice(p, argSz);
                        Encoding.Unicode.GetBytes(arg).CopyTo(argMem);
                        pointers[i] = memArgs.Address + ptrSize + p;
                        p += argSz;
                    }
                }

                while (PInvoke.QueryServiceStatusEx(
                    hService,
                    SC_STATUS_TYPE.SC_STATUS_PROCESS_INFO,
                    pBuf, out var bytesNeeded))
                {
                    switch (status.dwCurrentState)
                    {
                        case SERVICE_STATUS_CURRENT_STATE.SERVICE_STOPPED:
                            if (args != null && memArgs != null)
                            {
                                ReadOnlySpan<PCWSTR> pArgs = memArgs.AsSpan<PCWSTR>(0, args.Count);
                                PInvoke.StartService(hService, pArgs);
                            }
                            else
                            {
                                PInvoke.StartService(hService, null);
                            }
                            break;
                        case SERVICE_STATUS_CURRENT_STATE.SERVICE_START_PENDING:
                        case SERVICE_STATUS_CURRENT_STATE.SERVICE_STOP_PENDING:
                            Thread.Sleep((int)status.dwWaitHint);
                            break;
                        case SERVICE_STATUS_CURRENT_STATE.SERVICE_RUNNING:
                            return status.dwProcessId;
                    }
                }
                return null;
            }
            finally
            {
                memArgs?.Dispose();
            }
        }

        public static NativeMemoryHandle Win32CallWithGrowableBuffer(Func<NativeMemoryHandle, uint> call)
        {

            var handle = MemoryHGlobal.Alloc(1024);

            while (true)
            {
                var sizeBefore = handle.Size;
                var status = call(handle);
                var sizeAfter = handle.Size;
                if (sizeAfter != sizeBefore) continue;
                switch ((Win32Error)status)
                {
                    case Win32Error.ERROR_INSUFFICIENT_BUFFER:
                        var newSize = handle.Size * 2;
                        if (newSize >= SIZE_THRESHOLD) throw new InvalidOperationException();
                        handle.Realloc(newSize);
                        continue;
                }
                if (status == (uint)Win32Error.ERROR_SUCCESS) break;
                throw new InvalidOperationException($"{status:X}");
            }

            return handle;
        }

        public static NativeMemoryHandle NtCallWithGrowableBuffer(Func<NativeMemoryHandle, NtStatusCode> call)
        {
            var handle = MemoryHGlobal.Alloc(1024);

            while (true)
            {
                var sizeBefore = handle.Size;
                var status = call(handle);
                var sizeAfter = handle.Size;
                if (sizeAfter != sizeBefore) continue;
                switch (status)
                {
                    case NtStatusCode.STATUS_BUFFER_TOO_SMALL:
                    case NtStatusCode.STATUS_INFO_LENGTH_MISMATCH:
                        var newSize = handle.Size * 2;
                        if (newSize >= SIZE_THRESHOLD) throw new InvalidOperationException();
                        handle.Realloc(newSize);
                        continue;
                }
                if (Ntdll.NT_SUCCESS(status)) break;
                throw new InvalidOperationException($"{status:X}");
            }

            return handle;
        }

        public static UNICODE_STRING NewUnicodeString(string str)
        {
            var uniStr = new UNICODE_STRING();
            /**
             * $FIXME: this works as long as str is not garbage collected. 
             * it should be pinned or copied to IDisposable memory instead
             **/
            PInvoke.RtlInitUnicodeString(ref uniStr, str);
            return uniStr;
        }

        public static OBJECT_ATTRIBUTES NewObjectAttributes(
            TypedPointer<UNICODE_STRING> objectName,
            uint attributes,
            HANDLE rootDirectory,
            TypedPointer<SECURITY_DESCRIPTOR> secDescr = default)
        {
            OBJECT_ATTRIBUTES attrs;
            unsafe
            {
                attrs = new OBJECT_ATTRIBUTES
                {
                    Length = (uint)Unsafe.SizeOf<OBJECT_ATTRIBUTES>(),
                    RootDirectory = rootDirectory,
                    Attributes = attributes,
                    ObjectName = objectName.ToPointer(),
                    SecurityDescriptor = secDescr.Address.ToPointer(),
                    SecurityQualityOfService = null
                };
            }
            return attrs;
        }

        public static string? GetHandleName(SafeHandle thisProc, SafeHandle handle, uint dwProcessId)
        {
            SafeFileHandle dupHandle;
            if (dwProcessId == (uint)Process.GetCurrentProcess().Id)
            {
                dupHandle = new SafeFileHandle(handle.DangerousGetHandle(), false);
            }
            else
            {
                using var hProc = PInvoke.OpenProcess_SafeHandle(0
                    | PROCESS_ACCESS_RIGHTS.PROCESS_DUP_HANDLE
                    | PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_INFORMATION,
                    false, dwProcessId);

                if (hProc.IsInvalid)
                {
                    return null;
                }

                if (!PInvoke.DuplicateHandle(
                    hProc, handle, thisProc,
                    out dupHandle, 0, false,
                    DUPLICATE_HANDLE_OPTIONS.DUPLICATE_SAME_ACCESS
                ) || dupHandle.IsInvalid)
                {
                    return null;
                }
            }

            NtStatusCode status = default;
            using var buf = Helpers.NtCallWithGrowableBuffer((buf) =>
            {
                status = NtQueryObject(
                    new HANDLE(dupHandle.DangerousGetHandle()),
                    OBJECT_INFORMATION_CLASS.ObjectNameInformation,
                    buf.Address, (uint)buf.Size, out var objNameLength);
                return status;
            });

            if (status != NtStatusCode.SUCCESS)
            {
                return null;
            }

            using var nameInfo = new TypedMemoryHandle<OBJECT_NAME_INFORMATION>(buf);
            var nameBuf = nameInfo.Value.Name.Buffer;
            return nameBuf.ToString();

        }

        public static SafeFileHandle OpenProcessToken(SafeFileHandle hProc, TOKEN_ACCESS_MASK flags)
        {
            PInvoke.OpenProcessToken(hProc, flags, out var hToken);
            return hToken;
        }

        private static TOKEN_PRIVILEGES MakeTokenPrivileges(string privilegeName, TOKEN_PRIVILEGES_ATTRIBUTES attrs)
        {
            if (!PInvoke.LookupPrivilegeValue(null, privilegeName, out var luid))
            {
                throw new Win32Exception();
            }

            var tp = new TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Privileges = new VariableLengthInlineArray<LUID_AND_ATTRIBUTES>
                {
                    e0 = new LUID_AND_ATTRIBUTES
                    {
                        Luid = luid,
                        Attributes = attrs
                    }
                }
            };
            return tp;
        }

        public static void EnablePrivilege(string privilegeName)
        {
            using var hProc = PInvoke.GetCurrentProcess_SafeHandle();
            if (hProc == null)
            {
                throw new Win32Exception();
            }
            using var hToken = OpenProcessToken(hProc, TOKEN_ACCESS_MASK.TOKEN_QUERY | TOKEN_ACCESS_MASK.TOKEN_ADJUST_PRIVILEGES);
            if (hToken == null)
            {
                throw new Win32Exception();
            }

            var tp = MakeTokenPrivileges(privilegeName, TOKEN_PRIVILEGES_ATTRIBUTES.SE_PRIVILEGE_ENABLED);
            unsafe
            {
                if (!PInvoke.AdjustTokenPrivileges(
                    hToken, false,
                    &tp, (uint)Marshal.SizeOf(tp),
                    null, null))
                {
                    throw new Win32Exception();
                }
            }
        }
    }
}
