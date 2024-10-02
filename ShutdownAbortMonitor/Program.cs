#region License
/*
 * Copyright (C) 2024 Stefano Moioli <smxdev4@gmail.com>
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
#endregion
using System.Diagnostics;
using System.Reflection;
using ShutdownLib;
using Windows.Win32;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ShutdownAbortMonitor;

public class Program
{
	public static string GetBinaryDir()
	{
		var ownDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
		if (ownDir == null)
		{
			throw new InvalidOperationException("Cannot determine execution directory");
		}
		return ownDir;
	}

	private static Process? RestartPreWatcher(ICollection<string> childArgs)
	{
		var preShutdownProc = ShutdownGlobals.GetProcess(ShutdownMode.PreShutdown);
		if (preShutdownProc == null)
		{
			Console.Error.WriteLine("pre-shutdown watcher not found");
		}
		else
		{
			preShutdownProc.WaitForExit(TimeSpan.FromSeconds(5));
			if (!preShutdownProc.HasExited)
			{
				preShutdownProc.Kill();
			}
		}

		var self = Process.GetCurrentProcess();
		if (self.MainModule == null) return null;

		var pi = new ProcessStartInfo
		{
			FileName = ShutdownGlobals.GetExeFile(ShutdownProgramType.Shutdown)
		};
		pi.ArgumentList.Add("-pre");
		foreach (var arg in childArgs)
		{
			pi.ArgumentList.Add(arg);
		}
		return Process.Start(pi);
	}

	public static void Main(string[] args)
	{
		var args_it = args.GetEnumerator();
		var argi = 0;

		var childArgs = Array.Empty<string>();
		for (; args_it.MoveNext(); argi++)
		{
			var arg = args_it.Current;
			switch (arg)
			{
				case "--":
					childArgs = args.Skip(argi + 1).ToArray();
					break;
			}
		}

		int phase = 0;
		var isShuttingDown = false;
		var shutdownAborted = false;
		for (; !shutdownAborted; Thread.Sleep(200))
		{
			var prevValue = isShuttingDown;
			isShuttingDown = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_SHUTTINGDOWN) != 0;

			Console.WriteLine($"[{phase}]: isShuttingDown: {isShuttingDown}");

			switch (phase)
			{
				case 0:
					// rising edge
					if (!prevValue && isShuttingDown)
					{
						++phase;
					}
					break;
				case 1:
					// falling edge
					if (prevValue && !isShuttingDown)
					{
						shutdownAborted = true;
					}
					break;
			}
		}

		if (shutdownAborted)
		{
			Console.WriteLine("Shutdown aborted, restarting shutdown watcher");
			RestartPreWatcher(childArgs);
		}
	}
}
