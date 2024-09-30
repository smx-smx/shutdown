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
using System.Threading.Tasks;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace Shutdown.Components.ProcessKiller.Modules
{
    public class IdaKiller : ProcessKillerBase, IProcessKiller
    {
        public bool IsProcessSupported(Process process, HWND hWnd, PerProcessSettings settings)
        {
            if (!settings.Flags.HasFlag(ProcessKillFlags.UI_AttemptSave))
            {
                return false;
            }

            if (!TryGetMainModule(process, out var mainModule))
            {
                return false;
            }
            return mainModule.ModuleName.Equals("ida64.exe", StringComparison.InvariantCultureIgnoreCase)
            || mainModule.ModuleName.Equals("ida.exe", StringComparison.InvariantCultureIgnoreCase);
        }

        public bool KillProcess(Process process, HWND hWnd, PerProcessSettings settings)
        {
            PInvoke.PostMessage(hWnd, PInvoke.WM_SYSCOMMAND, PInvoke.SC_CLOSE, 0);
            Thread.Sleep(100);

            var boxedSaveHwnd = GetWindowByTitle(process, "Save database");
            if (boxedSaveHwnd == null) return false;

            var saveHwnd = boxedSaveHwnd.Value;

            SendAcceleratorKey(saveHwnd, 'K');
            process.WaitForExit(settings.Timeout);
            return process.HasExited;
        }
    }
}
