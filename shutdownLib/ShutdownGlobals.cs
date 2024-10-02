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
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace ShutdownLib;

public enum ShutdownMode
{
    /// <summary>
    /// Pre-Shutdown actions
    /// </summary>
    PreShutdown,
    /// <summary>
    /// Shutdown actions
    /// </summary>
    Shutdown
}

public enum ShutdownProgramType
{
    Shutdown,
    NtQueryNameWorker,
    ProcessSignaler,
    ShutdownAbortMonitor
}

public class ShutdownGlobals
{
    public static string GetModeIdentity(ShutdownMode mode)
    {
        var modeIdent = mode switch
        {
            ShutdownMode.PreShutdown => "pre",
            ShutdownMode.Shutdown => "normal",
            _ => Enum.GetName(mode)
        };
        if (modeIdent == null)
        {
            throw new InvalidOperationException("Invalid shutdown mode");
        }
        return modeIdent;
    }

    public static string GetBinaryDir()
    {
        var ownDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        if (ownDir == null)
        {
            throw new InvalidOperationException("Cannot determine execution directory");
        }
        return ownDir;
    }

    public static string GetExeFile(ShutdownProgramType program)
    {
        var ownDir = GetBinaryDir();
        var fileName = program switch
        {
            ShutdownProgramType.NtQueryNameWorker => "NtQueryNameWorker.exe",
            ShutdownProgramType.Shutdown => "shutdown.exe",
            ShutdownProgramType.ProcessSignaler => "ProcessSignaler.exe",
            ShutdownProgramType.ShutdownAbortMonitor => "ShutdownAbortMonitor.exe",
            _ => throw new ArgumentException($"Invalid program type: {Enum.GetName(program)}")
        };
        return Path.Combine(ownDir, fileName);
    }

    public static string GetPidFile(ShutdownMode mode)
    {
        return Path.Combine(
            GetBinaryDir(),
            $"shutdown_{GetModeIdentity(mode)}.pid");
    }

    public static Process? GetProcess(ShutdownMode mode)
    {
        var pidFile = GetPidFile(mode);
        if (!File.Exists(pidFile))
        {
            return null;
        }
        if (!int.TryParse(File.ReadAllText(pidFile), out var pid))
        {
            return null;
        }
        try
        {
            return Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            // process not running
            return null;
        }
    }
}
