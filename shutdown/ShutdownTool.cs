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

    public int Run(string[] args)
    {
        var ownDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        if (ownDir == null)
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

        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddLogging(log =>
        {
            var loggingSection = builder.Configuration.GetSection("Logging");
            log.AddFile(loggingSection);
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

        LoggerProviderOptions.RegisterProviderOptions<EventLogSettings, EventLogLoggerProvider>(builder.Services);
        /*builder.Services.Configure<ShutdownOptions>(
            builder.Configuration.GetSection("ShutdownSettings"),
            opts =>
            {
                opts.ErrorOnUnknownConfiguration = true;
            });*/
        builder.Services.AddSingleton(settings);

        builder.Services.AddSingleton<ShutDownActions>();
        builder.Services.AddSingleton<ShutdownWatcher>();
        builder.Services.AddHostedService<ShutdownService>();

        builder.Services.AddSingleton<DismountVolumesFactory>();
        builder.Services.AddSingleton<CloseOpenHandlesFactory>();
        builder.Services.AddSingleton<ShutdownVirtualMachinesFactory>();
        builder.Services.AddSingleton<ProcessKillerFactory>();


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
            if (args.Length > 0 && args[0] == "-now")
            {
                host.Services.GetRequiredService<ShutDownActions>().Run();
                Console.WriteLine("- done");
                return 0;
            }

            host.Run();
        }
        catch (Exception ex)
        {
            mainLogger.LogError(ex, "Unhandled exception");
            return 1;
        }
        return 0;
    }

    public static int Main(string[] args)
    {
        return new ShutdownTool().Run(args);
    }
}
