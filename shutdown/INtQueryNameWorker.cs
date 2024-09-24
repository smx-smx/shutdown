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
using ShutdownLib;

namespace Shutdown
{
    public interface INtQueryNameWorker
    {
        void Start();
        string? GetName(Span<SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX> handles, int index, int timeoutMs = 100);
    }
}
