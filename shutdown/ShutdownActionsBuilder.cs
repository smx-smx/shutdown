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
using ShutdownLib;

namespace Shutdown
{
    [Flags]
    public enum ShutdownVirtualMachinesFlags
    {
        Normal = 1 << 0,
        Critical = 1 << 1
    }


    public class ShutdownActionsBuilder
    {
        private IList<IAction> _actions;
        private ShutdownOptions _options;

        private ICollection<CloseOpenHandlesItem> _closeHandlesList;
        private ICollection<DismountVolumeItem> _dismountVolumesList;

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
            _dismountVolumesList = new List<DismountVolumeItem>();
        }

        private void AddVolume(string volumeName, VolumeOptions opts)
        {
            var enable = opts.CloseHandles?.Enable ?? true;
            if (enable)
            {
                _closeHandlesList.Add(new CloseOpenHandlesItem
                {
                    IsVolume = true,
                    NameOrPath = volumeName,
                    FlushObjects = opts.CloseHandles?.FlushObjects ?? false
                });
            }

            enable = opts.Dismount?.Enable ?? true;
            if (enable)
            {
                _dismountVolumesList.Add(new DismountVolumeItem
                {
                    Dismount = enable,
                    VolumeLetter = volumeName,
                    OfflineDisks = opts.OwningDisks?.Offline?.Enable ?? false
                });
            }
        }

        private void BuildIscsiTargets()
        {
            if (_options.IscsiTargets == null) return;
            var iscsiTargets = _options.IscsiTargets
                .Where(it => it.Value.Logout?.Enable == true)
                .Select(it => it.Key)
                .ToHashSet();

            var logoutAction = _factories.logoutIscsiTargets.Create(new LogoutIscsiParams
            {
                DryRun = _options.DryRun,
                Targets = iscsiTargets
            });
            _actions.Add(logoutAction);
        }

        private void BuildVirtualMachines(ShutdownVirtualMachinesFlags flags)
        {
            if (_options.VirtualMachines == null) return;
            var shutdownVms = _factories.shutdownVms.Create(new ShutdownVmParams
            {
                DryRun = _options.DryRun,
                DefaultOptions = ShutdownVmParams.DefaultVmOptions,
                Items = _options.VirtualMachines.Items,
                Flags = flags
            });

            _actions.Add(shutdownVms);
        }

        private void BuildCloseHandlePaths()
        {
            foreach (var pathSpec in _options.CloseHandles)
            {
                if (!pathSpec.Value.Enable) continue;
                _closeHandlesList.Add(new CloseOpenHandlesItem
                {
                    IsVolume = false,
                    NameOrPath = pathSpec.Key
                });
            }
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
                Paths = _closeHandlesList
            });
            var dismountVolumes = _factories.dismountVolumes.Create(new DismountVolumesParams
            {
                Volumes = _dismountVolumesList
            });

            _actions.Add(closeHandles);
            _actions.Add(dismountVolumes);
        }

        private void BuildProcessKiller()
        {
            var enable = _options.KillProcesses?.Enable ?? true;
            if (enable)
            {
                var opts = new ProcessKillerParams
                {
                    ProcessSettings = _options.KillProcesses?.Processes.ToDictionary(
                        p => p.Key,
                        p => new PerProcessSettings
                        {
                            MainModuleName = p.Key,
                            Timeout = TimeSpan.FromSeconds(p.Value.TimeoutSeconds)
                        }
                    ) ?? new Dictionary<string, PerProcessSettings>(),
                    DryRun = _options.KillProcesses?.DryRun.GetValueOrDefault(_options.DryRun) ?? _options.DryRun
                };
                var processKiller = _factories.processKiller.Create(opts);
                _actions.Add(processKiller);
            }
        }

        public ICollection<IAction> Build(ShutdownMode mode)
        {
            if (mode == ShutdownMode.PreShutdown)
            {
                BuildProcessKiller();
            } else
            {
                BuildCloseHandlePaths();
                BuildVolumes();
                BuildVirtualMachines(ShutdownVirtualMachinesFlags.Normal);
                BuildIscsiTargets();
                BuildVirtualMachines(ShutdownVirtualMachinesFlags.Critical);
            }
            return _actions;
        }
    }
}
