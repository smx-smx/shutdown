#region License
/*
 * Copyright (C) 2024 Stefano Moioli <smxdev4@gmail.com>
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
#endregion
using Smx.SharpIO.Memory;
using Windows.Win32;
using Windows.Win32.System.Memory;

namespace Shutdown
{
	public class VirtualMemoryManager : IMemoryManager
	{
		public PAGE_PROTECTION_FLAGS ProtectionFlags { get; set; } = PAGE_PROTECTION_FLAGS.PAGE_EXECUTE_READWRITE;

		public unsafe nint Alloc(nuint size)
		{
			var ptr = PInvoke.VirtualAlloc(null, size, VIRTUAL_ALLOCATION_TYPE.MEM_COMMIT | VIRTUAL_ALLOCATION_TYPE.MEM_RESERVE, ProtectionFlags);
			return new nint(ptr);
		}

		public unsafe void Free(nint ptr)
		{
			PInvoke.VirtualFree(ptr.ToPointer(), 0, VIRTUAL_FREE_TYPE.MEM_RELEASE);
		}

		public nint Realloc(nint ptr, nuint size)
		{
			throw new NotSupportedException();
		}
	}
}
