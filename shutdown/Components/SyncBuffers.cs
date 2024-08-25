#region License
/*
 * Copyright (C) 2024 Stefano Moioli <smxdev4@gmail.com>
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
#endregion
using ShutdownLib;
using Smx.SharpIO.Extensions;
using Smx.SharpIO.Memory;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection.Metadata;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;
using static ShutdownLib.Ntdll;

namespace Shutdown.Components
{
    public class SyncBuffers : IAction
    {
        private SafeNtHandle OpenNtDevices()
        {
            var device = Helpers.NewUnicodeString(@"\Device");
            using var pDevice = MemoryNative.Allocator.StructureToPtr(device);

            var attrs = Helpers.NewObjectAttributes(
                pDevice.Pointer,
                PInvoke.OBJ_CASE_INSENSITIVE,
                HANDLE.Null);

            NtStatusCode status;
            if ((status = NtOpenDirectoryObject(
                out var devhdl,
                ACCESS_MASK.DIRECTORY_QUERY, attrs
            )) != NtStatusCode.SUCCESS)
            {
                throw new InvalidOperationException($"{status:X}");
            }
            return new SafeNtHandle(devhdl, true);
        }

        private IEnumerable<DIRECTORY_BASIC_INFORMATION> EnumerateDeviceObjects()
        {
            using var handle = OpenNtDevices();

            uint ctx = 0;

            NtStatusCode status = NtStatusCode.SUCCESS;
            do
            {
                using var buf = Helpers.NtCallWithGrowableBuffer(
                    (buf) =>
                    {
                        status = NtQueryDirectoryObject(
                            handle.Handle, buf.Address,
                            (uint)buf.Size,
                            false, false, ref ctx, out var written);

                        if (status == NtStatusCode.STATUS_MORE_ENTRIES && written > buf.Size)
                        {
                            return NtStatusCode.STATUS_BUFFER_TOO_SMALL;
                        }
                        return status;
                    });

                var arr = buf.Memory.Cast<byte, DIRECTORY_BASIC_INFORMATION>();

                for (int i = 0; i < arr.Length; i++)
                {
                    var itm = arr.Span[i];
                    if (itm.ObjectName.Length == 0 && itm.ObjectName.MaximumLength == 0)
                    {
                        break;
                    }
                    yield return itm;

                }
            } while (status == NtStatusCode.STATUS_MORE_ENTRIES);
        }

        public static bool SyncObject(SafeNtHandle hRoot, string objectName)
        {
            var natObjectName = Helpers.NewUnicodeString(objectName);
            using var pObjectName = MemoryNative.Allocator.StructureToPtr(natObjectName);
            var attrs = Helpers.NewObjectAttributes(pObjectName.Pointer, PInvoke.OBJ_CASE_INSENSITIVE, hRoot.Handle);

            if (!NT_SUCCESS(NtOpenFile(out var hFile, (uint)DesiredAccess.GENERIC_WRITE, attrs, out var isb,
                (uint)(FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE | FILE_SHARE_MODE.FILE_SHARE_DELETE),
                0)))
            {
                return false;
            }
            using var handle = new SafeNtHandle(hFile, true);

            if (!NT_SUCCESS(NtFlushBuffersFile(handle.Handle, out var ioStatusBlock)))
            {
                return false;
            }
            return true;
        }

        private void SyncDisks(ShutdownState state)
        {
            using var hRoot = OpenNtDevices();
            foreach (var itm in EnumerateDeviceObjects())
            {
                var objName = itm.ObjectName.Buffer.ToString();
                if (!objName.StartsWith("HarddiskVolume", StringComparison.InvariantCultureIgnoreCase))
                {
                    continue;
                }
                Console.WriteLine($"=> Syncing: {objName}");
                state.SetShutdownStatusMessage($"Syncing {objName}");
                var sw = new Stopwatch();
                sw.Start();
                SyncObject(hRoot, objName);
                sw.Stop();
                Console.WriteLine($"Done in {sw.Elapsed.TotalSeconds} seconds");
            }
        }

        public void Execute(ShutdownState state)
        {
            SyncDisks(state);
        }
    }
}
