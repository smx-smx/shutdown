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

namespace ShutdownLib
{
    public class WtsMemoryManager : IMemoryManager
    {
        public nint Alloc(nuint size)
        {
            throw new NotSupportedException();
        }

        public unsafe void Free(nint ptr)
        {
            PInvoke.WTSFreeMemory(ptr.ToPointer());
        }

        public nint Realloc(nint ptr, nuint size)
        {
            throw new NotSupportedException();
        }
    }
}
