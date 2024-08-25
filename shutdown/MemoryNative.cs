#region License
/*
 * Copyright (C) 2024 Stefano Moioli <smxdev4@gmail.com>
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
#endregion
using Smx.SharpIO.Memory;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Shutdown
{
    internal class NativeMemoryManager : IMemoryManager
    {
        public unsafe nint Alloc(nuint size) => new nint(NativeMemory.Alloc(size));
        public unsafe void Free(nint ptr) => NativeMemory.Free(ptr.ToPointer());
        public unsafe nint Realloc(nint ptr, nuint size) => new nint(NativeMemory.Realloc(ptr.ToPointer(), size));
    }

    internal static class MemoryNative
    {
        public static readonly MemoryAllocator Allocator = new MemoryAllocator(new NativeMemoryManager());

        public static TypedMemoryHandle<T> Alloc<T>(nuint? size = null) where T : unmanaged
        {
            return Allocator.Alloc<T>(size);
        }

        public static NativeMemoryHandle Alloc(nuint size, bool owned = true)
        {
            return Allocator.Alloc(size, owned);
        }

        public static NativeMemoryHandle Alloc(nint size, bool owned = true)
        {
            return Alloc((nuint)size, owned);
        }
    }
}
