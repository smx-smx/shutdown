# Shutdown tool (still looking for a better name)

This tool was created to help me shut down my system properly, i.e. intercept shutdown and close services, virtual machines and processes in the right order.

The goal is to:

- Dismount an NTFS volume
- That is provided by iSCSI
- By a virtual machine (TrueNas) running on the same PC

The same virtual machine exposes other Virtual Machines from a Network Share, so it's important that those are shut down before TrueNas itself.

This tool accomplishes all of this by implementing:

- a "Shutdown Watcher" that registers a dummy/invisible window in order to catch `WM_QUERYENDSESSION` and `WM_ENDSESSION` events.


- a "Close Handles" action that will close open files on a given NTFS volume
- an optional "Sync Buffers" action that will flush any unwritten changes to disk to prevent corruption (shouldn't be really needed unless a forceful removal of the volume or unclean shutdown is to be performed)
- a "Dismount" action that will safely unmount an NTFS volume and take it offline (so it won't get remounted)
- a "Shutdown Virtual Machines" action that can take a list of VMWare virtual machines, separated between "Normal Virtual Machines" and "Critical Virtual Machines", and shut them down in the appropriate order (first the "Normal" ones, followed by the "Critical" ones)

All of these actions are configurable in the `AppSettings.json` JSON file (the project uses the .NET hosting infrastructure provided by `Microsoft.Extensions.Hosting`)

To use the project, build the program in `Release` configuration (Release configuration is built as a Windows application and will not spawn a console window like on `Debug`), then add `shutdown.exe` to the Windows task scheduler.

**NOTE**: you must add it with the following settings:

- User account: your primary Windows user
- Run only when user is logged on
- Run with highest privileges

Failing to do this will result in shutdown not being intercepted, and risking data loss due to improper shutdown

## Pre-shutdown mode

The latest commits introduce a new feature called "pre-shutdown".

In this mode, 2 instances of the program will be spawned: one which will run early in the shutdown flow ("pre") and another that will run later, when most UI applications have been already terminated (we can refer to it as "normal").

The purpose of this mode is to kill certain UI applications before they get notified about shutdown, and take a default action.

The currently implemented handlers are:

- automatically close any open `notepad`, `mspaint`, `hxd` instance without saving (useful if you use them as scratchpad)
- automatically save any close any open IDA database.
- try to cleanly close running console applications by sending the Windows equivalent of SIGINT (`CTRL_C_EVENT`) to them

To enable this mode, you can add `-pre` to the command line in the Task Scheduler settings. **You don't need to add a duplicate entry**. The program will spawn a second "normal" instance when running in `-pre` mode.
