using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace PatchLight.Core;

// A tech-news article that mentions Windows updates. ReportsProblem: the headline says something broke.
public sealed record NewsArticle(
    string Source,
    string Title,
    string Url,
    DateTimeOffset Published,
    IReadOnlyList<string> Kbs,
    bool ReportsProblem);

public interface INewsSource
{
    Task<IReadOnlyList<NewsArticle>> GetAsync(CancellationToken ct);
}

// Reads a public RSS 2.0 feed. Problems often show up in the news days before Microsoft lists them.
public static partial class NewsFeedParser
{
    [GeneratedRegex(@"KB\d{6,8}(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex Kb();

    // Words that say something broke. Bare "issues", "slow" and "fails" are left out: "Microsoft issues an update",
    // "slowly rolling out" and "addresses issues" are good news.
    [GeneratedRegex(@"\b(break|breaks|broke|broken|bugs?|buggy|fails to|failing|crash\w*|stutter\w*|errors?|black screen|BSOD|" +
        @"blue screen|freez\w*|won't|can't|cannot|unable to|not working|stops? working|causing (issues|problems)|" +
        @"(issues|problems) with)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Problem();

    // "KB5127648 fixes bugs", "addresses issues", "emergency update for the bug": good news.
    [GeneratedRegex(@"\b(fixes|fixed|fix for|resolves|resolved|addresses|patch(es|ed)?|out-of-band|emergency update)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex Fixes();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tags();

    // RSS dates may end in a named US zone ("EDT"), which .NET doesn't read.
    private static readonly Dictionary<string, string> Zones = new(StringComparer.OrdinalIgnoreCase)
    {
        ["EDT"] = "-04:00", ["EST"] = "-05:00", ["CDT"] = "-05:00", ["CST"] = "-06:00",
        ["MDT"] = "-06:00", ["MST"] = "-07:00", ["PDT"] = "-07:00", ["PST"] = "-08:00",
        ["GMT"] = "+00:00", ["UT"] = "+00:00", ["UTC"] = "+00:00", ["Z"] = "+00:00",
    };

    private static DateTimeOffset? ParseDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        var space = text.LastIndexOf(' ');
        if (space > 0 && Zones.TryGetValue(text[(space + 1)..], out var offset)) text = text[..space] + " " + offset;
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            ? date : null;
    }

    public static IReadOnlyList<NewsArticle> Parse(string xml, string source)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (XmlException)
        {
            return [];
        }

        var articles = new List<NewsArticle>();
        foreach (var item in document.Descendants("item"))
        {
            // Curly apostrophes ("can’t") are common in feeds.
            var title = WebUtility.HtmlDecode(item.Element("title")?.Value ?? "").Replace('\u2019', '\'').Trim();
            var url = item.Element("link")?.Value.Trim() ?? "";
            if (title.Length == 0 || !Uri.TryCreate(url, UriKind.Absolute, out _)) continue;

            // Some sites name the KB only in the full article, not in the short description.
            var full = item.Element(XNamespace.Get("http://purl.org/rss/1.0/modules/content/") + "encoded")?.Value ?? "";
            var description = WebUtility.HtmlDecode(Tags().Replace($"{item.Element("description")?.Value} {full}", " "));
            var published = ParseDate(item.Element("pubDate")?.Value) ?? DateTimeOffset.MinValue;
            var kbs = Kb().Matches($"{title} {description}").Select(m => m.Value.ToUpperInvariant()).Distinct().ToList();

            articles.Add(new NewsArticle(source, title, url, published, kbs,
                ReportsProblem: Problem().IsMatch(title) && !Fixes().IsMatch(title)));
        }
        return articles;
    }
}

public static class NewsMatcher
{
    public static IReadOnlyList<NewsArticle> ReportsFor(IReadOnlyList<NewsArticle> articles, PendingUpdate update) =>
        update.Kb is null ? []
        : articles.Where(a => a.ReportsProblem && a.Kbs.Contains(update.Kb))
            .DistinctBy(a => a.Url)
            .OrderByDescending(a => a.Published)
            .ToList();
}

// Downloads the feeds and keeps problem reports for 30 days, because feeds only list the last few days.
// A site that can't be reached just means fewer news; it never fails a check.
public sealed class RssNewsSource(HttpClient http, string storePath, TimeProvider time,
    IReadOnlyList<(string Name, string Url)> feeds) : INewsSource
{
    public static IReadOnlyList<(string Name, string Url)> DefaultFeeds { get; } =
    [
        ("Neowin", "https://www.neowin.net/news/rss/"),
        ("Windows Latest", "https://www.windowslatest.com/feed/"),
        ("BleepingComputer", "https://www.bleepingcomputer.com/feed/"),
    ];

    private static readonly TimeSpan KeepFor = TimeSpan.FromDays(30);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public async Task<IReadOnlyList<NewsArticle>> GetAsync(CancellationToken ct)
    {
        var articles = Load();
        var now = time.GetUtcNow();
        // All feeds at once, so a slow site doesn't add up.
        foreach (var fetched in await Task.WhenAll(feeds.Select(feed => FetchAsync(feed.Name, feed.Url, ct))))
        {
            articles.AddRange(fetched
                .Where(a => a.ReportsProblem && a.Kbs.Count > 0 && articles.All(saved => saved.Url != a.Url))
                // An article without a readable date counts from when PatchLight first saw it.
                .Select(a => a.Published == DateTimeOffset.MinValue ? a with { Published = now } : a)
                .ToList());
        }

        var cutoff = now - KeepFor;
        var kept = articles.Where(a => a.Published >= cutoff).DistinctBy(a => a.Url).ToList();
        Save(kept);
        return kept;
    }

    private async Task<IReadOnlyList<NewsArticle>> FetchAsync(string name, string url, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            return NewsFeedParser.Parse(await http.GetStringAsync(url, timeout.Token), name);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return [];
        }
    }

    private List<NewsArticle> Load()
    {
        try
        {
            return File.Exists(storePath)
                ? JsonSerializer.Deserialize<List<NewsArticle>>(File.ReadAllText(storePath), Json) ?? []
                : [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private void Save(List<NewsArticle> articles)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(storePath)!);
            File.WriteAllText(storePath, JsonSerializer.Serialize(articles, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
