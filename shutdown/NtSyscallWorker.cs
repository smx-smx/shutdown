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
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Smx.SharpIO.Memory;
using Windows.Win32;
using Windows.Win32.System.Threading;
using Windows.Win32.System.Memory;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Win32.Security;
using Windows.Win32.System.Diagnostics.Debug;
using Windows.Win32.Foundation;
using ShutdownLib;
using Windows.Wdk.System.Threading;
using Windows.Wdk.Foundation;
using Windows.Win32.System.Kernel;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;


#if TARGET_64BIT
using nint_t = System.Int64;
#else
using nint_t = System.Int32;
#endif


namespace Shutdown
{
    public class NtSyscallWorker : IDisposable
    {
        private NativeMemoryHandle _shellCode;
        private SafeFileHandle _hThread;
#if ALLOC_STACK
        private NativeMemoryHandle _stack;
#endif

        private nuint _stackBot;
        private nuint _stackTop;
        private SafeFileHandle _hEvent;

        private const int STACK_ARGV_MAX = 10;

        private nuint _currentStackPointer;

        public MemoryAllocator StackAllocator { get; private set; }

        private Dictionary<nuint, nuint> _stackAllocations = new Dictionary<nuint, nuint>();


        //private readonly TypedPointer<SyscallContext> _scCtx;
        private nint _scCtx;

        private nint _scHandlerAddr;


        private nint StackAlloc(nuint size)
        {
            var addr = _currentStackPointer - size;
            if (addr < _stackBot)
            {
                throw new StackOverflowException();
            }
            unsafe
            {
                NativeMemory.Clear(addr.ToPointer(), size);
            }
            _currentStackPointer = _currentStackPointer - size;
            _stackAllocations.Add(addr, size);

            return (nint)addr;
        }

        private void StackFree(nint ptr)
        {
            if (!_stackAllocations.TryGetValue((nuint)ptr, out var size))
            {
                throw new InvalidOperationException();
            }
            _stackAllocations.Remove((nuint)ptr);

            _currentStackPointer += size;
            if (_currentStackPointer >= _stackTop)
            {
                throw new StackOverflowException();
            }
        }

        private static TypedPointer<TEB> GetTeb(SafeFileHandle hThread)
        {
            using var tbi = MemoryHGlobal.Alloc<THREAD_BASIC_INFORMATION>();
            if (!Ntdll.NT_SUCCESS(Ntdll.NtQueryInformationThread(
                hThread.ToHandle(),
                THREADINFOCLASS.ThreadBasicInformation,
                tbi.Address, (uint)tbi.Memory.Size, out var length
            )))
            {
                throw new InvalidOperationException();
            }
            var pTeb = new TypedPointer<TEB>(tbi.Value.TebBaseAddress.Address);
            return pTeb;
        }

        private readonly ILogger<NtSyscallWorker> _logger;

#if ALLOC_STACK
        private void AdjustThreadStack()
        {
            var pTeb = GetTeb(_hThread);
            unsafe
            {
                pTeb.Value.NtTib.StackBase = _stackTop.ToPointer();
                pTeb.Value.NtTib.StackLimit = _stack.Address.ToPointer();
            }
        }
#endif

        private readonly MemoryAllocator _mman;

        private LPTHREAD_START_ROUTINE _threadRoutine;

        private SafeFileHandle CreateThread()
        {
            const uint stackSize = 128 * 1024;

            var hThread = CreateThread(_threadRoutine, stackSize,
                            out _stackTop, out _stackBot,
                            out _scCtx,
                            out _scHandlerAddr);

            var beginOfUserMem = (nuint)nint.Size + 32 + (nuint)(nint.Size * STACK_ARGV_MAX) + (nuint)Unsafe.SizeOf<SyscallFrame>();
            _currentStackPointer = _stackTop - beginOfUserMem;

#if ALLOC_STACK
            _stack = _stackTop - stackSize;
            AdjustThreadStack();
#endif
            return hThread;
        }

        public NtSyscallWorker(ILogger<NtSyscallWorker> logger)
        {
            _logger = logger;
            _mman = new MemoryAllocator(new VirtualMemoryManager
            {
                ProtectionFlags = PAGE_PROTECTION_FLAGS.PAGE_READWRITE
            });

            _shellCode = PrepareCodeBuffer();
            _threadRoutine = Marshal.GetDelegateForFunctionPointer<LPTHREAD_START_ROUTINE>(_shellCode.Address);

#if ALLOC_STACK
            _stack = mman.Alloc(64 * 1024);
            _stackTop = _stack.Address + (nint)_stack.Size;
#endif

            _hEvent = PInvoke.CreateEvent(null, true, false, "ScEvent");
            if (_hEvent.IsInvalid)
            {
                throw new Win32Exception();
            }

            _hThread = CreateThread();

            StackAllocator = new MemoryAllocator(
                new CustomMemoryManager(StackAlloc, StackFree)
            );
        }

        private NativeMemoryHandle PrepareCodeBuffer()
        {
            var alloc = new MemoryAllocator(new VirtualMemoryManager
            {
                ProtectionFlags = PAGE_PROTECTION_FLAGS.PAGE_EXECUTE_READWRITE
            });
            var mem = alloc.Alloc(1024);
            if (mem == null)
            {
                throw new InvalidOperationException("VirtualAlloc failed");
            }

            var shellCode = Convert.FromHexString("4881ecb80000004889214989cfb80e0000004d8b57084831d20f054885c07401ccb8be0100004d8b57104831d20f05488b4424784c8b942480000000488b9424880000004c8b8424900000004c8b8c24980000000f054889442478b80e0000004c8b9424a80000004831d20f054885c07401ccb8be0100004c8b9424b00000004831d20f05eba8909090909090909090");
            shellCode.CopyTo(mem.Span);
            return mem;
        }


        private SafeFileHandle CreateThread(
            LPTHREAD_START_ROUTINE threadRoutine,
            uint stackSize,
            out nuint stackHigh,
            out nuint stackLow,
            //out TypedPointer<SyscallContext> scPtr
            out nint scPtr,
            out nint scHandler
        )
        {

            using var setupArgs = MemoryHGlobal.Alloc<ThreadSetupArgs>();

            uint tid;
            SafeFileHandle hThread;
            unsafe
            {
                hThread = PInvoke.CreateThread(
                    (SECURITY_ATTRIBUTES?)null, stackSize,
                    threadRoutine, setupArgs.Address.ToPointer(),
                    THREAD_CREATION_FLAGS.THREAD_CREATE_SUSPENDED,
                    &tid
                );
            }

            _logger.LogTrace($"tid: {tid:X}");

#if false
            // use VirtualAlloc for aligned allocations and prevent STATUS_DATATYPE_MISALIGNMENT
            // https://stackoverflow.com/questions/56516445/getting-0x3e6-when-calling-getthreadcontext-for-debugged-thread
            using var pCtx = _mman.Alloc<CONTEXT>();
            //pCtx.Value.ContextFlags = CONTEXT_FLAGS.CONTEXT_INTEGER_AMD64 | CONTEXT_FLAGS.CONTEXT_CONTROL_AMD64;
            pCtx.Value.ContextFlags = CONTEXT_FLAGS.CONTEXT_FULL_AMD64;

            unsafe
            {
                if (!PInvoke.GetThreadContext(
                    hThread.ToHandle(),
                    (CONTEXT*)pCtx.Address.ToPointer())
                )
                {
                    throw new Win32Exception();
                }
            }
#endif

            var pTeb = GetTeb(hThread);
            unsafe
            {
                stackHigh = new nuint(pTeb.Value.NtTib.StackBase);
                stackLow = new nuint(pTeb.Value.NtTib.StackLimit);
            }
            _logger.LogTrace($"StackHigh: {stackHigh:X}");
            _logger.LogTrace($"StackLow: {stackHigh:X}");

            setupArgs.Value.hThread = hThread.ToHandle();
            setupArgs.Value.hEvent = _hEvent.ToHandle();

            _logger.LogTrace($"hThread: {hThread.ToHandle():X}");
            _logger.LogTrace($"hEvent: {_hEvent.ToHandle():X}");

            ThreadRun(PInvoke.INFINITE, hThread);
            scPtr = setupArgs.Value.ScCtx;

            Thread.Sleep(1);
            using var pCtx = _mman.Alloc<CONTEXT>();
            pCtx.Value.ContextFlags = CONTEXT_FLAGS.CONTEXT_CONTROL_AMD64;
            GetThreadContext(pCtx.Pointer, hThread);

            scHandler = (nint)pCtx.Value.Rip;

            _logger.LogTrace($"stack context addr: {scPtr:X}");
            return hThread;
        }


        private unsafe int OnException(EXCEPTION_POINTERS* eptr)
        {
            const uint EXCEPTION_BREAKPOINT = 0x80000003;

            Console.WriteLine("exception");
            Console.Out.Flush();
            if (eptr == null || eptr->ExceptionRecord == null
            || eptr->ExceptionRecord->ExceptionCode != new NTSTATUS(unchecked((int)EXCEPTION_BREAKPOINT)))
            {
                return PInvoke.EXCEPTION_CONTINUE_SEARCH;
            }

            *(byte*)eptr->ContextRecord->Rip = 0xC3;
            return PInvoke.EXCEPTION_CONTINUE_EXECUTION;
        }

        public nint RunSyscall(
            NtSyscall syscallNum,
            params nint[] argv
        )
        {
            return RunSyscall(PInvoke.INFINITE, syscallNum, argv);
        }

        private void DumpStack(string filePath)
        {
            var stackSz = _stackTop - _stackBot;
            var stack = new byte[stackSz];
            Marshal.Copy((nint)_stackBot, stack, 0, (int)stackSz);
            File.WriteAllBytes(filePath, stack);
        }

        private void ResetThread()
        {
#if THREAD_RESET_EXPERIMENTAL
            using var pCtx = _mman.Alloc<CONTEXT>();
            pCtx.Value.ContextFlags = CONTEXT_FLAGS.CONTEXT_CONTROL_AMD64 | CONTEXT_FLAGS.CONTEXT_INTEGER_X86;
            GetThreadContext(pCtx.Pointer);
            pCtx.Value.Rax = 0;
            pCtx.Value.Rip = (ulong)_scHandlerAddr;
            SetThreadContext(pCtx.Pointer);
#else
            if (!PInvoke.TerminateThread(_hThread, 0))
            {
                throw new Win32Exception();
            }
            _hThread = CreateThread();
#endif
        }

        private bool ThreadRun(uint timeoutMilliseconds, SafeFileHandle? hThread = null)
        {
            var targetThread = hThread != null ? hThread : _hThread;

            if (!PInvoke.ResetEvent(_hEvent))
            {
                throw new Win32Exception();
            }

            if (PInvoke.ResumeThread(targetThread) == unchecked((uint)-1))
            {
                throw new Win32Exception();
            }

            if (PInvoke.WaitForSingleObject(_hEvent, timeoutMilliseconds) != WAIT_EVENT.WAIT_OBJECT_0)
            {
                _logger.LogTrace("----------- RECOVER");
                if (PInvoke.SuspendThread(_hThread) == unchecked((uint)-1))
                {
                    throw new Win32Exception();
                }
                ResetThread();
                return false;
            }
            return true;
        }

        /// <summary>
        /// Wait for the thread to suspend itself after setup
        /// </summary>
        private void InitialThreadSetup(SafeFileHandle hThread)
        {
            using var pCtx = _mman.Alloc<CONTEXT>();
            pCtx.Value.ContextFlags = CONTEXT_FLAGS.CONTEXT_INTEGER_AMD64 | CONTEXT_FLAGS.CONTEXT_CONTROL_AMD64;

            ThreadRun(PInvoke.INFINITE, hThread);
        }

        private int _iCtx = 0;

        private void DumpState(CONTEXT ctx)
        {
            DumpStack(@$"C:\TEMP\ctx_{_iCtx}_stack.bin");
            File.WriteAllText(@$"C:\TEMP\ctx_{_iCtx}_entry.json",
                JsonSerializer.Serialize(ctx, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    IncludeFields = true
                })
            );
            _iCtx++;
        }

        public nint RunSyscall(
            uint timeoutMilliseconds,
            NtSyscall syscallNum,
            params nint[] argv
        )
        {
            var argc = argv.Length;
            var iArg = 0;

            var argcStack = Math.Max(0, argc - 4);

            //Console.WriteLine($"args: {argc}, stack: {argcStack}");

            /**
              * rsp top <--
              * - syscall ctx (custom)
              * - arguments
              * - [32] shadow space (scratch area)
              * - [ 8] return address
              */
            if (argcStack > STACK_ARGV_MAX)
            {
                throw new InvalidOperationException("too many stack arguments");
            }

            var pScCtx = new TypedPointer<SyscallFrame>(_scCtx);

            var scCtx = pScCtx.Value;
            scCtx.Rax = (ulong)syscallNum;
            if (iArg++ < argc) scCtx.R10 = (ulong)argv[0];
            if (iArg++ < argc) scCtx.Rdx = (ulong)argv[1];
            if (iArg++ < argc) scCtx.R8 = (ulong)argv[2];
            if (iArg++ < argc) scCtx.R9 = (ulong)argv[3];

            for (int i = 0; iArg < argc; i++, iArg++)
            {
                unsafe
                {
                    scCtx.StackArgs[SyscallFrame.MAX_ARGV - 1 - i] = (ulong)argv[iArg];
                }
            }

            scCtx.Setup.hThread = _hThread.ToHandle();
            scCtx.Setup.hEvent = _hEvent.ToHandle();

            pScCtx.Value = scCtx;
            if (ThreadRun(timeoutMilliseconds))
            {
                return (nint)pScCtx.Value.Rax;
            } else
            {
                return (nint)NtStatusCode.STATUS_TIMEOUT;
            }
        }

        private unsafe void GetThreadContext(TypedPointer<CONTEXT> pCtx, SafeFileHandle? hThread = null)
        {
            var targetThread = hThread != null ? hThread : _hThread;
            unsafe
            {
                if (!PInvoke.GetThreadContext(
                    targetThread.ToHandle(),
                    (CONTEXT*)pCtx.Address.ToPointer())
                )
                {
                    throw new Win32Exception();
                }
            }
        }

        private unsafe void SetThreadContext(TypedPointer<CONTEXT> pCtx)
        {
            if (!PInvoke.SetThreadContext(
                    _hThread.ToHandle(),
                    (CONTEXT*)pCtx.Address.ToPointer())
                )
            {
                throw new Win32Exception();
            }
        }

        public void Dispose()
        {
            PInvoke.TerminateThread(_hThread, 0);
            _hThread.Dispose();
            _hEvent.Dispose();
            _shellCode.Dispose();

#if ALLOC_STACK
            _stack.Dispose();
#endif
        }
    }
}
