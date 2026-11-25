using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using PotatoVN.App.PluginBase.Guides;

var tests = new (string Name, Func<Task> Run)[]
{
    ("2DFan tries original title and ranks exact matches", SearchOriginalTitleAsync),
    ("Moon archive and article parsing", ReadMoonAsync),
    ("No results, layout changes, and network errors remain distinct", DistinguishFailuresAsync),
    ("Automatic fallback and explicit source selection", FallbackAsync),
    ("Local mount preference is opt-in", () => RunSync(LocalDefaultSelection)),
    ("Article ID remains usable after mirror change", MirrorAsync),
    ("Legacy links, independent sources, and JSON round trip", () => RunSync(History)),
    ("Paragraph bookmarks survive insertions and find ignores case", () => RunSync(Bookmarks)),
    ("Local text supports Unicode and legacy encodings", () => RunSync(LocalEncodings)),
    ("Local discovery favors remembered files and mounts survive folder moves", LocalDiscoveryAsync),
    ("Local reader rejects binary, empty and oversized input", InvalidLocalFilesAsync),
    ("Caller cancellation interrupts the response body", CancelBodyAsync),
    ("Stalled response body times out", TimeoutBodyAsync),
};
await Task.WhenAll(tests.Select(async test =>
{
    await test.Run();
    Console.WriteLine($"PASS {test.Name}");
}));
Console.WriteLine($"{tests.Length} checks passed.");

static async Task SearchOriginalTitleAsync()
{
    var queries = new List<string>();
    using var http = new HttpClient(new Handler(uri =>
    {
        string query = Uri.UnescapeDataString(uri.Query);
        queries.Add(query);
        return Fixture(query.Contains("Yakou", StringComparison.Ordinal) ? "df2an-results.html" : "df2an-empty.html");
    }));
    var source = new Df2anGuideSource(new GuidePageClient(http), () => Df2anGuideSource.DefaultDomain, _ => { });
    var result = await source.SearchAsync(new GuideGame("夜光故事", "Yakou Story", "夜光故事", null), default);
    Check(queries.SequenceEqual(["?keyword=夜光故事", "?keyword=Yakou Story"]), "Expected two distinct search requests.");
    Check(result.Value?.Select(item => item.Id).SequenceEqual(["17", "42"]) == true,
        "Expected the exact normalized match first, with duplicate and foreign links removed.");
}

static async Task ReadMoonAsync()
{
    using var http = new HttpClient(new Handler(uri =>
        Fixture(uri.AbsolutePath == "/ga77" ? "ymgal-articles.html" : "ymgal-article.html")));
    var source = new YmgalGuideSource(new GuidePageClient(http));
    var missing = await source.SearchAsync(new GuideGame("夜光故事", null, null, "invalid"), default);
    Check(missing.Issue == GuideIssue.MissingGameId, "Invalid archive ID should allow manual browser search.");
    var list = await source.SearchAsync(new GuideGame("夜光故事", null, null, "77"), default);
    Check(list.Value is [{ Id: "65" }], "Expected archive article ID.");
    var article = await source.ReadArticleAsync("65", "fallback", default);
    Check(article.Value?.Text == "先完成共同线。\n路线 B：走到码头。", "Expected selectable article paragraphs.");
}

static async Task DistinguishFailuresAsync()
{
    using var http = new HttpClient(new Handler(uri =>
        uri.AbsolutePath switch
        {
            "/ga1" => Fixture("layout-changed.html"),
            "/ga2" => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            _ => Fixture("df2an-empty.html"),
        }));
    var source = new YmgalGuideSource(new GuidePageClient(http));
    Task<GuideResult<IReadOnlyList<GuideLink>>> Search(string id) =>
        source.SearchAsync(new GuideGame("夜光故事", null, null, id), default);
    Check((await Search("1")).Issue == GuideIssue.LayoutChanged, "Unknown markup must not masquerade as an empty catalog.");
    Check((await Search("2")).Issue == GuideIssue.Unavailable, "HTTP failure must be shown as unavailable.");
    Check((await Search("3")).Issue == GuideIssue.NoResults, "Recognized empty page must be shown as no result.");
}

static async Task FallbackAsync()
{
    var calls = new List<GuideSource>();
    var address = new Uri("https://example.invalid/");
    Task<GuideResult<IReadOnlyList<GuideLink>>> Search(GuideSource source, CancellationToken token)
    {
        calls.Add(source);
        return Task.FromResult(source == GuideSource.Ymgal
            ? GuideResult<IReadOnlyList<GuideLink>>.Failed(GuideIssue.NoResults, address)
            : GuideResult<IReadOnlyList<GuideLink>>.Found([new("42", "夜光故事")], address));
    }
    var automatic = await GuideSearch.FindAsync(GuideSource.Ymgal, true, Search, default);
    Check(automatic.Source == GuideSource.Df2an && automatic.Other?.Issue == GuideIssue.NoResults &&
        calls.SequenceEqual([GuideSource.Ymgal, GuideSource.Df2an]), "Automatic should try the second source on no result.");
    calls.Clear();
    var manual = await GuideSearch.FindAsync(GuideSource.Ymgal, false, Search, default);
    Check(manual.Result.Issue == GuideIssue.NoResults && calls.SequenceEqual([GuideSource.Ymgal]),
        "Manual source must not silently switch.");
    var bothFailed = await GuideSearch.FindAsync(GuideSource.Df2an, true, (site, _) => Task.FromResult(
        GuideResult<IReadOnlyList<GuideLink>>.Failed(site == GuideSource.Df2an
            ? GuideIssue.Unavailable : GuideIssue.MissingGameId, address)), default);
    Check(bothFailed.Source == GuideSource.Df2an, "Keep the first browser entry when the secondary archive ID is missing.");
}

static void LocalDefaultSelection()
{
    Check(GuideSearch.InitialSource("auto", true) is null, "A mount should not affect online automatic selection.");
    Check(GuideSearch.InitialSource("local-first", true) == GuideSource.Local, "Local-first should start on a mounted guide.");
    Check(GuideSearch.InitialSource("local-first", false) is null, "Local-first should use online automatic if nothing is mounted.");
    Check(GuideSearch.InitialSource("2dfan", true) == GuideSource.Df2an &&
        GuideSearch.InitialSource("ymgal", true) == GuideSource.Ymgal, "An explicit online source must be preserved.");
}

static async Task MirrorAsync()
{
    string domain = Df2anGuideSource.DefaultDomain;
    using var http = new HttpClient(new Handler(uri =>
    {
        if (uri.Host != "2dfdf.de") return new HttpResponseMessage(HttpStatusCode.Forbidden);
        return Fixture(uri.AbsolutePath == "/topics/42" ? "df2an-topic.html" : "df2an-empty.html");
    }));
    var source = new Df2anGuideSource(new GuidePageClient(http), () => domain, mirror => domain = mirror);
    var article = await source.ReadArticleAsync("42", "fallback", default);
    Check(domain == "https://2dfdf.de" && article.BrowserUrl?.AbsolutePath == "/topics/42",
        "Expected the same article ID on a working mirror.");
    Check(article.Value?.Text == "共同线 起始选项\n路线 A\n前往广场\n章节　选项", "Unexpected article text.");
}

static void History()
{
    var game = Guid.NewGuid();
    var invalid = Guid.NewGuid();
    var saved = new ConcurrentDictionary<Guid, GameGuideHistory>();
    var legacy = new Dictionary<Guid, string>
    {
        [game] = "https://2dfmax.top/topics/0042/?legacy=1",
        [invalid] = "https://2dfan.com/subjects/42",
    };
    GuideHistory.Migrate(saved, legacy);
    Check(!saved.ContainsKey(invalid) && saved[game].Df2an?.Article.Id == "42", "Expected only valid legacy topics.");
    var moon = new GuideReading(new GuideReference(GuideSource.Ymgal, "65"), "月幕攻略");
    GuideHistory.Remember(saved, game, moon);
    GuideHistory.Migrate(saved, new Dictionary<Guid, string> { [game] = "/topics/77" });
    Check(saved[game].Df2an?.Article.Id == "42" && saved[game].LastSource == GuideSource.Ymgal,
        "A restored legacy file must not replace a current choice.");
    var position = new GuideBookmark("key", 2, .52);
    Check(GuideHistory.RememberPosition(saved, game, moon.Article, position), "Expected an updated bookmark.");
    var roundTrip = JsonSerializer.Deserialize<ConcurrentDictionary<Guid, GameGuideHistory>>(JsonSerializer.Serialize(saved));
    Check(roundTrip?[game].Ymgal?.Bookmark == position && roundTrip[game].Df2an?.Article.Id == "42",
        "Both sources should survive serialization.");
    Check(GuideSearch.Preferred(null, saved[game], new GuideGame(null, null, null, null)) == GuideSource.Ymgal,
        "Auto should resume the last successfully opened article.");
    GuideHistory.Remember(saved, game, moon with { Article = moon.Article with { Id = "70" } });
    Check(!GuideHistory.RememberPosition(saved, game, moon.Article, position), "An old reader must not update a newer article.");
    saved.Clear();
    Check(!GuideHistory.RememberPosition(saved, game, moon.Article, position), "Scrolling must not recreate cleared history.");
}

static void Bookmarks()
{
    var original = new GuideText("路线 A\n存档点\n路线 B");
    GuideBookmark position = original.Bookmark(1, .52);
    var changed = new GuideText("更新记录\n路线 A\n存档点\n路线 B");
    Check(changed.FindParagraph(position) == 2, "Expected a paragraph anchor after an insertion.");
    Check(new GuideText("路线 A").FindParagraph(position) == -1, "Deleted content should use the saved fraction.");
    Check(changed.Find("路线 a").SequenceEqual([5]), "Expected case-insensitive matching with UTF-16 offsets.");
}

static void LocalEncodings()
{
    const string chinese = "路线一：从学校开始。";
    const string japanese = "攻略：まずここからはじめる。";
    const string traditional = "路線一：從學校開始。";
    byte[] CodePage(int page, string text) =>
        CodePagesEncodingProvider.Instance.GetEncoding(page)!.GetBytes(text);
    byte[] WithBom(Encoding encoding, string text) => encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray();

    Check(LocalGuideFile.Decode(WithBom(Encoding.UTF8, chinese), LocalGuideEncoding.Auto) == chinese, "UTF-8 BOM failed.");
    Check(LocalGuideFile.Decode(WithBom(Encoding.Unicode, japanese), LocalGuideEncoding.Auto) == japanese, "UTF-16 LE failed.");
    Check(LocalGuideFile.Decode(WithBom(Encoding.BigEndianUnicode, chinese), LocalGuideEncoding.Auto) == chinese, "UTF-16 BE failed.");
    Check(LocalGuideFile.Decode(WithBom(new UTF32Encoding(true, true), japanese), LocalGuideEncoding.Auto) == japanese, "UTF-32 BE failed.");
    Check(LocalGuideFile.Decode(Encoding.Unicode.GetBytes("Route A\r\n攻略"), LocalGuideEncoding.Auto) == "Route A\r\n攻略",
        "UTF-16 without a BOM failed.");
    Check(LocalGuideFile.Decode(CodePage(54936, chinese), LocalGuideEncoding.Auto) == chinese, "GB18030 failed.");
    Check(LocalGuideFile.Decode(CodePage(932, japanese), LocalGuideEncoding.Auto) == japanese, "Shift-JIS failed.");
    Check(LocalGuideFile.Decode(CodePage(950, traditional), LocalGuideEncoding.Big5) == traditional, "Manual Big5 failed.");
    Check(LocalGuideFile.Decode(CodePage(51932, japanese), LocalGuideEncoding.EucJp) == japanese, "Manual EUC-JP failed.");
    Check(LocalGuideFile.Decode([0xB3, 0x1A], LocalGuideEncoding.Dos437) == "│", "Legacy NFO decoding failed.");
}

static async Task LocalDiscoveryAsync()
{
    string root = Directory.CreateTempSubdirectory("pvn-guide-").FullName;
    try
    {
        string game = Directory.CreateDirectory(Path.Combine(root, "game")).FullName;
        string elsewhere = Directory.CreateDirectory(Path.Combine(root, "outside")).FullName;
        string remembered = Path.Combine(elsewhere, "已选攻略.md");
        string route = Path.Combine(game, "路线.txt");
        string readme = Path.Combine(game, "Readme.txt");
        foreach (string path in new[] { remembered, route, readme, Path.Combine(game, "收藏.pdf") })
            await File.WriteAllTextAsync(path, "路线一\r\n回到学校。");

        var candidates = LocalGuideFile.Discover(game, remembered, default);
        Check(candidates.Select(file => file.Path).SequenceEqual([remembered, route, readme]) &&
            candidates[0].RememberedByHost, "Expected remembered file first, followed by guide filenames.");
        Check(LocalGuideFile.Discover(game, "路线.txt", default).Count == 2, "Candidate list must deduplicate host text.");
        var mount = LocalGuideFile.Mount(route, game, LocalGuideEncoding.Auto);
        var moved = Path.Combine(root, "moved");
        Directory.Move(game, moved);
        string? relocated = LocalGuideFile.Resolve(mount, moved);
        Check(mount.InGameFolder && relocated == Path.Combine(moved, "路线.txt"), "Moving the game should retain its local file.");
        Check(await LocalGuideFile.ReadAsync(relocated!, mount.Encoding, default) == "路线一\n回到学校。",
            "Local reader should preserve normalized lines.");
        var external = LocalGuideFile.Mount(remembered, moved, LocalGuideEncoding.Auto);
        Check(!external.InGameFolder && LocalGuideFile.Resolve(external, moved) == remembered,
            "External files should retain their absolute path.");
        Check(LocalGuideFile.Resolve(mount with { Path = Path.Combine("..", "outside", "已选攻略.md") }, moved) is null,
            "A relative mount should stay in its game folder.");
        Check(JsonSerializer.Deserialize<LocalGuideMount>(JsonSerializer.Serialize(mount)) == mount,
            "A local mount should round-trip.");
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static async Task InvalidLocalFilesAsync()
{
    string root = Directory.CreateTempSubdirectory("pvn-invalid-").FullName;
    try
    {
        string archive = Path.Combine(root, "archive.txt");
        await File.WriteAllBytesAsync(archive, [0x50, 0x4B, 0x03, 0x04, 0]);
        await CheckInvalid(archive);
        string pdf = Path.Combine(root, "document.txt");
        await File.WriteAllTextAsync(pdf, "%PDF-1.7");
        await CheckInvalid(pdf);
        string empty = Path.Combine(root, "empty.txt");
        await File.WriteAllTextAsync(empty, " \r\n\t");
        await CheckInvalid(empty);
        string large = Path.Combine(root, "large.txt");
        using (var stream = File.Create(large)) stream.SetLength(4 * 1024 * 1024 + 1);
        await CheckInvalid(large);
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }

    static async Task CheckInvalid(string path)
    {
        try
        {
            await LocalGuideFile.ReadAsync(path, LocalGuideEncoding.Auto, default);
            throw new InvalidOperationException($"Unexpected accepted file: {Path.GetFileName(path)}");
        }
        catch (InvalidDataException) { }
    }
}

static async Task CancelBodyAsync()
{
    using var http = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new StalledStream()) }));
    using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
    try
    {
        await new GuidePageClient(http).GetAsync(new Uri("https://example.invalid/cancel"), cancellation.Token)
            .WaitAsync(TimeSpan.FromSeconds(3));
        throw new InvalidOperationException("Body read ignored cancellation.");
    }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
}

static async Task TimeoutBodyAsync()
{
    using var http = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new StalledStream()) }));
    try
    {
        await new GuidePageClient(http).GetAsync(new Uri("https://example.invalid/stall"), default)
            .WaitAsync(TimeSpan.FromSeconds(25));
        throw new InvalidOperationException("Body read failed to time out.");
    }
    catch (HttpRequestException error) when (error.Message == "请求超时") { }
}

static HttpResponseMessage Fixture(string filename) => new(HttpStatusCode.OK)
{
    Content = new StringContent(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", filename)),
        Encoding.UTF8, "text/html"),
};

static Task RunSync(Action check)
{
    check();
    return Task.CompletedTask;
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class Handler(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(respond(request.RequestUri!));
    }
}

sealed class StalledStream : MemoryStream
{
    public override bool CanSeek => false;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    {
        await Task.Delay(Timeout.Infinite, token);
        return 0;
    }
}
