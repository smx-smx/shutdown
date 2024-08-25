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
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Windows.Win32.Foundation;
using static ShutdownLib.Ntdll;

namespace ShutdownLib
{
    public class SafeNtHandle : SafeHandle
    {
        public HANDLE Handle => new HANDLE(this.handle);

        public SafeNtHandle(HANDLE handle, bool ownsHandle) : base(-1, ownsHandle)
        {
            SetHandle(handle);
        }

        public SafeNtHandle(nint handle, bool ownsHandle) : this(new HANDLE(handle), ownsHandle) { }

        public override bool IsInvalid => handle == -1;

        protected override bool ReleaseHandle()
        {
            return NtClose(new HANDLE(this.handle)) == NtStatusCode.SUCCESS;
        }
    }
}
