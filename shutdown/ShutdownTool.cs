#region License
/*
 * Copyright (C) 2024 Stefano Moioli <smxdev4@gmail.com>
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
#endregion
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Configuration;
using Microsoft.Extensions.Logging.EventLog;
using Microsoft.Win32.SafeHandles;
using NReco.Logging.File;
using Shutdown;
using Shutdown.Components;
using ShutdownLib;
using Smx.SharpIO.Extensions;
using Smx.SharpIO.Memory;
using Smx.Winter;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Dynamic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Services;
using static ShutdownLib.Ntdll;



class ShutdownTool
{
    public ShutdownTool()
    {

    }

    private const string SERVICE_NAME = "SmxShutdownService";
    private const string SERVICE_DISPLAY_NAME = "Smx Shutdown Service";

    private static void RestartAsService()
    {
        using var hSCManager = PInvoke.OpenSCManager(
                    null,
                    PInvoke.SERVICES_ACTIVE_DATABASE,
                    (uint)GENERIC_ACCESS_RIGHTS.GENERIC_EXECUTE | PInvoke.SC_MANAGER_CREATE_SERVICE);

        using var hServiceExisting = PInvoke.OpenService(
            hSCManager, SERVICE_NAME,
            (uint)(GENERIC_ACCESS_RIGHTS.GENERIC_READ | GENERIC_ACCESS_RIGHTS.GENERIC_EXECUTE));

        var hService = hServiceExisting;

        var exePath = Environment.ProcessPath;
        if (exePath == null)
        {
            throw new InvalidOperationException("Couldn't determine the Process Path");
        }

        if (hService.IsInvalid)
        {
            unsafe
            {
                hService = PInvoke.CreateService(
                    hSCManager,
                    SERVICE_NAME,
                    SERVICE_DISPLAY_NAME,
                    PInvoke.SERVICE_ALL_ACCESS,
                    ENUM_SERVICE_TYPE.SERVICE_WIN32_OWN_PROCESS,
                    SERVICE_START_TYPE.SERVICE_DEMAND_START,
                    SERVICE_ERROR.SERVICE_ERROR_NORMAL,
                    exePath,
                    null, null, null,
                    null, // LocalSystem
                    null
                );
                if (hService.IsInvalid)
                {
                    throw new Win32Exception();
                }
            }
        }

        var servicePid = Helpers.StartService(hService);
        if (servicePid == null)
        {
            throw new InvalidOperationException("Failed to start service");
        }
    }

    private static bool TryTake(IEnumerator<string> it, [MaybeNullWhen(false)] out string arg)
    {
        if (!it.MoveNext())
        {
            arg = null;
            return false;
        }

        arg = it.Current;
        return true;
    }

    public int Run(string[] args)
    {
        var ownDir = AppContext.BaseDirectory;
        if (string.IsNullOrEmpty(ownDir))
        {
            throw new InvalidOperationException("Failed to get execution directory");
        }
        Environment.CurrentDirectory = ownDir;

#if false
        if (!WindowsServiceHelpers.IsWindowsService())
        {
            RestartAsService();
            Environment.Exit(0);
        } else
        {
            Helpers.LaunchDebugger();
        }
#endif

#if DEBUG
        if (!WindowsServiceHelpers.IsWindowsService())
        {
            //Helpers.AllocConsole();
        }
#endif


        var runNow = false;
        var shutdownMode = ShutdownMode.Shutdown;
        var debugMode = false;

        var args_it = args.GetEnumerator();
        while (args_it.MoveNext())
        {
            var res = true;
            var arg = args_it.Current;
            switch (arg)
            {
                case "-now":
                    runNow = true;
                    break;
                case "-pre":
                    shutdownMode = ShutdownMode.PreShutdown;
                    break;
                case "-debug":
                    debugMode = true;
                    break;
            }

            if (!res)
            {
                throw new ArgumentException("Invalid program arguments");
            }
        }

        var modeIdent = ShutdownGlobals.GetModeIdentity(shutdownMode);

        var pidFile = ShutdownGlobals.GetPidFile(shutdownMode);
        File.WriteAllText(pidFile, Process.GetCurrentProcess().Id.ToString());

        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddLogging(log =>
        {
            var loggingSection = builder.Configuration.GetSection("Logging");
            log.AddFile(loggingSection, opts =>
            {
                opts.FormatLogFileName = fName =>
                {
                    return string.Format(fName, modeIdent);
                };
            });
        });

        builder.Services.AddWindowsService(opts =>
        {
            opts.ServiceName = "ShutdownService";
        });

        /** 
         * workaround for https://stackoverflow.com/questions/41259476/microsoft-extensions-configuration-binding-dictionary-with-colons-in-key
         **/
        var settingsPath = Path.Combine(ownDir, "AppSettings.json");
        ShutdownSettingsRoot? settings;
        {
            using var jsonStream = new FileStream(settingsPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            settings = JsonSerializer.Deserialize<ShutdownSettingsRoot>(jsonStream);
        }
        if (settings == null)
        {
            throw new InvalidOperationException($"Failed to read configuration from {settingsPath}");
        }


        // spawn a copy for normal shutdown
        if (shutdownMode == ShutdownMode.PreShutdown && !runNow)
        {
            var normalModeProc = ShutdownGlobals.GetProcess(ShutdownMode.Shutdown);
            /**
              * spawn normal mode runner, if not already running
              * the runner might be already running if shutdown got interrupted and we were respawned by ShutdownAbortMonitor
              **/
            if (normalModeProc == null)
            {
                var pi = new ProcessStartInfo
                {
                    FileName = ShutdownGlobals.GetExeFile(ShutdownProgramType.Shutdown)
                };
                if (debugMode)
                {
                    pi.ArgumentList.Add("-debug");
                }
                Process.Start(pi);
            }
        }

        LoggerProviderOptions.RegisterProviderOptions<EventLogSettings, EventLogLoggerProvider>(builder.Services);
        /*builder.Services.Configure<ShutdownOptions>(
            builder.Configuration.GetSection("ShutdownSettings"),
            opts =>
            {
                opts.ErrorOnUnknownConfiguration = true;
            });*/
        builder.Services.AddSingleton(settings);

        builder.Services.AddSingleton<ShutDownActions>();
        builder.Services.AddSingleton<ShutdownWatcherFactory>();
        if (!runNow)
        {
            builder.Services.AddHostedService((services) =>
            {
                var watcherFactory = services.GetRequiredService<ShutdownWatcherFactory>();
                var watcher = watcherFactory.CreateWatcher(shutdownMode, debugMode);
                return new ShutdownWatcherService(watcher);
            });
        }

        builder.Services.AddScoped<NtSyscallWorker>();
        builder.Services.AddScoped<NtQueryNameNative>();
        builder.Services.AddScoped<NtQueryNameWorkerProviderNative>();
        builder.Services.AddScoped<NtQueryNameWorkerProviderIpc>();
        builder.Services.AddSingleton<INtQueryNameWorkerProvider>((services) =>
        {
            return services.GetRequiredService<NtQueryNameWorkerProviderNative>();
        });

        builder.Services.AddSingleton<DismountVolumesFactory>();
        builder.Services.AddSingleton<CloseOpenHandlesFactory>();
        builder.Services.AddSingleton<ShutdownVirtualMachinesFactory>();
        builder.Services.AddSingleton<ProcessKillerFactory>();
        builder.Services.AddSingleton<LogoutIscsiFactory>();


        builder.Services.AddSingleton<ShutdownActionFactories>();

        var host = builder.Build();

        var mainLogger = host.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger<ShutdownTool>();



        foreach (var arg in args)
        {
            mainLogger.LogDebug($"=> {arg}");
        }


        /** VMWare auto-start VMs run as SYSTEM **/
        Helpers.EnablePrivilege(PInvoke.SE_DEBUG_NAME);
        Helpers.EnablePrivilege(PInvoke.SE_IMPERSONATE_NAME);
        using var systemToken = ElevationService.ImpersonateSystem();
        Helpers.EnablePrivilege(PInvoke.SE_TCB_NAME); // for WinSta impersonation


        try
        {
            if (runNow)
            {
                host.Services.GetRequiredService<ShutDownActions>().Run(shutdownMode);
                Console.WriteLine("- done");
            } else
            {
                host.Run();
            }
        } catch (Exception ex)
        {
            mainLogger.LogError(ex, "Unhandled exception");
            return 1;
        }
        mainLogger.LogInformation("exiting");
        return 0;
    }

    public static int Main(string[] args)
    {
        return new ShutdownTool().Run(args);
    }
}
