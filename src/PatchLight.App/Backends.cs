using PatchLight.Core;
using PatchLight.Windows;

namespace PatchLight.App;

public static class Backends
{
    public static Backend Real()
    {
        var exe = Environment.ProcessPath ?? throw new PatchLightException("Couldn't find PatchLight's own program file.");
        var autostart = Autostart.ForCurrentUser();
        return new Backend
        {
            Service = PatchLightHost.CreateService(Program.RequireSupportedWindows()),
            Actions = new RunAsAdministrator(),
            GetDailyCheckAsync = () => Task.Run(() => DailyTask.VisibleXml() switch
            {
                null => DailyCheckState.Off,
                var xml when !string.Equals(DailyTask.CommandFromXml(xml), exe, StringComparison.OrdinalIgnoreCase) => DailyCheckState.Moved,
                var xml when !DailyTask.IsCurrent(xml) => DailyCheckState.Outdated,
                _ => DailyCheckState.On,
            }),
            GetStartWithWindows = () => autostart.IsEnabled(exe),
            SetStartWithWindows = on =>
            {
                if (on) autostart.Enable(exe);
                else autostart.Disable();
            },
            BackgroundLogPath = PatchLightHost.BackgroundLogPath(SystemInfo.DataDirectory),
            NewsSource = PatchLightHost.CreateNewsSource(),
            GetProtection = () => PatchLightHost.CreateProtectionStore().Load(),
            IsHomeEdition = SystemInfo.IsHomeEdition(),
            DeferManagedElsewhere = () => !SystemInfo.IsHomeEdition() && DeferPolicy.IsManagedElsewhere(
                new WindowsUpdatePolicyValues(), PatchLightHost.CreateProtectionStore().Load().OwnsDeferPolicy),
        };
    }

    public static Backend Demo()
    {
        var (service, actions) = DemoData.Create();
        var startWithWindows = true;
        return new Backend
        {
            Service = service,
            Actions = actions,
            GetDailyCheckAsync = () => Task.FromResult(actions.DailyCheckOn ? DailyCheckState.On : DailyCheckState.Off),
            GetStartWithWindows = () => startWithWindows,
            SetStartWithWindows = on => startWithWindows = on,
            NewsSource = DemoData.News,
            IsDemo = true,
            GetProtection = () => actions.Protection,
        };
    }
}
