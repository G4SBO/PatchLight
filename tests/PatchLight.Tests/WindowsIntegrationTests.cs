using PatchLight.Core;
using PatchLight.Windows;

namespace PatchLight.Tests;

// Talks to the real Windows Update on this PC. Read-only. Skip with: dotnet test --filter Category!=Integration
[Trait("Category", "Integration")]
public class WindowsIntegrationTests
{
    [Fact]
    public void Detects_a_supported_windows_version()
    {
        var info = SystemInfo.Read();
        Assert.True(info.IsSupported);
        Assert.Matches(@"^\d\dH\d$", info.DisplayVersion!);
        // ProductName says "Windows 10" on Windows 11 too, so the release must come from the build number.
        Assert.Equal(info.Build.Major < 22000, WindowsRelease.IsWindows10(info.Release));
    }

    [Fact]
    public void Can_tell_whether_this_is_a_work_pc() => _ = SystemInfo.IsWorkPc();

    [Fact]
    public void Data_lives_in_program_data() =>
        Assert.EndsWith(@"ProgramData\PatchLight", SystemInfo.DataDirectory, StringComparison.OrdinalIgnoreCase);

    [Fact]
    public async Task Lists_pending_updates_with_kb_and_build()
    {
        var pending = await new WindowsUpdateClient().GetPendingAsync(CancellationToken.None);
        Assert.All(pending, u => Assert.False(string.IsNullOrWhiteSpace(u.UpdateId)));
        Assert.All(pending.Where(u => u.Kb is not null), u => Assert.StartsWith("KB", u.Kb));
    }

    [Fact]
    public async Task Reads_install_history()
    {
        var history = await new WindowsUpdateClient().GetHistoryAsync(50, CancellationToken.None);
        Assert.NotEmpty(history);
    }
}
