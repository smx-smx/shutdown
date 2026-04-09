#region License
/*
 * Copyright (C) 2024 Stefano Moioli <smxdev4@gmail.com>
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
#endregion
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shutdown.Components;
using ShutdownLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
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
        private readonly ILogger<ShutDownActions> _logger;
        private readonly ILogger<ShutdownActionsBuilder> _logger2;

        public ShutDownActions(
            ShutdownSettingsRoot options,
            ShutdownActionFactories factories,
            ILogger<ShutDownActions> logger,
            ILogger<ShutdownActionsBuilder> logger2)
        {
            _options = options.ShutdownSettings;
            _factories = factories;
            _logger = logger;
            _logger2 = logger2;
        }

        private void RunActions(ShutdownMode mode)
        {
            var actions = new ShutdownActionsBuilder(_options, _factories, _logger2).Build(mode);
            foreach (var act in actions)
            {
                _logger.LogInformation($"Running {act.GetType().Name}");
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
            }
            finally
            {
                _sema.Release();
            }
        }
    }
}
