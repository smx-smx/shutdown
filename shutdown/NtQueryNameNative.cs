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
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using ShutdownLib;
using Smx.SharpIO.Memory;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;

namespace Shutdown
{
    public class NtQueryNameNative : INtQueryNameWorker, IDisposable
    {
        private NtSyscallWorker _worker;
        private SafeHandle _thisProc;

        public NtQueryNameNative()
        {
            _worker = new NtSyscallWorker();
            _thisProc = PInvoke.GetCurrentProcess_SafeHandle();
        }

        private int _callidx = 0;

        private string? GetHandleName(SafeHandle handle, uint dwProcessId)
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
                    hProc, handle, _thisProc,
                    out dupHandle, /*(uint)DesiredAccess.GENERIC_ALL*/ 0, false,
                    DUPLICATE_HANDLE_OPTIONS.DUPLICATE_SAME_ACCESS //0
                ) || dupHandle.IsInvalid)
                {
                    return null;
                }
            }

            //const int timeoutMs = 10;
            const int timeoutMs = 1;

            NtStatusCode status = default;
            using var buf = Helpers.NtCallWithGrowableBuffer((buf) =>
            {
                using var objNameLength = MemoryHGlobal.Alloc<uint>();
                NtStatusCode status = (NtStatusCode)_worker.RunSyscall(
                    timeoutMs,
                    NtSyscall.NtQueryObject,
                    dupHandle.ToHandle(),
                    (nint)OBJECT_INFORMATION_CLASS.ObjectNameInformation,
                    buf.Address,
                    (nint)buf.Size,
                    objNameLength.Pointer.Address
                );
                if (status == NtStatusCode.STATUS_ACCESS_DENIED)
                {
                    throw new InvalidOperationException($"Handle {dupHandle.ToHandle():X} denied");
                }
                return status;
            });

            /*using var objNameLength = _worker.StackAllocator.Alloc<uint>();
            using var buf = _worker.StackAllocator.Alloc(2048);

            var status = (NtStatusCode)_worker.RunSyscall(
                NtSyscall.NtQueryObject,
                dupHandle.ToHandle(),
                (nint)OBJECT_INFORMATION_CLASS.ObjectNameInformation,
                buf.Address,
                (nint)buf.Size,
                objNameLength.Pointer.Address
            );*/

            if (status != NtStatusCode.SUCCESS)
            {
                return null;
            }

            using var nameInfo = new TypedMemoryHandle<OBJECT_NAME_INFORMATION>(buf);
            var nameBuf = nameInfo.Value.Name.Buffer;
            return nameBuf.ToString();

        }

        public void Dispose()
        {
            _worker.Dispose();
            _thisProc.Dispose();
        }

        public string? GetName(Span<SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX> handles, int index, int timeoutMs = 100)
        {
            var itm = handles[index];
            var handle = new SafeNtHandle(itm.HandleValue, false);
            return GetHandleName(handle, (uint)itm.UniqueProcessId);
        }

        public void Start()
        { }
    }
}
