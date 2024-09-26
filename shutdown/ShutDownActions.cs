#region License
/*
 * Copyright (C) 2024 Stefano Moioli <smxdev4@gmail.com>
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
#endregion
using Microsoft.Extensions.Options;
using Shutdown.Components;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace Shutdown
{
    public class ShutDownActions
    {
        private bool _hasRun = false;
        private SemaphoreSlim _sema = new SemaphoreSlim(1);
        private ShutdownState _state = new ShutdownState(null);

        private readonly ShutdownOptions _options;
        private readonly ShutdownActionFactories _factories;

        public ShutDownActions(ShutdownSettingsRoot options, ShutdownActionFactories factories)
        {
            _options = options.ShutdownSettings;
            _factories = factories;
        }

        private void RunActions(ShutdownMode mode)
        {
            var actions = new ShutdownActionsBuilder(_options, _factories).Build(mode);
            foreach (var act in actions)
            {
                act.Execute(_state);
            }
        }

        public void Run(ShutdownMode mode, HWND? hWND = null)
        {
            _state = new ShutdownState(hWND);
            _sema.Wait();
            try
            {
                if (_hasRun) return;
                _hasRun = true;
                RunActions(mode);
            } finally
            {
                _sema.Release();
            }
        }
    }
}
