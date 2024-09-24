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
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Windows.Win32.Foundation;

namespace ShutdownLib
{
    public static class HandleExtensions
    {
        public static HANDLE ToHandle(this SafeHandle handle)
        {
            return new HANDLE(handle.DangerousGetHandle());
        }
    }
}
