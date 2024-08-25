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
using System.Text;
using System.Threading.Tasks;
using Windows.Win32.Foundation;

namespace Shutdown
{
    public static class DisposableMemoryExtensions
    {
        public static unsafe PWSTR ToPWSTR(this NativeMemoryHandle mem)
        {
            return new PWSTR((char*)mem.Address.ToPointer());
        }
    }
}
