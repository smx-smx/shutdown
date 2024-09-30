#region License
/*
 * Copyright (C) 2024 Stefano Moioli <smxdev4@gmail.com>
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
#endregion
using ShutdownLib;
using Smx.SharpIO.Memory;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.Win32;
using Windows.Win32.System.Threading;
using static ShutdownLib.Ntdll;

namespace Shutdown
{
    public class ProcessUtil
    {
        public static List<string> ParseCommandLine(string cmdline)
        {
            var parts = new List<string>();
            var regex = new Regex(@"(""(?:\\.|[^""])*""|\S+)");
            var matches = regex.Matches(cmdline);

            foreach (Match m in matches)
            {
                var value = m.Value;
                if (value.StartsWith("\"") && value.EndsWith("\""))
                {
                    value = value.Substring(1, value.Length - 2);
                }
                parts.Add(value);
            }
            return parts;
        }

        public static string? GetCommandLine(Process proc)
        {
            var dwProcessId = (uint)proc.Id;

            using var hProc = PInvoke.OpenProcess_SafeHandle(0
                | PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_INFORMATION
                | PROCESS_ACCESS_RIGHTS.PROCESS_VM_READ,
                false, dwProcessId
            );
            if (hProc.IsInvalid) return null;

            var info = MemoryHGlobal.Alloc<PROCESS_BASIC_INFORMATION>();
            var status = NtQueryInformationProcess(
                hProc.ToHandle(),
                (uint)PROCESS_INFORMATION_CLASS.ProcessBasicInformation,
                info.Address, (uint)Unsafe.SizeOf<PROCESS_BASIC_INFORMATION>(),
                out var returnLength
            );

            if (!NT_SUCCESS(status))
            {
                return null;
            }

            string cmdLine;
            unsafe
            {
                var pPeb = info.Value.PebBaseAddress;
                if (pPeb == null) return null;

                /** phase 1 **/

                using var pebBuf = MemoryHGlobal.Alloc<PEB_unmanaged>();

                nuint nRead = 0;
                if (!PInvoke.ReadProcessMemory(hProc, pPeb,
                    pebBuf.Address.ToPointer(), pebBuf.Memory.Size,
                    &nRead
                ) || nRead != pebBuf.Memory.Size)
                {
                    throw new Win32Exception();
                }
                nRead = 0;

                /** phase 2 **/

                using var pebProcParams = MemoryHGlobal.Alloc<RTL_USER_PROCESS_PARAMETERS>();

                var peb = pebBuf.Value;
                if (peb.ProcessParameters == null) return null;

                if (!PInvoke.ReadProcessMemory(hProc, peb.ProcessParameters,
                    pebProcParams.Address.ToPointer(), pebProcParams.Memory.Size,
                    &nRead
                ) || nRead != pebProcParams.Memory.Size)
                {
                    throw new Win32Exception();
                }
                nRead = 0;

                /** phase 3 **/

                var cmdlineBuf = pebProcParams.Value.CommandLine;
                if (cmdlineBuf.Buffer.Value == null) return null;

                using var charBuf = MemoryHGlobal.Alloc(sizeof(char) * cmdlineBuf.Length);

                if (!PInvoke.ReadProcessMemory(hProc, cmdlineBuf.Buffer.Value,
                    charBuf.Address.ToPointer(), charBuf.Size, &nRead
                ) || nRead != charBuf.Size)
                {
                    throw new Win32Exception();
                }

                cmdLine = charBuf.ToPWSTR().ToString();

            }
            return cmdLine;
        }
    }
}
