#region License
/*
 * Copyright (C) 2024 Stefano Moioli <smxdev4@gmail.com>
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
#endregion

/*
#if TARGET_64BIT
using nint_t = System.Int64;
#else
using nint_t = System.Int32;
#endif
*/


using Smx.SharpIO.Memory;
using Windows.Win32.Foundation;

namespace Shutdown
{
	public struct ThreadSetupArgs
	{
		//public TypedPointer<SyscallContext> ScCtx;
		public nint ScCtx;
		public HANDLE hEvent;
		public HANDLE hThread;
	}

	public unsafe struct SyscallFrame
	{
		public const int MAX_ARGV = 10;

		public nint SavedRip;
		public fixed byte Shadow[32];
		public fixed ulong StackArgs[MAX_ARGV];
		public ulong Rax;
		public ulong R10;
		public ulong Rdx;
		public ulong R8;
		public ulong R9;
		public ThreadSetupArgs Setup;

	}
}

