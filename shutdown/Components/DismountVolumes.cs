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
using Smx.SharpIO.Memory;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Windows.Win32;
using Windows.Win32.Storage.FileSystem;
using Windows.Win32.System.Ioctl;
using static ShutdownLib.Ntdll;

namespace Shutdown.Components
{
    public class DismountVolumesFactory
    {
        private ILoggerFactory _factory;
        public DismountVolumesFactory(ILoggerFactory factory)
        {
            _factory = factory;
        }

        public DismountVolumes Create(HashSet<string> volumes)
        {
            var logger = _factory.CreateLogger<DismountVolumes>();
            return new DismountVolumes(volumes, logger);
        }
    }

    public class DismountVolumes : IAction
    {
        private readonly HashSet<string> _volumes;
        private readonly ILogger<DismountVolumes> _logger;

        public DismountVolumes(
            HashSet<string> volumes,
            ILogger<DismountVolumes> logger
        )
        {
            _volumes = volumes;
            _logger = logger;
        }

        private SafeHandle OpenVolume(string volumeLetter)
        {
            var hVolume = PInvoke.CreateFile(
                @$"\\.\{volumeLetter}",
                (uint)DesiredAccess.GENERIC_READ | (uint)DesiredAccess.GENERIC_WRITE,
                FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE,
                null, FILE_CREATION_DISPOSITION.OPEN_EXISTING,
                0, null
            );
            if (hVolume == null || hVolume.IsInvalid)
            {
                throw new Win32Exception();
            }
            return hVolume;
        }

        private void CloseOpenHandles()
        {
            using var buf = Helpers.NtCallWithGrowableBuffer(
                buf =>
                {
                    return NtQuerySystemInformation(
                        (uint)SYSTEM_INFORMATION_CLASS.SystemHandleInformation,
                        buf.Address,
                        (uint)buf.Size,
                        out var written
                    );
                });
            Console.WriteLine(buf.Size);
        }

        private SafeHandle? GetDiskFromVolume(SafeHandle hVolume)
        {
            var extents = new VOLUME_DISK_EXTENTS();
            uint dwBytesReturned;
            unsafe
            {
                if (!PInvoke.DeviceIoControl(
                    hVolume, PInvoke.IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS,
                    null, 0,
                    &extents, (uint)sizeof(VOLUME_DISK_EXTENTS),
                    &dwBytesReturned, null
                ))
                {
                    throw new Win32Exception();
                }
            }

            if (extents.NumberOfDiskExtents < 1)
            {
                return null;
            }
            var diskNumber = extents.Extents[0].DiskNumber;
            var hDisk = PInvoke.CreateFile(@$"\\?\PhysicalDrive{diskNumber}",
                (uint)(DesiredAccess.GENERIC_READ | DesiredAccess.GENERIC_WRITE),
                FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE,
                null, FILE_CREATION_DISPOSITION.OPEN_EXISTING,
                0, null);

            if (hVolume == null || hVolume.IsInvalid)
            {
                throw new Win32Exception();
            }

            return hDisk;
        }

        private void LockVolume(SafeHandle hVolume)
        {
            uint dwBytesReturned;
            unsafe
            {
                if (!PInvoke.DeviceIoControl(
                    hVolume, PInvoke.FSCTL_LOCK_VOLUME,
                    null, 0,
                    null, 0,
                    &dwBytesReturned, null
                ))
                {
                    throw new Win32Exception();
                }
            }
        }

        private void OfflineVolume(SafeHandle hVolume)
        {
            uint dwBytesReturned;
            unsafe
            {
                if (!PInvoke.DeviceIoControl(
                    hVolume, PInvoke.IOCTL_VOLUME_OFFLINE,
                    null, 0,
                    null, 0,
                    &dwBytesReturned, null
                ))
                {
                    throw new Win32Exception();
                }
            }
        }

        private void DismountVolume(SafeHandle hVolume)
        {
            uint dwBytesReturned;
            unsafe
            {
                if (!PInvoke.DeviceIoControl(
                    hVolume, PInvoke.FSCTL_DISMOUNT_VOLUME,
                    null, 0,
                    null, 0,
                    &dwBytesReturned, null
                ))
                {
                    throw new Win32Exception();
                }
            }
        }

        private void DismountVolume(string volumeLetter)
        {
            using var hVolume = OpenVolume(volumeLetter);

            //LockVolume(hVolume);
            /*using var hDisk = GetDiskFromVolume(hVolume);
            if (hDisk == null)
            {
                throw new InvalidOperationException();
            }*/


            //LockVolume(hVolume);
            DismountVolume(hVolume);
            OfflineVolume(hVolume);
        }

        public void Execute(ShutdownState state)
        {
            foreach (var volume in _volumes)
            {
                _logger.LogInformation($"Dismounting volume: {volume}");
                state.SetShutdownStatusMessage($"Offline volume: {volume}");
                DismountVolume(volume);
            }
        }
    }
}
