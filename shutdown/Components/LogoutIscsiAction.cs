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
using Smx.SharpIO.Extensions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Windows.Win32;
using Windows.Win32.Storage.IscsiDisc;

namespace Shutdown.Components
{
    public class LogoutIscsiParams
    {
        public required HashSet<string> Targets { get; set; }
        public required bool DryRun { get; set; }
    }

    public class LogoutIscsiFactory
    {
        private ILoggerFactory _factory;
        public LogoutIscsiFactory(ILoggerFactory factory)
        {
            _factory = factory;
        }

        public LogoutIscsiAction Create(LogoutIscsiParams opts)
        {
            return new LogoutIscsiAction(opts, _factory.CreateLogger<LogoutIscsiAction>());
        }
    }

    public class LogoutIscsiAction : IAction
    {
        private readonly LogoutIscsiParams _opts;
        private readonly ILogger<LogoutIscsiAction> _logger;

        private Dictionary<string, ISCSI_UNIQUE_SESSION_ID> GetInitiatorSessions(HashSet<string> initiators)
        {
            uint res = (uint)Win32Error.ERROR_SUCCESS;
            uint sessionCount = 0;
            using var buf = Helpers.Win32CallWithGrowableBuffer((buf) =>
            {
                uint bufferSize = buf.Size.ToUInt32();

                unsafe
                {
                    uint localSessionCount = 0;
                    res = PInvoke.GetIScsiSessionList(
                        &bufferSize,
                        &localSessionCount,
                        (ISCSI_SESSION_INFOW*)buf.Address.ToPointer()

                    );
                    sessionCount = localSessionCount;
                }
                return res;
            });

            var result = new Dictionary<string, ISCSI_UNIQUE_SESSION_ID>();

            var sessions = buf.AsSpan<ISCSI_SESSION_INFOW>(0, (int)sessionCount);
            foreach (var s in sessions)
            {
                var initiatorName = s.TargetName.ToString();
                if (initiators.Contains(initiatorName))
                {
                    result.Add(initiatorName, s.SessionId);
                }
            }
            return result;
        }

        public void Execute(ShutdownState actions)
        {
            var sessions = GetInitiatorSessions(_opts.Targets);
            var prefix = _opts.DryRun ? "[DRY] " : "";

            foreach (var sess in sessions)
            {
                var sid = sess.Value;
                _logger.LogInformation(prefix + $"Logout Target: {sess.Key}");
                if (_opts.DryRun)
                {
                    continue;
                }
                actions.SetShutdownStatusMessage($"Logout {sess.Key}");
                var res = PInvoke.LogoutIScsiTarget(ref sid);
                if (res != (uint)Win32Error.ERROR_SUCCESS)
                {
                    _logger.LogError($"Failed to logout iSCSI target {sess.Key}: "
                        + $"0x{Marshal.GetLastPInvokeError():X} - {Marshal.GetLastPInvokeErrorMessage()}");
                    continue;
                }
            }
        }

        public LogoutIscsiAction(
            LogoutIscsiParams opts,
            ILogger<LogoutIscsiAction> logger
        )
        {
            _opts = opts;
            _logger = logger;
        }
    }
}
