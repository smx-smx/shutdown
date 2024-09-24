#region License
/*
 * Copyright (C) 2024 Stefano Moioli <smxdev4@gmail.com>
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
#endregion
using ShutdownLib;
using Smx.SharpIO;
using Smx.SharpIO.Extensions;
using Smx.Winter;
using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Windows.Win32;
using static ShutdownLib.Ntdll;

namespace worker
{
    public class NtQueryNameServer
    {
        public static int Main(string[] args)
        {
            Helpers.EnablePrivilege(PInvoke.SE_DEBUG_NAME);
            Helpers.EnablePrivilege(PInvoke.SE_IMPERSONATE_NAME);
            using var systemToken = ElevationService.ImpersonateSystem();

            using var thisProc = PInvoke.GetCurrentProcess_SafeHandle();

            using var pipeIn = new AnonymousPipeClientStream(PipeDirection.In, args[0]);
            using var pipeOut = new AnonymousPipeClientStream(PipeDirection.Out, args[1]);

            try
            {
                using var sr = new StreamReader(pipeIn);
                using var sw = new StreamWriter(pipeOut);
                sw.WriteLine("."); // welcome banner
                sw.Flush();

                var itmSize = Unsafe.SizeOf<SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX>();
                while (true)
                {
                    var buf = new byte[itmSize];
                    if (pipeIn.Read(buf) != itmSize) break;

                    var itm = buf.AsSpan().Cast<SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX>()[0];
                    var handle = new SafeNtHandle(itm.HandleValue, false);
                    string? name;
                    try
                    {
                        name = Helpers.GetHandleName(thisProc, handle, (uint)itm.UniqueProcessId);
                    }
                    catch (InvalidOperationException)
                    {
                        continue;
                    }
                    sw.WriteLine(name);
                    sw.Flush();
                }
            }
            catch (IOException)
            {
                Console.Error.WriteLine("Got IO Exception, aborting");
            }
            return 0;
        }
    }
}
