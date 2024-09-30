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
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Win32.Foundation;

namespace Shutdown.Components.ProcessKiller
{
    public interface IProcessKiller
    {
        bool IsProcessSupported(Process process, HWND hWnd, PerProcessSettings settings);
        bool KillProcess(Process process, HWND hWnd, PerProcessSettings settings);
    }
}
