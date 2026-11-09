using Microsoft.Extensions.Time.Testing;
using PatchLight.Core;

namespace PatchLight.Tests;

// Windows 10 22H2 still gets monthly updates under Extended Security Updates. It has its own known-issues page,
// and both products had a "22H2", so Windows 10 versions carry a "10-" prefix.
public sealed class Windows10Tests : IDisposable
{
    private const string Windows10 = "10-22H2";
    private readonly string dir = Path.Combine(Path.GetTempPath(), "patchlight-win10-" + Guid.NewGuid());
    private readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    private static IReadOnlyList<KnownIssue> Page() => ReleaseHealthParser.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "status-windows-10-22h2.html")),
        ReleaseHealthSource.PageUrl(Windows10));

    [Theory]
    [InlineData(19045, "22H2", "10-22H2")]
    [InlineData(26200, "25H2", "25H2")]
    [InlineData(22631, "23H2", "23H2")]
    public void Picks_the_release_from_the_build_number(int build, string displayVersion, string expected) =>
        Assert.Equal(expected, WindowsRelease.ForPc(build, displayVersion));

    [Theory]
    [InlineData(19045, true)]
    [InlineData(19044, false)] // Windows 10 21H2: no more updates for home PCs
    [InlineData(17763, false)]
    [InlineData(22000, true)]
    [InlineData(26200, true)]
    public void Supports_windows_11_and_windows_10_22h2(int build, bool supported) =>
        Assert.Equal(supported, WindowsRelease.IsSupported(build));

    [Fact]
    public void Windows_10_and_11_22h2_use_different_pages()
    {
        Assert.Equal("https://learn.microsoft.com/en-us/windows/release-health/status-windows-10-22h2",
            ReleaseHealthSource.PageUrl("10-22H2"));
        Assert.Equal("https://learn.microsoft.com/en-us/windows/release-health/status-windows-11-22h2",
            ReleaseHealthSource.PageUrl("22H2"));
        Assert.Equal("https://learn.microsoft.com/en-us/windows/release-health/status-windows-11-25h2",
            ReleaseHealthSource.PageUrl("25H2"));
    }

    [Fact]
    public void Names_the_release()
    {
        Assert.Equal("Windows 10, version 22H2", WindowsRelease.Name("10-22H2"));
        Assert.Equal("Windows 11, version 25H2", WindowsRelease.Name("25H2"));
    }

    [Fact]
    public void Reads_microsofts_windows_10_page()
    {
        var issue = Assert.Single(Page());
        Assert.Equal("USB audio devices might fail to start or produce no sound", issue.Title);
        Assert.Equal("KB5122878", issue.OriginatingKb);
        Assert.Equal(new Version(19045, 7725), issue.OriginatingBuild);
        Assert.Equal(IssueStatus.Mitigated, issue.Status);
        Assert.StartsWith("https://learn.microsoft.com/en-us/windows/release-health/status-windows-10-22h2#", issue.Url);
    }

    [Fact]
    public void A_later_windows_10_update_inherits_an_open_problem()
    {
        var update = new PendingUpdate("id-1", "KB5127700", "2026-10 Security Update (KB5127700) (19045.7800)",
            new Version(19045, 7800), new DateOnly(2026, 10, 1), false);
        var verdict = VerdictEngine.Judge(update, Page(), new DateOnly(2026, 10, 13));
        Assert.Equal(Light.Yellow, verdict.Light);
        Assert.Equal("USB audio devices might fail to start or produce no sound", Assert.Single(verdict.Issues).Title);
    }

    [Fact]
    public async Task Windows_10_pc_reads_its_own_page_and_judges_a_windows_11_offer_against_windows_11()
    {
        var updates = new FakeUpdateClient();
        var issues = new FeatureUpdateTests.VersionedIssueSource(time);
        var service = new PatchLightService(updates, issues, new HoldStore(Path.Combine(dir, "holds.json")), time, Windows10);
        updates.Pending.Add(new PendingUpdate("id-1", "KB5127700", "2026-10 Security Update (KB5127700) (19045.7800)",
            new Version(19045, 7800), new DateOnly(2026, 10, 1), false));
        updates.Pending.Add(FeatureUpdateTests.Upgrade("25H2"));
        issues.ByVersion[Windows10] = Page().ToList();
        issues.ByVersion["25H2"] = [];

        var result = await service.CheckAsync(applyAutoRelease: false, CancellationToken.None);

        Assert.Equal(Light.Yellow, result.Verdicts.Single(v => v.Update.Kb == "KB5127700").Light);
        var upgrade = result.Verdicts.Single(v => v.Update.IsFeatureUpdate);
        Assert.Equal(Light.Green, upgrade.Light);
        Assert.Equal("No known problems in Windows 11, version 25H2.", upgrade.Reason);
    }
}
