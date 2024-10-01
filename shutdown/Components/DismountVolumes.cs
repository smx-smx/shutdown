#region License
/*
 * Copyright (C) 2024 Stefano Moioli <smxdev4@gmail.com>
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
#endregion
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using ShutdownLib;
using Smx.SharpIO.Memory;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
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

        public DismountVolumes Create(DismountVolumesParams opts)
        {
            var logger = _factory.CreateLogger<DismountVolumes>();
            return new DismountVolumes(opts, logger);
        }
    }

    public class DismountVolumeItem
    {
        public required string VolumeLetter { get; set; }
        public required bool Dismount { get; set; }
        public required bool OfflineDisks { get; set; }
    }

    public class DismountVolumesParams
    {
        public required ICollection<DismountVolumeItem> Volumes { get; set; }
    }

    public class DismountVolumes : IAction
    {
        private readonly HashSet<string> _offlinedVolumes;
        private readonly HashSet<uint> _offlinedDisks;
        private readonly Dictionary<string, HashSet<string>> _volumeToDisk;
        private readonly ILogger<DismountVolumes> _logger;
        private readonly DismountVolumesParams _opts;

        public DismountVolumes(
            DismountVolumesParams opts,
            ILogger<DismountVolumes> logger
        )
        {
            _opts = opts;
            _logger = logger;
            _offlinedVolumes = new HashSet<string>();
            _offlinedDisks = new HashSet<uint>();
            _volumeToDisk = new Dictionary<string, HashSet<string>>();
        }

        private static SafeFileHandle OpenDisk(uint diskNumber)
        {
            var diskPath = @$"\\?\PhysicalDrive{diskNumber}";
            var hDisk = PInvoke.CreateFile(diskPath,
                (uint)(DesiredAccess.GENERIC_READ | DesiredAccess.GENERIC_WRITE),
                FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE,
                null, FILE_CREATION_DISPOSITION.OPEN_EXISTING,
                0, null
            );
            if (hDisk == null || hDisk.IsInvalid)
            {
                throw new Win32Exception();
            }
            return hDisk;
        }

        private static SafeFileHandle OpenVolume(string volumeLetter)
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

        private static HashSet<uint> GetDiskNumbersFromVolume(string volumeLetter)
        {
            using var hVolume = OpenVolume(volumeLetter);
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
                return new HashSet<uint>();
            }

            return extents.Extents
                .AsSpan((int)extents.NumberOfDiskExtents).ToArray()
                .Select(ext => ext.DiskNumber)
                .ToHashSet();
        }

        private ICollection<SafeFileHandle> GetDisksFromVolume(SafeHandle hVolume)
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
                return Array.Empty<SafeFileHandle>();
            }

            var diskHandles = extents.Extents
                .AsSpan((int)extents.NumberOfDiskExtents).ToArray()
                .Select(ext => OpenDisk(ext.DiskNumber));

            return diskHandles.ToArray();
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

        private void OfflineDisk(SafeFileHandle hDisk)
        {
            var attrs = new SET_DISK_ATTRIBUTES
            {
                Version = (uint)Unsafe.SizeOf<SET_DISK_ATTRIBUTES>(),
                Attributes = PInvoke.DISK_ATTRIBUTE_OFFLINE,
                AttributesMask = PInvoke.DISK_ATTRIBUTE_OFFLINE,
            };
            unsafe
            {
                uint numBytesReturned = 0;
                if (!PInvoke.DeviceIoControl(
                    hDisk,
                    PInvoke.IOCTL_DISK_SET_DISK_ATTRIBUTES,
                    &attrs,
                    attrs.Version, // aka sizeof
                    null, 0,
                    &numBytesReturned, null
                ))
                {
                    throw new Win32Exception();
                }
                // Invalidates the cached partition table and re-enumerates the device.
                if (!PInvoke.DeviceIoControl(
                    hDisk, PInvoke.IOCTL_DISK_UPDATE_PROPERTIES,
                    null, 0, null, 0, &numBytesReturned, null
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

        private void DismountVolume(ShutdownState state, DismountVolumeItem volume)
        {
            if (_offlinedVolumes.Contains(volume.VolumeLetter))
            {
                _logger.LogWarning($"Volume {volume.VolumeLetter} already offline, skipping");
                return;
            }

            bool doOffline = true;
            var diskNumbers = GetDiskNumbersFromVolume(volume.VolumeLetter);
            foreach (var disk in diskNumbers)
            {
                if (_offlinedDisks.Contains(disk))
                {
                    /** 
                      * one of the disks is already offline
                      * so this volume must be on a disk that was offlined before
                      */
                    _logger.LogWarning($"Skipping volume offline, since disk {disk} is already offline");
                    doOffline = false;
                    break;
                }
            }

            using var hVolume = OpenVolume(volume.VolumeLetter);

            //LockVolume(hVolume);
            /*using var hDisk = GetDiskFromVolume(hVolume);
            if (hDisk == null)
            {
                throw new InvalidOperationException();
            }*/


            var disksToOffline = volume.OfflineDisks
                ? diskNumbers
                : new HashSet<uint>();

            //LockVolume(hVolume);
            if (doOffline)
            {
                state.SetShutdownStatusMessage($"Offline volume: {volume.VolumeLetter}");
                DismountVolume(hVolume);
                OfflineVolume(hVolume);
            }
            _offlinedVolumes.Add(volume.VolumeLetter);

            foreach (var diskNo in disksToOffline)
            {
                if (_offlinedDisks.Contains(diskNo))
                {
                    _logger.LogWarning($"Disk {diskNo} already offline, skipping");
                }
                using var hDisk = OpenDisk(diskNo);
                state.SetShutdownStatusMessage($"Offline disk: {diskNo}");
                OfflineDisk(hDisk);
                _offlinedDisks.Add(diskNo);
            }
        }

        public void Execute(ShutdownState state)
        {
            foreach (var volume in _opts.Volumes)
            {
                _logger.LogInformation($"Dismounting volume: {volume.VolumeLetter}");
                DismountVolume(state, volume);
            }
        }
    }
}
