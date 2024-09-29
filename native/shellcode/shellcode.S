/**
  * Windows NT syscall worker thread
  * Copyright (C) Stefano Moioli <smxdev4@gmail.com>
  */
#define NT_SET_EVENT 0xE
#define NT_SUSPEND_THREAD 0x1BE

#define SIZEOF_VOID_P 8

#define SC_ARGS_STACK 10
#define SC_ARGS_REGS 5

#define SIZEOF_SCSTK (SIZEOF_VOID_P * SC_ARGS_STACK)
#define SIZEOF_SCREGS (SIZEOF_VOID_P * SC_ARGS_REGS)

/**
  * - syscallContext ptr
  * - hEvent
  * - hThread
  * - register arguments
  * - stack arguments
  * - shadow space
  * - saved rip
  * <-- rsp
  **/
#define STACK_RESERVE (0 \
	+ SIZEOF_VOID_P \
	+ 32 \
	+ SIZEOF_SCSTK \
	+ SIZEOF_SCREGS \
	+ (SIZEOF_VOID_P * 3))

#define OFFSETOF_SCSTK (SIZEOF_VOID_P + 32)
#define SC_STK(i) (OFFSETOF_SCSTK + ((i) * SIZEOF_VOID_P))
#define SC_REG(i) (SC_STK(SC_ARGS_STACK) + ((i) * SIZEOF_VOID_P))

#define SC_SCPTR (SC_REG(SC_ARGS_REGS + 0))
#define SC_HEVENT (SC_REG(SC_ARGS_REGS + 1))
#define SC_HTHREAD (SC_REG(SC_ARGS_REGS + 2))

.intel_syntax noprefix
.globl sc_main
sc_main:
	sub rsp, STACK_RESERVE	
	// tell the other thread where to write syscall parameters
	mov qword ptr [rcx+0], rsp

	// save rcx since it will be overwritten by syscall
	mov r15, rcx 
	
	// NtSetEvent(sc.hEvent, 0)
	mov eax, NT_SET_EVENT
	mov r10, qword ptr [r15+8]
	xor rdx, rdx
	syscall
	test rax, rax
	je 1f
	int3

1:
	// NtSuspendThread(sc.hThread, 0)
	mov eax, NT_SUSPEND_THREAD
	mov r10, qword ptr [r15+16]
	xor rdx, rdx
	syscall
	/** thread is now waiting for the first syscall **/
	
2:
	//jmp .
	/**
	  * on resume, the following happens:
	  * - we complete the NtSuspendThread (with untouched registers)
	  * - we fill syscall arguments from the stack frame (setup by the "debugger" thread)
	  * - stack arguments are already in their correct place
	  * after executing the syscall:
	  * - we save the result back to the frame
	  * - we suspend again
	  */
	mov rax, qword ptr [rsp+SC_REG(0)]
	mov r10, qword ptr [rsp+SC_REG(1)]
	mov rdx, qword ptr [rsp+SC_REG(2)]
	mov r8, qword ptr [rsp+SC_REG(3)]
	mov r9, qword ptr [rsp+SC_REG(4)]
	syscall
	mov qword ptr [rsp+SC_REG(0)], rax

	// NtSetEvent(sc.hEvent, 0)
	mov eax, NT_SET_EVENT
	mov r10, qword ptr [rsp+SC_HEVENT]
	xor rdx, rdx
	syscall
	test rax, rax
	je 3f
	int3

3:
	// NtSuspendThread(sc.hThread, 0)
	mov eax, NT_SUSPEND_THREAD
	mov r10, qword ptr [rsp+SC_HTHREAD]
	xor rdx, rdx
	syscall

	/** we're now waiting for the second syscall **/
	
	/**
	  * on resume, the following happens:
	  * - we complete the NtSuspendThread (with untouched registers)
	  * - we jump back to the syscall code to repeat
	  **/
	jmp short 2b
