#region License
/*
 * Copyright (C) 2024 Stefano Moioli <smxdev4@gmail.com>
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
#endregion
using Microsoft.Extensions.Logging;
using ShutdownLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Shutdown.Components.ProcessKiller.Modules
{
    public class ArsenalImageMounter : ProcessKillerBase, IProcessKiller
    {
        private readonly ILogger<ArsenalImageMounter> _logger;

        public ArsenalImageMounter(ILogger<ArsenalImageMounter> logger)
        {
            _logger = logger;
        }

        public bool IsProcessSupported(Process process, HWND hWnd, PerProcessSettings settings)
        {
            if (!TryGetMainModule(process, out var mainMod))
            {
                return false;
            }
            if (!mainMod.ModuleName.Equals("ArsenalImageMounter.exe", StringComparison.InvariantCultureIgnoreCase))
            {
                return false;
            }

            var cmdline = ProcessUtil.GetCommandLine(process);
            if (cmdline == null || cmdline.Contains("--cli", StringComparison.InvariantCultureIgnoreCase))
            {
                return false;
            }
            return true;
        }

        public bool KillProcess(Process process, HWND hWnd, PerProcessSettings settings)
        {
            PInvoke.PostMessage(hWnd, PInvoke.WM_SYSCOMMAND, PInvoke.SC_CLOSE, 0);
            process.WaitForExit(TimeSpan.FromMinutes(1));
            return process.HasExited;
        }
    }
}
