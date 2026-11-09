using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using PatchLight.Core;
using PatchLight.Windows;

namespace PatchLight.App;

public static class Program
{
    private const string InstanceName = "PatchLight.App.Instance";
    private const string ShowSignalName = "PatchLight.App.Show";

    // Run by the uninstaller (as administrator) before it removes the files.
    public const string UninstallArgument = "--uninstall-cleanup";

    [STAThread]
    public static int Main(string[] args)
    {
        // Headless modes: the daily task (as SYSTEM) and the "Run as administrator" helper.
        if (args.Contains(DailyTask.Argument)) return RunBackgroundCheck();
        if (args.Contains(UninstallArgument)) return RunUninstallCleanup();
        if (args.Contains(DailyTask.SetupArgument)) return RunSetupDailyCheck();
        if (ElevatedCommand.Parse(args) is { } command) return RunElevated(command);

        var demo = args.Contains("--demo");
        using var instance = new Mutex(initiallyOwned: true, demo ? InstanceName + ".Demo" : InstanceName, out var isFirst);
        using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, demo ? ShowSignalName + ".Demo" : ShowSignalName);
        if (!isFirst)
        {
            // Already running (probably in the tray): bring that window forward instead.
            showSignal.Set();
            return 0;
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(callback =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App(new AppOptions(StartHidden: args.Contains("--tray"), Demo: demo, ShowSignal: showSignal, Instance: instance));
        });
        return 0;
    }

    private static int RunBackgroundCheck()
    {
        var logPath = PatchLightHost.BackgroundLogPath(SystemInfo.DataDirectory);
        try
        {
            DataFolder.Secure(SystemInfo.DataDirectory);
            var service = PatchLightHost.CreateService(RequireSupportedWindows());
            BackgroundCheck.RunAsync(service, logPath, TimeProvider.System, CancellationToken.None,
                PatchLightHost.CreateProtectionStore(), waitWithHolds: SystemInfo.IsHomeEdition()).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // Couldn't even start the check; still leave a note for the app.
            var note = new BackgroundCheckLog(DateTimeOffset.UtcNow, [],
                ex is PatchLightException ? ex.Message : $"Something went wrong: {ex.GetType().Name}.");
            try
            {
                Directory.CreateDirectory(SystemInfo.DataDirectory);
                File.WriteAllText(logPath, System.Text.Json.JsonSerializer.Serialize(note));
            }
            catch (Exception)
            {
            }
        }
        return 0;
    }

    // Run by the installer: the automatic check is on from the start (automatic pausing needs it), and an older
    // once-a-day task becomes the hourly one.
    private static int RunSetupDailyCheck()
    {
        try
        {
            DataFolder.Secure(SystemInfo.DataDirectory);
            DailyTask.EnsureRegistered(Environment.ProcessPath ?? throw new PatchLightException("Couldn't find PatchLight's own program file."));
            return 0;
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex);
            return 1;
        }
    }

    // Each step is best effort: uninstalling must always finish, and every step that can run should.
    private static int RunUninstallCleanup()
    {
        try
        {
            // Nothing PatchLight paused may stay hidden once it's gone.
            PatchLightHost.CreateService(RequireSupportedWindows()).ReleaseAllAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex);
        }
        // Windows' deferral policy stays only if PatchLight didn't write it.
        Try(() =>
        {
            if (PatchLightHost.CreateProtectionStore().Load().OwnsDeferPolicy)
                DeferPolicy.Apply(new WindowsUpdatePolicyValues(), 0, owns: true);
        });
        Try(DailyTask.Unregister);
        Try(() => Autostart.ForCurrentUser().Disable());
        Try(() =>
        {
            if (Directory.Exists(SystemInfo.DataDirectory)) Directory.Delete(SystemInfo.DataDirectory, recursive: true);
        });
        return 0;

        static void Try(Action step)
        {
            try
            {
                step();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                ErrorLog.Write(ex);
            }
        }
    }

    private static int RunElevated(ElevatedCommand command)
    {
        IElevatedActions actions;
        try
        {
            DataFolder.Secure(SystemInfo.DataDirectory);
            actions = new ElevatedActions(PatchLightHost.CreateService(RequireSupportedWindows()),
                Environment.ProcessPath ?? throw new PatchLightException("Couldn't find PatchLight's own program file."),
                PatchLightHost.CreateProtectionStore(), new WindowsUpdatePolicyValues(), SystemInfo.IsHomeEdition());
        }
        catch (Exception ex)
        {
            try
            {
                File.WriteAllText(command.ResultPath, ex is PatchLightException ? ex.Message : "Something went wrong.");
            }
            catch (Exception)
            {
            }
            return 2;
        }
        return command.RunAsync(actions, CancellationToken.None).GetAwaiter().GetResult();
    }

    public static string RequireSupportedWindows()
    {
        var windows = SystemInfo.Read();
        if (!windows.IsSupported) throw new PatchLightException(WindowsInfo.NotSupportedMessage);
        return windows.Release;
    }
}

// Instance: held while PatchLight runs; let go early when an update starts, so the installer doesn't wait for it.
public sealed record AppOptions(bool StartHidden, bool Demo, EventWaitHandle ShowSignal, Mutex Instance);
