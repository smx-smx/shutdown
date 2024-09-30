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
    public class GenericSaveDialogKiller : ProcessKillerBase, IProcessKiller
    {
        private readonly ILogger<GenericSaveDialogKiller> _logger;

        public GenericSaveDialogKiller(
            ILogger<GenericSaveDialogKiller> logger
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
            return mainModule.ModuleName.Equals("notepad.exe", StringComparison.InvariantCultureIgnoreCase)
            || mainModule.ModuleName.Equals("mspaint.exe", StringComparison.InvariantCultureIgnoreCase)
            || mainModule.ModuleName.Equals("HxD.exe", StringComparison.InvariantCultureIgnoreCase);
        }

        public bool KillProcess(Process process, HWND hWnd, PerProcessSettings settings)
        {
            if (!GetCloseDialog(hWnd, () =>
            {
                return GetWindowByClass(process, "#32770");
            }, TimeSpan.FromMilliseconds(100), out var dlgHandle))
            {
                _logger.LogError($"Cannot kill pid {process.Id}: dialog not found");
                return false;
            }

            var buttons = FindWindows(dlgHandle, (hwnd) =>
            {
                return GetWindowClass(hwnd) == "Button";
            });

            if (buttons.Count != 3)
            {
                _logger.LogError($"Cannot kill pid {process.Id}: unexpected dialog with {buttons.Count} buttons");
                return false;
            }
            // [Save] - [Dont Save] - [Cancel]
            PInvoke.SendMessage(buttons[1], PInvoke.BM_CLICK, 0, 0);

            process.WaitForExit(settings.Timeout);
            return true;
        }
    }
}
