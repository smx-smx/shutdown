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
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Shutdown
{
    public class ActionOptions
    {
        public bool Enable { get; set; } = true;
    }

    public class CloseHandlesOptions : ActionOptions
    {
        public bool FlushObjects { get; set; } = true;
    }

    public class DismountVolumeOptions : ActionOptions
    {
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum VmShutdownType
    {
        Suspend,
        Shutdown,
        Kill
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum VmShutdownMode
    {
        Soft,
        Hard
    }

    public class VolumeOptions
    {
        public DismountVolumeOptions? Dismount { get; set; }
        public CloseHandlesOptions? CloseHandles { get; set; }
    }

    public class KillProcessOptions
    {
        public uint TimeoutSeconds { get; set; } = 60;
    }

    public class KillProcessesOptions : ActionOptions
    {
        public bool? DryRun { get; set; }
        public Dictionary<string, KillProcessOptions> Processes { get; set; } = new Dictionary<string, KillProcessOptions>();
    }

    public class ShutdownVmOptions
    {
        /// <summary>
        /// Shutdown VM priority. negative values to put this VM are relative to the list end
        /// </summary>
        public int Order { get; set; } = 0;
        public bool Ignore { get; set; } = false;
        public string VmxPath { get; set; } = string.Empty;

        public VmShutdownType FirstAction { get; set; } = VmShutdownType.Shutdown;
        public VmShutdownMode FirstActionMode { get; set; } = VmShutdownMode.Soft;
        public int FirstActionTimeoutSeconds { get; set; } = 300;
        public VmShutdownType SecondAction { get; set; } = VmShutdownType.Suspend;
        public VmShutdownMode SecondActionMode { get; set; } = VmShutdownMode.Hard;
        public int SecondActionTimeoutSeconds { get; set; } = 300;
    }

    public class VirtualMachineOptions
    {
        public ShutdownVmOptions? Default { get; set; }
        public IList<ShutdownVmOptions> Items { get; set; } = new List<ShutdownVmOptions>();
    }

    public class ShutdownOptions
    {
        public bool DryRun { get; set; } = false;
        public Dictionary<string, VolumeOptions> Volumes { get; set; } = new Dictionary<string, VolumeOptions>();
        public VirtualMachineOptions? VirtualMachines { get; set; }
        public KillProcessesOptions? KillProcesses { get; set; }

    }

    public class ShutdownSettingsRoot
    {
        public required ShutdownOptions ShutdownSettings { get; set; }
    }
}
