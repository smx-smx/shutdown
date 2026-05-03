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
using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;
using Windows.Win32.Storage.FileSystem;
using static ShutdownLib.Ntdll;
using ShutdownLib;
using Microsoft.Extensions.Logging;
using Smx.Winter;

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

        /// <summary>
        /// 
        /// </summary>
        /// <param name="handleName"></param>
        /// <param name="handle"></param>
        /// <param name="dwProcessId"></param>
        /// <returns></returns>
        /// <remarks>This function MUST be static, since it can't log (the impersonated process might not have access to the log file)</remarks>
        private static (bool, string) FlushHandle(string handleName, HANDLE handle, uint dwProcessId)
        {
            NtStatusCode status;

            if (dwProcessId == (uint)Process.GetCurrentProcess().Id)
            {
                if (!NT_SUCCESS(status = NtFlushBuffersFile(handle, out _)))
                {
                    return (false, $"Sync failed (0x{(uint)status:X8}): {handleName}");
                }
                return (true, string.Empty);
            }

            using var thisProc = PInvoke.GetCurrentProcess_SafeHandle();
            using var hProc = PInvoke.OpenProcess_SafeHandle(
                PROCESS_ACCESS_RIGHTS.PROCESS_DUP_HANDLE,
                false, dwProcessId
            );
            if (hProc.IsInvalid)
            {
                var procName = string.Empty;
                try
                {
                    procName = Process.GetProcessById((int)dwProcessId).ProcessName;
                }
                catch (Exception) { }
                return (false, $"Sync failed: cannot Open process with ID {dwProcessId} ({procName})");
            }

            if (!NT_SUCCESS(status = NtDuplicateObject(
                    hProc.ToHandle(),
                    handle,
                    thisProc.ToHandle(),
                    out var dupHandle,
                    0, 0, (uint)DUPLICATE_HANDLE_OPTIONS.DUPLICATE_SAME_ACCESS
                )))
            {
                return (false, $"Cannot duplicate handle for sync: {handleName}");
            }

            if (!NT_SUCCESS(status = NtFlushBuffersFile(dupHandle, out _)))
            {
                return (false, $"Flush failed (0x{(uint)status:X8}): {handleName}");
            }
            return (true, $"Flushed: {handleName}");
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="handleName"></param>
        /// <param name="handle"></param>
        /// <param name="dwProcessId"></param>
        /// <returns></returns>
        /// <remarks>This function MUST be static, since it can't log (the impersonated process might not have access to the log file)</remarks>
        private (bool, string) CloseHandle(string handleName, HANDLE handle, uint dwProcessId)
        {
            NtStatusCode status;
            using var thisProc = PInvoke.GetCurrentProcess_SafeHandle();
            using var hProc = PInvoke.OpenProcess_SafeHandle(
                PROCESS_ACCESS_RIGHTS.PROCESS_DUP_HANDLE,
                false, dwProcessId
            );
            if (hProc.IsInvalid)
            {
                var procName = string.Empty;
                try
                {
                    procName = Process.GetProcessById((int)dwProcessId).ProcessName;
                }
                catch (Exception) { }
                return (false, $"Sync failed: cannot Open process with ID {dwProcessId} ({procName})");
            }

            if (!NT_SUCCESS(status = NtDuplicateObject(
                    hProc.ToHandle(),
                    handle,
                    thisProc.ToHandle(),
                    out var dupHandle,
                    0, 0, (uint)DUPLICATE_HANDLE_OPTIONS.DUPLICATE_CLOSE_SOURCE
                )))
            {
                return (false, $"Cannot duplicate handle for sync (0x{(uint)status:X8}): {handleName}");
            }

            // convert to owned to auto-close our local copy on return
            using var ownedSyncDup = new SafeNtHandle(dupHandle, true);
            return (true, string.Empty);
        }

        private bool FilterNtPath(string ntPath, string pathPrefix)
        {
            // first, check if it's an absolute path prefix
            if (ntPath.StartsWith(pathPrefix, StringComparison.CurrentCultureIgnoreCase))
            {
                return true;
            }

            // remove any leading backslash from the prefix, since splitting `ntPath` will get rid of them too
            pathPrefix = pathPrefix.TrimStart('\\');

            var parts = ntPath.Split('\\', 4);
            if (parts.Length < 3) return false;
            var marker = parts[0];
            var root = parts[1];
            if (marker != string.Empty) return false;
            if (root != "Device") return false;

            var deviceName = parts[2];
            if (deviceName == "Mup"
                || deviceName.StartsWith("HarddiskVolume"))
            {
                if (parts.Length < 4)
                {
                    // this is a handle to the device itself. don't close it because it might be important
                    // (chkdsk? partition editing tools?)
                    // although we're shutting down.. so all bets are off.
                    return false;
                }
                var devicePath = parts[3];
                if (devicePath.StartsWith(pathPrefix, StringComparison.CurrentCultureIgnoreCase))
                {
                    return true;
                }
            }
            return false;
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

            var procs = Process.GetProcesses().ToDictionary(p => p.Id, p => p);

            var myPid = (uint)Process.GetCurrentProcess().Id;

            for (var i = 0; i < numHandles; i++)
            {
                var h = handles[i];
                if (!IsSupportedHandle(h))
                {
                    continue;
                }

                var safeHandle = new SafeNtHandle(h.HandleValue, false);

                var ntName = _worker.GetName(handles, i);
                _logger.LogTrace($"OBJECT: 0x{handles[i].Object:X}, HANDLE: 0x{handles[i].HandleValue:X}, NAME: \"{ntName ?? string.Empty}\"");
                if (ntName == null) continue;
                if (!FilterNtPath(ntName, pathPrefix)) continue;

                // skip handles owned by ourselves
                if (h.UniqueProcessId == myPid) continue;

                var dryPrefix = _volumes.DryRun ? "[DRY] " : "";

                var procName = string.Empty;
                if (procs.TryGetValue((int)h.UniqueProcessId, out var process))
                {
                    procName = process.ProcessName;
                }
                _logger.LogDebug($"{dryPrefix}{h.UniqueProcessId} ({procName}): {h.ObjectTypeIndex} - {h.HandleValue:X} - {ntName}");

                string? flushMessage = null;
                string? closeMessage = null;

                // impersonate TI. we'll need it in case we want to impersonate svchost
                using (var tiHandle = ElevationService.ImpersonateTrustedInstaller())
                {
                    // we need to impersonate the process to make sure we can access the resource being flushed/closed
                    ElevationService.RunAsProcess((uint)h.UniqueProcessId, () =>
                    {
                        // we can only flush files (and not directories)
                        var flush = flushObjects && h.ObjectTypeIndex == ObjectTypeFile.TypeIndex;

                        if (flush)
                        {
                            // but... Mup handles always report as file, so we must check them separately
                            if (ntName.StartsWith(@"\Device\Mup\"))
                            {
                                var uncPath = @"\" + ntName.Substring(@"\Device\Mup".Length);
                                var attrs = File.GetAttributes(uncPath);
                                if (attrs.HasFlag(FileAttributes.Directory))
                                {
                                    flush = false;
                                }
                            }
                        }

                        var handle = new HANDLE(h.HandleValue);

                        if (flush)
                        {
                            var flushRes = FlushHandle(ntName, handle, (uint)h.UniqueProcessId);
                            flushMessage = flushRes.Item2;
                        }

                        if (_volumes.DryRun)
                        {
                            return;
                        }
                        var closeRes = CloseHandle(ntName, handle, (uint)h.UniqueProcessId);
                        closeMessage = closeRes.Item2;
                    });
                }

                if (!string.IsNullOrEmpty(flushMessage))
                {
                    _logger.LogInformation(flushMessage);
                }
                if (!string.IsNullOrEmpty(closeMessage))
                {
                    _logger.LogInformation(closeMessage);
                }
            }
        }

        public void Execute(ShutdownState state)
        {
            _worker.Start();
            foreach (var path in _volumes.Paths)
            {
                var withFlush = (path.FlushObjects) ? "Flushing+" : string.Empty;

                if (path.IsVolume)
                {
                    _logger.LogInformation($"{withFlush}Closing handles for volume: {path.NameOrPath}");
                    state.SetShutdownStatusMessage($"Closing volume handles: {path.NameOrPath}");
                    var volumePath = Helpers.QueryDosDevice(path.NameOrPath);
                    CloseOpenHandles(volumePath, path.FlushObjects);
                }
                else
                {
                    _logger.LogInformation($"{withFlush}Closing handles for path: {path.NameOrPath}");
                    state.SetShutdownStatusMessage($"Closing path handles: {path.NameOrPath}");
                    CloseOpenHandles(path.NameOrPath, path.FlushObjects);
                }
            }
        }
    }
}
