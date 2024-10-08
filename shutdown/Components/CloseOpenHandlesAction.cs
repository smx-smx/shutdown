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
using System.Diagnostics;
using System.Linq;
using System.Net.Http.Headers;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.System.Threading;
using Windows.Win32.Storage.FileSystem;
using static ShutdownLib.Ntdll;
using System.IO.Pipes;
using ShutdownLib;
using Microsoft.Extensions.Logging;

namespace Shutdown.Components
{
    public class CloseOpenHandlesItem
    {
        public required bool IsVolume { get; set; }
        public required string NameOrPath { get; set; }
        public bool FlushObjects { get; set; } = false;
    }

    public class CloseOpenHandlesParams
    {
        public bool DryRun { get; set; } = false;
        public ICollection<CloseOpenHandlesItem> Paths { get; set; } = new List<CloseOpenHandlesItem>();
    }

    public class CloseOpenHandlesFactory
    {
        private readonly ILoggerFactory _loggerFactory;
        private readonly INtQueryNameWorkerProvider _workerProvider;

        public CloseOpenHandlesFactory(
            ILoggerFactory loggerFactory,
            INtQueryNameWorkerProvider workerProvider
        )
        {
            _loggerFactory = loggerFactory;
            _workerProvider = workerProvider;
        }

        public CloseOpenHandlesAction Create(CloseOpenHandlesParams opts)
        {
            return new CloseOpenHandlesAction(opts, _loggerFactory, _workerProvider);
        }
    }

    public class CloseOpenHandlesAction : IAction
    {
        private static readonly Dictionary<string, ObjectTypeInformation> ObjectTypes;
        private static readonly ObjectTypeInformation ObjectTypeFile;
        private static readonly ObjectTypeInformation ObjectTypeDirectory;

        static CloseOpenHandlesAction()
        {
            ObjectTypes = GetObjectTypesByName();
            if (!ObjectTypes.TryGetValue("File", out var objectTypeFile)
            || !ObjectTypes.TryGetValue("Directory", out var objectTypeDirectory))
            {
                throw new InvalidOperationException();
            }

            ObjectTypeFile = objectTypeFile;
            ObjectTypeDirectory = objectTypeDirectory;
        }

        private static bool IsSupportedHandle(SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX h)
        {
            if (h.ObjectTypeIndex == ObjectTypeFile.TypeIndex) return true;
            if (h.ObjectTypeIndex == ObjectTypeDirectory.TypeIndex) return true;
            return false;
        }

        private static IEnumerable<ObjectTypeInformation> GetObjectTypes()
        {
            NtStatusCode status = default;
            using var buf = Helpers.NtCallWithGrowableBuffer(
                buf =>
                {
                    status = NtQueryObject(
                        HANDLE.Null,
                        OBJECT_INFORMATION_CLASS.ObjectTypesInformation,
                        buf.Address,
                        (uint)buf.Size,
                        out var returnLength
                    );
                    return status;
                });

            if (status != NtStatusCode.SUCCESS)
            {
                throw new Win32Exception();
            }

            var ptr = new TypedMemoryHandle<OBJECT_TYPES_INFORMATION>(buf);
            var numTypes = ptr.Value.NumberOfTypes;
            var types = ptr.Value.Types;
            if (ptr.Value.Address != buf.Address) throw new InvalidOperationException();
            foreach (var typePtr in types)
            {
                var val = typePtr.Value;
                yield return new ObjectTypeInformation
                {
                    DefaultNonPagedPoolCharge = val.DefaultNonPagedPoolCharge,
                    DefaultPagedPoolCharge = val.DefaultPagedPoolCharge,
                    GenericMapping = val.GenericMapping,
                    HighWaterHandleTableUsage = val.HighWaterHandleTableUsage,
                    HighWaterNamePoolUsage = val.HighWaterNamePoolUsage,
                    HighWaterNonPagedPoolUsage = val.HighWaterNonPagedPoolUsage,
                    HighWaterNumberOfHandles = val.HighWaterNumberOfHandles,
                    HighWaterNumberOfObjects = val.HighWaterNumberOfObjects,
                    HighWaterPagedPoolUsage = val.HighWaterPagedPoolUsage,
                    InvalidAttributes = val.InvalidAttributes,
                    MaintainHandleCount = val.MaintainHandleCount,
                    PoolType = val.PoolType,
                    ReservedByte = val.ReservedByte,
                    SecurityRequired = val.SecurityRequired,
                    TotalHandleTableUsage = val.TotalHandleTableUsage,
                    TotalNamePoolUsage = val.TotalNamePoolUsage,
                    TotalNonPagedPoolUsage = val.TotalNonPagedPoolUsage,
                    TotalNumberOfHandles = val.TotalNumberOfHandles,
                    TotalNumberOfObjects = val.TotalNumberOfObjects,
                    TotalPagedPoolUsage = val.TotalPagedPoolUsage,
                    TypeIndex = val.TypeIndex,
                    ValidAccessMask = val.ValidAccessMask,
                    TypeName = val.TypeName.Buffer.ToString()
                };
            }
        }

        private static Dictionary<byte, ObjectTypeInformation> GetObjectTypesByIndex()
        {
            return GetObjectTypes().ToDictionary(t => t.TypeIndex, t => t);
        }

        private static Dictionary<string, ObjectTypeInformation> GetObjectTypesByName()
        {
            return GetObjectTypes()
                .Where(t => t.TypeName != null)
                .ToDictionary(
                    t => t.TypeName!,
                    t => t
                );
        }


        private readonly CloseOpenHandlesParams _volumes;
        private readonly ILogger<CloseOpenHandlesAction> _logger;
        private readonly INtQueryNameWorker _worker;

        public CloseOpenHandlesAction(
            CloseOpenHandlesParams opts,
            ILoggerFactory loggerFactory,
            INtQueryNameWorkerProvider nameWorkerProvider
        )
        {
            _volumes = opts;
            _logger = loggerFactory.CreateLogger<CloseOpenHandlesAction>();
            _worker = nameWorkerProvider.GetWorker();
        }

        private unsafe string? FileHandleGetName(SafeHandle handle)
        {
            FILE_NAME_INFO finfo = new FILE_NAME_INFO();
            if (!PInvoke.GetFileInformationByHandleEx(handle, FILE_INFO_BY_HANDLE_CLASS.FileNameInfo, &finfo, (uint)sizeof(FILE_NAME_INFO)))
            {
                return null;
            }
            return new string(finfo.FileName.AsSpan((int)finfo.FileNameLength).ToArray());
        }

        private bool CloseHandle(string handleName, HANDLE handle, uint dwProcessId, bool flush)
        {
            HANDLE syncHandle;
            HANDLE dupHandle;

            if (dwProcessId == (uint)Process.GetCurrentProcess().Id)
            {
                dupHandle = handle;
                if (!NT_SUCCESS(NtFlushBuffersFile(handle, out _)))
                {
                    _logger.LogError($"Sync failed: {handleName}");
                }
            } else
            {
                using var thisProc = PInvoke.GetCurrentProcess_SafeHandle();
                using var hProc = PInvoke.OpenProcess_SafeHandle(
                    PROCESS_ACCESS_RIGHTS.PROCESS_DUP_HANDLE,
                    false, dwProcessId
                );
                if (hProc.IsInvalid)
                {
                    return false;
                }
                if (flush)
                {
                    do
                    {
                        if (!NT_SUCCESS(NtDuplicateObject(
                            hProc.ToHandle(),
                            handle,
                            thisProc.ToHandle(),
                            out syncHandle,
                            0, 0, (uint)DUPLICATE_HANDLE_OPTIONS.DUPLICATE_SAME_ACCESS
                        )))
                        {
                            _logger.LogError($"Cannot duplicate handle for sync: {handleName}");
                        }

                        if (!NT_SUCCESS(NtFlushBuffersFile(syncHandle, out _)))
                        {
                            _logger.LogError($"Flush failed: {handleName}");
                        }

                        // create an owned handle to auto-close it
                        using var ownedSyncDup = new SafeNtHandle(syncHandle, true);
                    } while (false);
                }

                if (!NT_SUCCESS(NtDuplicateObject(
                    hProc.ToHandle(),
                    handle,
                    thisProc.ToHandle(),
                    out dupHandle,
                    0, 0, (uint)DUPLICATE_HANDLE_OPTIONS.DUPLICATE_CLOSE_SOURCE
                )))
                {
                    return false;
                }
            }

            // create an owned handle to auto-close it
            using var ownedDup = new SafeNtHandle(dupHandle, true);
            return true;
        }

        private void CloseOpenHandles(string pathPrefix, bool flushObjects)
        {
            using var thisProc = PInvoke.GetCurrentProcess_SafeHandle();
            NtStatusCode status;
            using var buf = Helpers.NtCallWithGrowableBuffer(
                buf =>
                {
                    status = NtQuerySystemInformation(
                        (uint)SYSTEM_INFORMATION_CLASS.SystemExtendedHandleInformation,
                        buf.Address,
                        (uint)buf.Size,
                        out var returnLength
                    );
                    if (status == NtStatusCode.STATUS_INFO_LENGTH_MISMATCH)
                    {
                        buf.Realloc(returnLength + (128 * 1024));
                    }
                    return status;
                });


            var ptr = new TypedMemoryHandle<SYSTEM_HANDLE_INFORMATION_EX>(buf);
            if (ptr.Value.Address != buf.Address) throw new InvalidOperationException("buffer pointer address mismatch");
            var numHandles = ptr.Value.NumberOfHandles;
            var handles = ptr.Value.Handles;


            for (var i = 0; i < numHandles; i++)
            {
                var h = handles[i];
                if (!IsSupportedHandle(h))
                {
                    continue;
                }

                var safeHandle = new SafeNtHandle(h.HandleValue, false);

                _logger.LogTrace($"OBJECT: 0x{handles[i].Object:X}, HANDLE: 0x{handles[i].HandleValue:X}");
                var ntName = _worker.GetName(handles, i);
                if (ntName == null) continue;

                bool found = ntName.StartsWith(pathPrefix, StringComparison.InvariantCultureIgnoreCase);
                if (!found)
                {
                    const string NT_DEVICE_MUP = @"\Device\Mup\";

                    if (pathPrefix.StartsWith(@"\\")
                        && ntName.StartsWith(NT_DEVICE_MUP, StringComparison.CurrentCultureIgnoreCase
                    ))
                    {
                        found = ntName.Substring(NT_DEVICE_MUP.Length)
                            .StartsWith(pathPrefix.Substring(2));
                    }
                }

                if (!found)
                {
                    continue;
                }

                var dryPrefix = _volumes.DryRun ? "[DRY] " : "";
                _logger.LogDebug($"{dryPrefix}{h.UniqueProcessId}: {h.ObjectTypeIndex} - {h.HandleValue:X} - {ntName}");



                if (_volumes.DryRun)
                {
                    continue;
                }

                var flush = flushObjects && h.ObjectTypeIndex == ObjectTypeFile.TypeIndex;
                CloseHandle(
                    ntName,
                    new HANDLE(h.HandleValue),
                    (uint)h.UniqueProcessId,
                    flush);
            }
        }

        public void Execute(ShutdownState state)
        {
            _worker.Start();
            foreach (var vol in _volumes.Paths)
            {
                if (vol.IsVolume)
                {
                    _logger.LogInformation($"Closing handles for volume: {vol.NameOrPath}");
                    state.SetShutdownStatusMessage($"Closing volume handles: {vol.NameOrPath}");
                    var bufName = Helpers.Win32CallWithGrowableBuffer((buf) =>
                    {
                        var maxChars = (uint)(buf.Size / sizeof(char));
                        var numChars = PInvoke.QueryDosDevice(vol.NameOrPath, buf.ToPWSTR(), maxChars);
                        return (uint)Marshal.GetLastPInvokeError();
                    });
                    var volumePath = bufName.ToPWSTR().ToString();
                    CloseOpenHandles(volumePath, vol.FlushObjects);
                } else
                {
                    _logger.LogInformation($"Closing handles for path: {vol.NameOrPath}");
                    state.SetShutdownStatusMessage($"Closing path handles: {vol.NameOrPath}");
                    CloseOpenHandles(vol.NameOrPath, vol.FlushObjects);
                }
            }
        }
    }
}
