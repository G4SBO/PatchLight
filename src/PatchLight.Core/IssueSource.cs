namespace PatchLight.Core;

public interface IIssueSource
{
    Task<IssueSnapshot> GetAsync(string windowsVersion, CancellationToken ct);
}

public sealed class ReleaseHealthSource(HttpClient http, string cacheDir, TimeProvider time) : IIssueSource
{
    public static string PageUrl(string windowsVersion) =>
        $"https://learn.microsoft.com/en-us/windows/release-health/{WindowsRelease.PageSlug(windowsVersion)}";

    public async Task<IssueSnapshot> GetAsync(string windowsVersion, CancellationToken ct)
    {
        var url = PageUrl(windowsVersion);
        var cachePath = Path.Combine(cacheDir, $"release-health-{windowsVersion.ToLowerInvariant()}.html");
        string html;
        IReadOnlyList<KnownIssue> issues;
        try
        {
            using var response = await http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            html = await response.Content.ReadAsStringAsync(ct);
            issues = ReleaseHealthParser.Parse(html, url);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested
            && ex is HttpRequestException or TaskCanceledException or ReleaseHealthFormatException)
        {
            return await LoadSavedCopyAsync(url, cachePath, ct);
        }

        // Saving the copy is best effort: a failed write must not fail a download that succeeded.
        var now = time.GetUtcNow();
        await TrySaveAsync(cacheDir, cachePath, html, now, ct);
        return new IssueSnapshot(issues, now, FromCache: false);
    }

    private static async Task<IssueSnapshot> LoadSavedCopyAsync(string url, string cachePath, CancellationToken ct)
    {
        if (!File.Exists(cachePath))
            throw new PatchLightException(
                "Couldn't reach Microsoft's known issues list, and there is no saved copy yet. " +
                "Check your internet connection and try again.");

        var issues = ReleaseHealthParser.Parse(await File.ReadAllTextAsync(cachePath, ct), url);
        var savedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(cachePath), TimeSpan.Zero);
        return new IssueSnapshot(issues, savedAt, FromCache: true);
    }

    private static async Task TrySaveAsync(
        string cacheDir, string cachePath, string html, DateTimeOffset savedAt, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(cacheDir);
            await File.WriteAllTextAsync(cachePath, html, ct);
            File.SetLastWriteTimeUtc(cachePath, savedAt.UtcDateTime);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
