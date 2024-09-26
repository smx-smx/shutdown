#region License
/*
 * Copyright (C) 2024 Stefano Moioli <smxdev4@gmail.com>
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
#endregion
﻿using System.ComponentModel;
using Windows.Win32;

uint pid = 0;
if (args.Length < 1
	|| !uint.TryParse(args[0], out pid)
	|| pid == 0)
{
	Console.Error.WriteLine("Usage: [pid]");
	Environment.Exit(1);
}

uint signal = args.Length > 1
	? uint.Parse(args[1])
	: PInvoke.CTRL_C_EVENT;

PInvoke.FreeConsole();
if (!PInvoke.AttachConsole(pid))
{
	throw new Win32Exception();
}
// Taken from MedallionShell:
// disable signal handling for our program
// from https://docs.microsoft.com/en-us/windows/console/setconsolectrlhandler:
// "Calling SetConsoleCtrlHandler with the NULL and TRUE arguments causes the calling process to ignore CTRL+C signals"
if (!PInvoke.SetConsoleCtrlHandler(null, true))
{
	throw new Win32Exception();
}

if (!PInvoke.GenerateConsoleCtrlEvent(signal, pid))
{
	throw new Win32Exception();
}
