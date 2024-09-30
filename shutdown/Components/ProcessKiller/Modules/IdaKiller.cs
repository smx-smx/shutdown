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
using Microsoft.Extensions.Logging;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace Shutdown.Components.ProcessKiller.Modules
{
    public class IdaKiller : ProcessKillerBase, IProcessKiller
    {
        private readonly ILogger<IdaKiller> _logger;

        public IdaKiller(
            ILogger<IdaKiller> logger
        )
        {
            _logger = logger;
        }

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
            if (!GetCloseDialog(hWnd, () =>
            {
                return GetWindowByTitle(process, "Save database");
            }, TimeSpan.FromMilliseconds(100), out var dlgHandle))
            {
                _logger.LogError($"Cannot kill pid {process.Id}: dialog not found");
                return false;
            }

            SendAcceleratorKey(dlgHandle, 'K');
            process.WaitForExit(settings.Timeout);
            return process.HasExited;
        }
    }
}
