#region License
/*
 * Copyright (C) 2024 Stefano Moioli <smxdev4@gmail.com>
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
#endregion
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Windows.Win32;

var args_it = args.GetEnumerator();

uint pid = 0;
bool sendCtrlC = true;
uint signal = PInvoke.CTRL_C_EVENT;

bool error = false;
for (int argi = 0, unnamed_argi = 0; args_it.MoveNext() && !error; argi++)
{
    switch (args_it.Current)
    {
        case "-check":
            sendCtrlC = false;
            break;
        default:
            switch (unnamed_argi++)
            {
                case 0:
                    if (!uint.TryParse(args[argi], out pid) || pid == 0)
                    {
                        error = true;
                    }
                    break;
                case 1:
                    signal = uint.Parse(args[argi]);
                    break;
            }
            break;
    }
}

if (error)
{
    Console.Error.WriteLine("Usage: [pid]");
    Environment.Exit(1);
}


PInvoke.FreeConsole();
if (!PInvoke.AttachConsole(pid))
{
    Console.Error.WriteLine($"AttachConsole failed: 0x{Marshal.GetLastPInvokeError():X} - {Marshal.GetLastPInvokeErrorMessage()}");
    Environment.Exit(1);
}
// Taken from MedallionShell:
// disable signal handling for our program
// from https://docs.microsoft.com/en-us/windows/console/setconsolectrlhandler:
// "Calling SetConsoleCtrlHandler with the NULL and TRUE arguments causes the calling process to ignore CTRL+C signals"
if (!PInvoke.SetConsoleCtrlHandler(null, true))
{
    Console.Error.WriteLine($"SetConsoleCtrlHandler failed: 0x{Marshal.GetLastPInvokeError():X} - {Marshal.GetLastPInvokeErrorMessage()}");
    Environment.Exit(1);
}

if (sendCtrlC)
{
    // special group 0: all process attached to the console
    if (!PInvoke.GenerateConsoleCtrlEvent(signal, 0))
    {
        Console.Error.WriteLine($"GenerateConsoleCtrlEvent failed: 0x{Marshal.GetLastPInvokeError():X} - {Marshal.GetLastPInvokeErrorMessage()}");
        Environment.Exit(1);
    }
}
