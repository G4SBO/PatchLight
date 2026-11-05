using System.Net;
using Microsoft.Extensions.Time.Testing;
using PatchLight.Core;
using static PatchLight.Tests.VerdictEngineTests;

namespace PatchLight.Tests;

public sealed class NewsTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
    private readonly string dir = Path.Combine(Path.GetTempPath(), "patchlight-news-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    // Made-up feed in the shape of a real RSS 2.0 news feed.
    private static string Feed(params (string Title, string Description, string Date)[] items) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0"><channel><title>Example tech news</title>
        {string.Join("\n", items.Select((it, i) => $"""
            <item><title>{it.Title}</title><link>https://news.example/{i}</link>
            <description><![CDATA[{it.Description}]]></description><pubDate>{it.Date}</pubDate></item>
            """))}
        </channel></rss>
        """;

    private const string Day = "Tue, 13 Oct 2026 18:00:00 +0000";

    [Fact]
    public void Reads_articles_and_the_kb_numbers_they_mention()
    {
        var articles = NewsFeedParser.Parse(Feed(("Windows 11 KB5127648 breaks Wi-Fi sign-in", "Some PCs can't connect.", Day)), "Example");

        var article = Assert.Single(articles);
        Assert.Equal("Windows 11 KB5127648 breaks Wi-Fi sign-in", article.Title);
        Assert.Equal("https://news.example/0", article.Url);
        Assert.Equal("Example", article.Source);
        Assert.Equal(["KB5127648"], article.Kbs);
        Assert.True(article.ReportsProblem);
        Assert.Equal(new DateTimeOffset(2026, 10, 13, 18, 0, 0, TimeSpan.Zero), article.Published);
    }

    [Theory]
    [InlineData("Windows 11 KB5127648 breaks Wi-Fi sign-in, but there's a manual fix", true)]
    [InlineData("Microsoft admits a Windows 11 bug is causing stutter in games", true)]
    [InlineData("Windows 11 KB5127648 fixes bugs related to audio", false)]
    [InlineData("Windows 11 KB5127648 is out with major changes", false)]
    [InlineData("I tested the October update: everything new, improved, and fixed", false)]
    [InlineData("Microsoft issues emergency update KB5127700", false)]
    [InlineData("Windows 11 KB5127648 is slowly rolling out", false)]
    [InlineData("KB5127648 addresses File Explorer issues", false)]
    [InlineData("Microsoft releases out-of-band update for KB5127648 bug", false)]
    [InlineData("After KB5127648, some PCs can’t connect to Wi-Fi", true)]
    [InlineData("KB5127648 is causing issues with printers", true)]
    public void Only_problem_reports_count(string title, bool problem) =>
        Assert.Equal(problem, NewsFeedParser.Parse(Feed((title, "KB5127648", Day)), "Example").Single().ReportsProblem);

    [Fact]
    public void Kb_in_the_text_counts_only_for_problem_headlines()
    {
        var articles = NewsFeedParser.Parse(Feed(
            ("Microsoft admits a Windows 11 bug is causing stutter in games", "After installing KB5127648, games stutter.", Day),
            ("Weekly roundup", "This week: KB5127648 and more.", Day)), "Example");

        Assert.Equal(["KB5127648"], NewsMatcher.ReportsFor(articles, Update("KB5127648")).Select(a => a.Kbs.Single()));
        Assert.Single(NewsMatcher.ReportsFor(articles, Update("KB5127648")));
    }

    [Fact]
    public void BleepingComputer_fixture_matches_unresolved_update_problems()
    {
        Assert.Contains(RssNewsSource.DefaultFeeds,
            feed => feed.Name == "BleepingComputer" && feed.Url == "https://www.bleepingcomputer.com/feed/");
        var xml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "bleepingcomputer-feed.xml"));
        var articles = NewsFeedParser.Parse(xml, "BleepingComputer");

        var matches = NewsMatcher.ReportsFor(articles, Update("KB5127648"));

        var article = Assert.Single(matches);
        Assert.Equal("BleepingComputer", article.Source);
        Assert.Equal("Microsoft: October Windows update may break Smart App Control", article.Title);
        Assert.Equal(
            "https://www.bleepingcomputer.com/news/microsoft/kb5127648-windows-security-update-may-break-smart-app-control/",
            article.Url);
    }

    [Theory]
    [InlineData("Best Windows 11 laptop deals this week")]
    [InlineData("Windows 11 KB5127648 review: everything new")]
    [InlineData("Microsoft PowerToys adds CursorHide, Button Lock mouse tools")]
    public void Ignores_deals_reviews_and_other_products(string title)
    {
        var articles = NewsFeedParser.Parse(Feed((title, "KB5127648", Day)), "BleepingComputer");

        Assert.Empty(NewsMatcher.ReportsFor(articles, Update("KB5127648")));
    }

    [Theory]
    [InlineData("Tue, 13 Oct 2026 14:00:00 EDT")]
    [InlineData("Tue, 13 Oct 2026 18:00:00 GMT")]
    [InlineData("Tue, 13 Oct 2026 11:00:00 PDT")]
    public void Reads_dates_with_named_time_zones(string date) =>
        Assert.Equal(new DateTimeOffset(2026, 10, 13, 18, 0, 0, TimeSpan.Zero),
            NewsFeedParser.Parse(Feed(("KB5127648 breaks Wi-Fi", "", date)), "Example").Single().Published.ToUniversalTime());

    [Fact]
    public async Task Article_without_a_readable_date_counts_from_when_it_was_seen()
    {
        var time = new FakeTimeProvider(Now);
        var source = new RssNewsSource(new HttpClient(new StubHandler(_ => Feed(("KB5127648 breaks Wi-Fi", "", "sometime last week")))),
            Path.Combine(dir, "news.json"), time, [("Example", "https://news.example/feed")]);

        Assert.Equal(Now, Assert.Single(await source.GetAsync(CancellationToken.None)).Published);
    }

    [Fact]
    public void Damaged_feed_gives_no_articles() =>
        Assert.Empty(NewsFeedParser.Parse("<rss><channel><item><title>oops", "Example"));

    [Fact]
    public void News_turns_a_safe_update_into_wait_but_never_red()
    {
        var report = new NewsArticle("Example", "KB5127648 breaks Wi-Fi", "https://news.example/0", Now, ["KB5127648"], true);
        var safe = VerdictEngine.Judge(Update(), [], new DateOnly(2026, 10, 13));

        var withNews = VerdictEngine.WithNews(safe, [report]);
        Assert.Equal(Light.Yellow, withNews.Light);
        Assert.Equal("Problems reported in the news, not confirmed by Microsoft yet: KB5127648 breaks Wi-Fi", withNews.Reason);
        Assert.Single(withNews.News);

        var red = VerdictEngine.Judge(Update(), [Issue(IssueStatus.Confirmed, kb: "KB5127648")], new DateOnly(2026, 10, 13));
        var redWithNews = VerdictEngine.WithNews(red, [report]);
        Assert.Equal(Light.Red, redWithNews.Light);
        Assert.Equal(red.Reason, redWithNews.Reason);
        Assert.Single(redWithNews.News);
    }

    [Fact]
    public async Task Source_keeps_articles_for_30_days_and_survives_failures()
    {
        var time = new FakeTimeProvider(Now);
        var store = Path.Combine(dir, "news.json");
        var feed = Feed(("Windows 11 KB5127648 breaks Wi-Fi sign-in", "", Day));

        var source = new RssNewsSource(new HttpClient(new StubHandler(_ => feed)), store, time,
            [("Example", "https://news.example/feed")]);
        Assert.Single(await source.GetAsync(CancellationToken.None));

        // Next time the site is down: the saved article is still there.
        var offline = new RssNewsSource(new HttpClient(new StubHandler(_ => throw new HttpRequestException("down"))), store, time,
            [("Example", "https://news.example/feed")]);
        Assert.Single(await offline.GetAsync(CancellationToken.None));

        // A month and a bit later it's gone.
        time.Advance(TimeSpan.FromDays(31));
        Assert.Empty(await offline.GetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Service_adds_news_to_the_check()
    {
        var time = new FakeTimeProvider(Now);
        var updates = new FakeUpdateClient();
        updates.Pending.Add(Update(released: new DateOnly(2026, 10, 1)));
        var service = new PatchLightService(updates, new FakeIssueSource(time), new HoldStore(Path.Combine(dir, "h.json")), time, "25H2")
        {
            News = new FixedNews([new NewsArticle("Example", "KB5127648 breaks Wi-Fi", "https://news.example/0", Now, ["KB5127648"], true)]),
        };

        var verdict = Assert.Single((await service.CheckAsync(false, CancellationToken.None)).Verdicts);

        Assert.Equal(Light.Yellow, verdict.Light);
        Assert.Single(verdict.News);
    }

    [Fact]
    public async Task Broken_news_never_breaks_the_check()
    {
        var time = new FakeTimeProvider(Now);
        var updates = new FakeUpdateClient();
        updates.Pending.Add(Update(released: new DateOnly(2026, 10, 1)));
        var service = new PatchLightService(updates, new FakeIssueSource(time), new HoldStore(Path.Combine(dir, "h.json")), time, "25H2")
        {
            News = new ThrowingNews(),
        };

        Assert.Equal(Light.Green, Assert.Single((await service.CheckAsync(false, CancellationToken.None)).Verdicts).Light);
    }

    [Fact]
    public void Reads_kb_numbers_from_the_full_article_text_too()
    {
        const string xml = """
            <rss version="2.0" xmlns:content="http://purl.org/rss/1.0/modules/content/"><channel>
            <item><title>Microsoft admits a Windows 11 bug is causing stutter in games</title><link>https://news.example/1</link>
            <description>Short teaser.</description>
            <content:encoded><![CDATA[<p>After installing <b>KB5127648</b>, some games stutter.</p>]]></content:encoded>
            <pubDate>Tue, 13 Oct 2026 18:00:00 +0000</pubDate></item>
            </channel></rss>
            """;
        Assert.Equal(["KB5127648"], Assert.Single(NewsFeedParser.Parse(xml, "Example")).Kbs);
    }

    private sealed class FixedNews(IReadOnlyList<NewsArticle> articles) : INewsSource
    {
        public Task<IReadOnlyList<NewsArticle>> GetAsync(CancellationToken ct) => Task.FromResult(articles);
    }

    private sealed class ThrowingNews : INewsSource
    {
        public Task<IReadOnlyList<NewsArticle>> GetAsync(CancellationToken ct) => throw new InvalidOperationException("boom");
    }

    private sealed class StubHandler(Func<HttpRequestMessage, string> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(respond(request)) });
    }
}
