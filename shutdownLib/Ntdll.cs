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
using System.Linq;
using System.Reflection.Metadata;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Windows.Win32.Foundation;
using Windows.Wdk.Foundation;
using Windows.Win32;
using Windows.Win32.System.IO;
using System.Reflection;
using Smx.SharpIO.Memory;
using System.Runtime.CompilerServices;
using Windows.Win32.Security;
using Windows.Wdk.System.Threading;
using Windows.Win32.System.Kernel;
using Windows.Win32.System.Threading;
using Windows.Win32.System.Diagnostics.Debug;


#if TARGET_64BIT
using nint_t = System.Int64;
#else
using nint_t = System.Int32;
#endif

namespace ShutdownLib
{
    public enum DesiredAccess : uint
    {
        GENERIC_READ = 0x80000000,
        GENERIC_WRITE = 0x40000000,
        GENERIC_EXECUTE = 0x20000000,
        GENERIC_ALL = 0x10000000,
    }

    public enum NtStatusCode : uint
    {
        SUCCESS = 0x0,
        STATUS_MORE_ENTRIES = 0x105,
        STATUS_BUFFER_TOO_SMALL = 0xC0000023,
        STATUS_INFO_LENGTH_MISMATCH = 0xC0000004,
        STATUS_ACCESS_VIOLATION = 0xC0000005,
        STATUS_ACCESS_DENIED = 0xC0000022,
        STATUS_OBJECT_PATH_INVALID = 0xC0000039,
        STATUS_NOT_SUPPORTED = 0xC00000BB,
        STATUS_TIMEOUT = 0x00000102
    }

    public enum SYSTEM_INFORMATION_CLASS : uint
    {
        SystemHandleInformation = 16,
        SystemExtendedHandleInformation = 64
    }

    public enum PROCESS_INFORMATION_CLASS : uint
    {
        ProcessBasicInformation = 0,
    }

    public enum OBJECT_INFORMATION_CLASS : uint
    {
        ObjectBasicInformation = 0,
        ObjectNameInformation = 1,
        ObjectTypeInformation = 2,
        ObjectTypesInformation = 3
    }

    public struct OBJECT_NAME_INFORMATION
    {
        public UNICODE_STRING Name;
    }

    public struct CURDIR
    {
        public UNICODE_STRING DosPath;
        public HANDLE Handle;
    }

    public struct RTL_USER_PROCESS_PARAMETERS_INTERNAL
    {
        /** reserved1 (16 bytes) **/
        public uint AllocationSize;
        public uint Size;
        public uint Flags;
        public uint DebugFlags;
        /** reserved2 (80 bytes) **/
        public HANDLE ConsoleHandle;
        public nint ConsoleFlags;
        public HANDLE StandardInput;
        public HANDLE StandardOutput;
        public HANDLE StandardError;
        public CURDIR CurrentDirectory;
        /****/
        public UNICODE_STRING ImagePathName;
        public UNICODE_STRING CommandLine;
        public nint Environment;
        /// and so on, we don't need all the fields
    }

    public static class ACCESS_MASK
    {
        public const uint DIRECTORY_QUERY = 0x0001;
    }


    public struct DIRECTORY_BASIC_INFORMATION
    {
        public UNICODE_STRING ObjectName;
        public UNICODE_STRING ObjectTypeName;
    }

    public struct SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX
    {
        public nint Object;
        public nint UniqueProcessId;
        public nint HandleValue;
        public uint GrantedAccess;
        public ushort CreatorBackTraceIndex;
        public ushort ObjectTypeIndex;
        public uint HandleAttributes;
        public uint Reserved;
    }

    public struct SYSTEM_HANDLE_INFORMATION_EX
    {
        public nint NumberOfHandles;
        public nint Reserved;

        public unsafe nint Address => new nint((byte*)Unsafe.AsPointer(ref this));

        public unsafe Span<SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX> Handles
        {
            get
            {
                var ptr = (byte*)Unsafe.AsPointer(ref this) + sizeof(SYSTEM_HANDLE_INFORMATION_EX);
                return new Span<SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX>(ptr, (int)NumberOfHandles);
            }
        }
    }

    public struct SYSTEM_HANDLE_TABLE_ENTRY_INFO
    {
        public ushort UniqueProcessId;
        public ushort CreatorBackTraceIndex;
        public byte ObjectTypeIndex;
        public byte HandleAttributes;
        public ushort HandleValue;
        public nint Object;
        public uint GrantedAccess;
    }

    public struct SYSTEM_HANDLE_INFORMATION
    {
        public uint NumberOfHandles;

        public unsafe Span<SYSTEM_HANDLE_TABLE_ENTRY_INFO> Handles
        {
            get
            {
                var ptr = (byte*)Unsafe.AsPointer(ref this) + sizeof(SYSTEM_HANDLE_INFORMATION);
                return new Span<SYSTEM_HANDLE_TABLE_ENTRY_INFO>(ptr, (int)NumberOfHandles);
            }
        }
    }

    public class ObjectTypeInformation
    {
        public string? TypeName;
        public uint TotalNumberOfObjects;
        public uint TotalNumberOfHandles;
        public uint TotalPagedPoolUsage;
        public uint TotalNonPagedPoolUsage;
        public uint TotalNamePoolUsage;
        public uint TotalHandleTableUsage;
        public uint HighWaterNumberOfObjects;
        public uint HighWaterNumberOfHandles;
        public uint HighWaterPagedPoolUsage;
        public uint HighWaterNonPagedPoolUsage;
        public uint HighWaterNamePoolUsage;
        public uint HighWaterHandleTableUsage;
        public uint InvalidAttributes;
        public GENERIC_MAPPING GenericMapping;
        public uint ValidAccessMask;
        public BOOLEAN SecurityRequired;
        public BOOLEAN MaintainHandleCount;
        public byte TypeIndex;
        public byte ReservedByte;
        public uint PoolType;
        public uint DefaultPagedPoolCharge;
        public uint DefaultNonPagedPoolCharge;
    }

    public struct OBJECT_TYPE_INFORMATION
    {
        public UNICODE_STRING TypeName;
        public uint TotalNumberOfObjects;
        public uint TotalNumberOfHandles;
        public uint TotalPagedPoolUsage;
        public uint TotalNonPagedPoolUsage;
        public uint TotalNamePoolUsage;
        public uint TotalHandleTableUsage;
        public uint HighWaterNumberOfObjects;
        public uint HighWaterNumberOfHandles;
        public uint HighWaterPagedPoolUsage;
        public uint HighWaterNonPagedPoolUsage;
        public uint HighWaterNamePoolUsage;
        public uint HighWaterHandleTableUsage;
        public uint InvalidAttributes;
        public GENERIC_MAPPING GenericMapping;
        public uint ValidAccessMask;
        public BOOLEAN SecurityRequired;
        public BOOLEAN MaintainHandleCount;
        public byte TypeIndex;
        public byte ReservedByte;
        public uint PoolType;
        public uint DefaultPagedPoolCharge;
        public uint DefaultNonPagedPoolCharge;

        public unsafe int SizeOf
        {
            get
            {
                return (0
                    + sizeof(OBJECT_TYPE_INFORMATION)
                    + (int)Helpers.AlignUp(TypeName.MaximumLength, nint.Size)
                );
            }
        }
    }

    public struct OBJECT_TYPES_INFORMATION
    {
        private nint _NumberOfTypes;
        public uint NumberOfTypes => (uint)_NumberOfTypes;

        public unsafe nint Address => new nint((byte*)Unsafe.AsPointer(ref this));
        public TypedPointer<OBJECT_TYPES_INFORMATION> ToPointer()
        {
            return new TypedPointer<OBJECT_TYPES_INFORMATION>(Address);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="types"></param>
        /// <returns></returns>
        /// <remarks>
        /// iterators work by making a copy of the struct (which then won't point to the original buffer anymore).
        /// We therefore need to use a static method with a pointer argument
        /// </remarks>
        public static IEnumerable<TypedPointer<OBJECT_TYPE_INFORMATION>> GetTypesEnumerator(
            TypedPointer<OBJECT_TYPES_INFORMATION> types
        )
        {
            var sizeofItem = Helpers.AlignUp(Unsafe.SizeOf<OBJECT_TYPE_INFORMATION>(), nint.Size);
            var ptr = types.Address + Unsafe.SizeOf<OBJECT_TYPES_INFORMATION>();
            var numTypes = types.Value.NumberOfTypes;
            for (var i = 0; i < numTypes; i++)
            {
                var itmPtr = new TypedPointer<OBJECT_TYPE_INFORMATION>(ptr);
                yield return itmPtr;
                ptr += itmPtr.Value.SizeOf;
            }
        }

        public IEnumerable<TypedPointer<OBJECT_TYPE_INFORMATION>> Types => GetTypesEnumerator(this.ToPointer());
    }

    public struct CLIENT_ID
    {
        public HANDLE UniqueProcess;
        public HANDLE UniqueThread;
    }

    public struct THREAD_BASIC_INFORMATION
    {
        public NTSTATUS ExitStatus;
        public TypedPointer<TEB> TebBaseAddress;
        public CLIENT_ID ClientId;
        public nint AffinityMask;
        public uint Priority;
        public uint BasePriority;
    }

    public unsafe struct TEB
    {
        public NT_TIB NtTib;
        public nint EnvironmentPointer;
        public CLIENT_ID ClientId;
        public nint ActiveRpcHandle;
        public nint ThreadLocalStoragePointer;
        public TypedPointer<PEB_unmanaged> ProcessEnvironmentBlock;
        public uint LastErrorValue;
        public uint CountOfOwnedCriticalSections;
        public nint CsrClientThread;
        public nint Win32ThreadInfo;
        public fixed uint User32Reserved[26];
        public fixed uint UserReserved[5];
        public nint WOW32Reserved;
        public uint CurrentLocale;
        public uint FpSoftwareStatusRegister;
        /** we can stop here, we don't really need the rest */
    }

    public struct INITIAL_TEB
    {
        public nint OldStackBase;
        public nint OldStackLimit;
        public nint StackBase;
        public nint StackLimit;
        public nint StackAllocationBase;
    }

    public enum NtSyscall : uint
    {
        NtClose = 0xF,
        NtQueryObject = 0x10
    }

    public static class Ntdll
    {
        public static bool NT_SUCCESS(NtStatusCode ntStatus)
        {
            return (uint)ntStatus < 0x80000000;
        }

        [DllImport("ntdll")]
        public static extern NtStatusCode NtOpenDirectoryObject(
            out HANDLE DirectoryHandle,
            uint DesiredAccess,
            OBJECT_ATTRIBUTES ObjectAttributes
        );

        [DllImport("ntdll")]
        public static extern NtStatusCode NtQueryDirectoryObject(
            HANDLE DirectoryHandle,
            nint Buffer,
            uint Length,
            BOOLEAN ReturnSingleEntry,
            BOOLEAN RestartScan,
            ref uint Context,
            out uint ReturnLength
        );

        [DllImport("ntdll")]
        public static extern NtStatusCode NtQueryObject(
            HANDLE Handle,
            OBJECT_INFORMATION_CLASS ObjectInformationClass,
            nint ObjectInformation,
            uint ObjectInformationLength,
            out uint ReturnLength
        );

        [DllImport("ntdll")]
        public static extern NtStatusCode NtOpenFile(
            out HANDLE FileHandle,
            uint DesiredAccess,
            OBJECT_ATTRIBUTES ObjectAttributes,
            out IO_STATUS_BLOCK IoStatusBlock,
            uint ShareAccess,
            uint OpenOptions
        );

        [DllImport("ntdll")]
        public static extern NtStatusCode NtClose(HANDLE Handle);

        [DllImport("ntdll")]
        public static extern NtStatusCode NtFlushBuffersFile(HANDLE FileHandle, out IO_STATUS_BLOCK IoStatusBlock);

        [DllImport("ntdll")]
        public static extern NtStatusCode NtDuplicateObject(
            HANDLE SourceProcessHandle,
            HANDLE SourceHandle,
            HANDLE TargetProcessHandle,
            out HANDLE TargetHandle,
            uint DesiredAccess,
            uint HandleAttributes,
            uint Options
        );

        [DllImport("ntdll")]
        public static extern NtStatusCode NtQuerySystemInformation(
            uint SystemInformationClass,
            nint SystemInformation,
            uint SystemInformationLength,
            out uint ReturnLength
        );

        [DllImport("ntdll")]
        public static extern NtStatusCode NtQueryInformationProcess(
            HANDLE ProcessHandle,
            uint ProcessInformationClass,
            nint ProcessInformation,
            uint ProcessInformationLength,
            out uint ReturnLength
        );

        [DllImport("ntdll")]
        public static extern NtStatusCode NtQueryInformationThread(
            HANDLE ThreadHandle,
            THREADINFOCLASS ThreadInformationClass,
            nint ThreadInformation,
            uint ThreadInformationLength,
            out uint ReturnLength
        );

        [DllImport("ntdll")]
        public static extern NtStatusCode NtCreateThread(
            out HANDLE ThreadHandle,
            THREAD_ACCESS_RIGHTS DesiredAccess,
            TypedPointer<OBJECT_ATTRIBUTES> ObjectAttributes,
            HANDLE ProcessHandle,
            out CLIENT_ID ClientId,
            CONTEXT ThreadContext,
            INITIAL_TEB InitialTeb,
            BOOLEAN CreateSuspended
        );

        [DllImport("ntdll")]
        public static extern void RtlInitializeContext(
            HANDLE ProcessHandle,
            ref CONTEXT ThreadContext,
            nint ThreadStartParam,
            nint ThreadStartAddress,
            nint ThreadStackAddress
        );
    }
}
