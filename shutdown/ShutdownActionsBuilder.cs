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
using Microsoft.Extensions.DependencyInjection;
using Shutdown.Components;

namespace Shutdown
{
    public class ShutdownActionsBuilder
    {
        private IList<IAction> _actions;
        private ShutdownOptions _options;

        private ICollection<CloseOpenHandlesItem> _closeHandlesList;
        private HashSet<string> _dismountVolumesList;


        private ShutdownActionFactories _factories;

        public ShutdownActionsBuilder(
            ShutdownOptions options,
            ShutdownActionFactories factories
        )
        {
            _factories = factories;
            _actions = new List<IAction>();
            _options = options;

            _closeHandlesList = new List<CloseOpenHandlesItem>();
            _dismountVolumesList = new HashSet<string>();
        }

        private void AddVolume(string volumeName, VolumeOptions opts)
        {
            var enable = opts.CloseHandles?.Enable ?? true;
            if (enable)
            {
                _closeHandlesList.Add(new CloseOpenHandlesItem
                {
                    Name = volumeName,
                    FlushObjects = opts.CloseHandles?.FlushObjects ?? false
                });
            }

            enable = opts.Dismount?.Enable ?? true;
            if (enable)
            {
                _dismountVolumesList.Add(volumeName);
            }
        }

        private void BuildVirtualMachines()
        {
            if (_options.VirtualMachines == null) return;
            var shutdownVms = _factories.shutdownVms.Create(new ShutdownVmParams
            {
                DryRun = _options.DryRun,
                DefaultOptions = ShutdownVmParams.DefaultVmOptions,
                Items = _options.VirtualMachines.Items
            });

            _actions.Add(shutdownVms);
        }

        private void BuildVolumes()
        {
            if (_options.Volumes == null) return;
            foreach (var volume in _options.Volumes)
            {
                AddVolume(volume.Key, volume.Value);
            }

            var closeHandles = _factories.closeOpenHandles.Create(new CloseOpenHandlesParams
            {
                DryRun = _options.DryRun,
                Volumes = _closeHandlesList
            });
            var dismountVolumes = _factories.dismountVolumes.Create(_dismountVolumesList);

            _actions.Add(closeHandles);
            _actions.Add(dismountVolumes);
        }


        public ICollection<IAction> Build()
        {
            BuildVolumes();
            BuildVirtualMachines();
            return _actions;
        }
    }
}
