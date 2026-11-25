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
    ("Aggregate lookup starts selected sources and isolates failures", AggregateAsync),
    ("Source connectivity checks report complete and partial availability", GuideConnectionsAsync),
    ("Aggregate rankings prioritize matching guides and limit repeated sites", () => RunSync(AggregateRanking)),
    ("2DFan aggregate reads guides from exact search results", Df2anAggregateAsync),
    ("Moon searches exact game titles without an archive ID", MoonNameAsync),
    ("Steam public guides are validated against the game's app ID", () => RunSync(SteamPublicGuides)),
    ("Local mount preference is opt-in", () => RunSync(LocalDefaultSelection)),
    ("CnGal resolves exact game and lists only related guides", CngalAsync),
    ("Seiya resolves original titles and platform variants from Shift-JIS catalogs", SeiyaAsync),
    ("Saved web links route supported articles and Steam needs an App ID", () => RunSync(WebLinks)),
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
    var missing = await source.SearchAsync(new GuideGame(null, null, null, "invalid"), default);
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

static async Task AggregateAsync()
{
    var address = new Uri("https://example.invalid/");
    var started = new ConcurrentDictionary<GuideSource, byte>();
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var lookup = GuideAggregation.SearchAsync(null, includeSteam: true, async (site, token) =>
    {
        started[site] = 0;
        await release.Task.WaitAsync(token);
        if (site == GuideSource.Ymgal) throw new HttpRequestException("test offline");
        return new GuideSiteResults(site, [new GuideLink(site.ToString(), "夜光故事攻略")], GuideIssue.None, address);
    }, (_, _) => { }, default);
    Check(started.Count == 5 && !lookup.IsCompleted, "All enabled sources must start before any finishes.");
    release.SetResult();
    var sites = await lookup;
    Check(sites.Length == 5 && sites.Single(site => site.Source == GuideSource.Ymgal).Issue == GuideIssue.Unavailable &&
        sites.Where(site => site.Source != GuideSource.Ymgal).All(site => site.Links.Count == 1),
        "An unavailable source must preserve the other platforms' results.");
    var withoutSteam = await GuideAggregation.SearchAsync(null, false,
        (site, _) => Task.FromResult(new GuideSiteResults(site, [], GuideIssue.NoResults, address)), (_, _) => { }, default);
    Check(withoutSteam.Length == 4 && withoutSteam.All(site => site.Source != GuideSource.Steam), "Steam needs an App ID.");

    GuideSource[] selected = [GuideSource.Cngal, GuideSource.Seiya, GuideSource.Cngal, GuideSource.Local];
    var custom = await GuideAggregation.SearchAsync(selected, true,
        (site, _) =>
        {
            Check(site is GuideSource.Cngal or GuideSource.Seiya, "An unchecked site must not be queried.");
            return Task.FromResult(new GuideSiteResults(site, [], GuideIssue.NoResults, address));
        }, (_, error) => throw new InvalidOperationException("Unexpected source query.", error), default);
    Check(custom.Select(site => site.Source).SequenceEqual([GuideSource.Cngal, GuideSource.Seiya]),
        "Use only selected online sites, ignoring duplicates and non-online entries.");
    var empty = await GuideAggregation.SearchAsync([], true,
        (_, _) => throw new InvalidOperationException("Expected no online requests."), (_, error) => throw error, default);
    Check(empty.Length == 0, "An explicitly empty selection stays empty.");
    Check(GuideAggregation.NormalizeSources(null).SequenceEqual(GuideAggregation.OnlineSources),
        "A missing selection in older settings keeps all online sources enabled.");

    using var cancel = new CancellationTokenSource();
    var pending = GuideAggregation.SearchAsync(null, true, async (site, token) =>
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        return new GuideSiteResults(site, [], GuideIssue.None, address);
    }, (_, _) => { }, cancel.Token);
    cancel.Cancel();
    try
    {
        await pending;
        throw new InvalidOperationException("Expected canceled aggregated search.");
    }
    catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
}

static async Task GuideConnectionsAsync()
{
    var down = new Uri("https://example.invalid/down");
    var up = new Uri("https://example.invalid/up");
    using var http = new HttpClient(new Handler(uri =>
        new HttpResponseMessage(uri == up ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable)));
    var pages = new GuidePageClient(http);
    Check(await pages.ProbeEndpointsAsync([up], default) == GuideConnection.Connected,
        "A working site should be reported as reachable.");
    Check(await pages.ProbeEndpointsAsync([up, down], default) == GuideConnection.Partial,
        "A site with two endpoints can be partly reachable.");
    Check(await pages.ProbeEndpointsAsync([down], default) == GuideConnection.Unavailable,
        "Failed endpoints should be reported as unavailable.");
    using var cancel = new CancellationTokenSource();
    cancel.Cancel();
    try
    {
        await pages.ProbeEndpointsAsync([up], cancel.Token);
        throw new InvalidOperationException("Expected canceled connectivity check.");
    }
    catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
}

static void AggregateRanking()
{
    var address = new Uri("https://example.invalid/");
    var ranked = GuideAggregation.Rank(new("夜光故事", "Yakou Story", "夜光故事", null, SteamId: "324160"),
    [
        new(GuideSource.Df2an, [
            new("intro", "夜光故事评测"),
            new("route", "第三章"),
            new("guide", "夜光故事全路线攻略"),
            new("guide", "重复链接"),
            new("guide2", "夜光故事全结局"),
            new("guide3", "夜光故事全成就"),
            new("guide4", "夜光故事全流程攻略"),
        ], GuideIssue.None, address),
        new(GuideSource.Seiya, [new("console", "Yakou Story Switch版"), new("jp", "Yakou Story Steam版")], GuideIssue.None, address),
        new(GuideSource.Steam, [new("patch", "Yakou Story translation patch"), new("steam", "Yakou Story Achievement Guide")],
            GuideIssue.None, address),
    ]);
    Check(ranked.Take(3).Select(result => result.Link.Id).SequenceEqual(["guide", "jp", "steam"]),
        "Match game titles and guide intent and use better per-site results to break ties.");
    Check(ranked.Count(result => result.Link.Id == "guide") == 1 &&
        ranked.All(result => result.Link.Id is not ("intro" or "patch")) &&
        ranked[^1].Link.Id == "route", "Deduplicate entries and exclude reviews and patches.");
    Check(ranked.Single(guide => guide.Link.Id == "console").Score < ranked.Single(guide => guide.Link.Id == "jp").Score,
        "Show a more relevant edition first when a Steam App ID is known.");
    var best = GuideAggregation.BestMatches(ranked);
    Check(best.Count(item => item.Source == GuideSource.Df2an) == 3 && best.Any(item => item.Source == GuideSource.Steam),
        "The short list has a per-site cap and retains other platforms.");
}

static async Task Df2anAggregateAsync()
{
    using var http = new HttpClient(new Handler(uri => uri.AbsolutePath switch
    {
        "/subjects/search" => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                <div class="nexus-subjects">
                  <h3 class="subject-card-title"><a href="/subjects/42">夜光故事</a></h3>
                  <h3 class="subject-card-title"><a href="/subjects/43">夜光故事外传</a></h3>
                  <aside><a href="/subjects/1">夜光故事</a></aside>
                </div>
                """),
        },
        "/subjects/42" => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                <h1 class="subject-title">夜光故事</h1>
                <a href="/topics/91">继续阅读</a>
                <aside class="nexus-right-sidebar">
                  <a class="related-link" href="/topics/92">全结局攻略</a>
                </aside>
                """),
        },
        _ => throw new InvalidOperationException("Must only open the exact game in the search area."),
    }));
    var source = new Df2anGuideSource(new GuidePageClient(http), () => Df2anGuideSource.DefaultDomain, _ => { });
    var result = await source.SearchArticlesAsync(new("夜光故事", null, null, null), default);
    Check(result.Links is [{ Id: "92" }], "Use verified games' actual guides, excluding the site introduction.");
}

static async Task MoonNameAsync()
{
    using var http = new HttpClient(new Handler(uri => uri.AbsolutePath switch
    {
        "/search" => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                <div id="yms-search-result">
                  <a class="game-view-card" href="/ga78"><h3>夜光故事 外传</h3></a>
                  <a class="game-view-card" href="https://elsewhere.invalid/ga77"><h3>夜光故事</h3></a>
                  <a class="game-view-card" href="/ga77"><h3>Yakou Story</h3><div class="subtitle">夜光故事</div></a>
                </div>
                """),
        },
        "/ga77" => Fixture("ymgal-articles.html"),
        _ => throw new InvalidOperationException("Expected one exact game archive."),
    }));
    var source = new YmgalGuideSource(new GuidePageClient(http));
    var result = await source.SearchAsync(new GuideGame("夜光故事", null, null, null), default);
    Check(result.Value is [{ Id: "65" }], "Search game titles when there is no archive ID and open the exact archive.");
}

static void SteamPublicGuides()
{
    var address = GuideWebLinks.SteamGuides("324160")!;
    const string html = """
        <div class="guides_home_list_ctn">
          <a class="workshopItemCollection" data-appid="324160" data-publishedfileid="42"
             href="/sharedfiles/filedetails/?id=42">
             <div class="workshopItemApp guide"></div><div class="workshopItemTitle">CLANNAD Walkthrough</div>
          </a>
          <a class="workshopItemCollection" data-appid="23800" data-publishedfileid="3" href="/sharedfiles/filedetails/?id=3">
             <div class="workshopItemApp guide"></div><div class="workshopItemTitle">Another game's guide</div>
          </a>
          <a class="workshopItemCollection" data-appid="324160" data-publishedfileid="5" href="/sharedfiles/filedetails/?id=5">
             <div class="workshopItemTitle">Non-guide Steam item</div>
          </a>
        </div>
        """;
    Check(SteamGuideSource.Parse(html, address).Links is
        [{ Id: "https://steamcommunity.com/sharedfiles/filedetails/?id=42", Title: "CLANNAD Walkthrough" }],
        "Extract the requested game's public guide cards only.");
    Check(SteamGuideSource.Parse("<div class='guideInfo'></div>", address).Issue == GuideIssue.NoResults,
        "A recognized Steam guide page can have zero guides.");
}

static void LocalDefaultSelection()
{
    Check(GuideSearch.InitialSource("auto", true) is null, "A mount should not affect online automatic selection.");
    Check(GuideSearch.InitialSource("local-first", true) == GuideSource.Local, "Local-first should start on a mounted guide.");
    Check(GuideSearch.InitialSource("local-first", false) is null, "Local-first should use online automatic if nothing is mounted.");
    Check(GuideSearch.InitialSource("2dfan", true) == GuideSource.Df2an &&
        GuideSearch.InitialSource("ymgal", true) == GuideSource.Ymgal, "An explicit online source must be preserved.");
}

static async Task CngalAsync()
{
    var calls = new List<string>();
    using var http = new HttpClient(new Handler(uri =>
    {
        calls.Add(uri.AbsolutePath);
        return uri.AbsolutePath switch
        {
            "/api/home/Search" => JsonResponse("""
                {"pagedResultDto":{"data":[{"entry":{"id":6,"name":"海沙风云 外传"}},{"entry":{"id":79,"name":"海沙风云"}}]}}
                """),
            "/api/entries/GetEntryView/79" => JsonResponse("""
                {"type":"Game","articleRelevances":[
                  {"id":418,"type":"Tought","name":"《海沙风云》全结局攻略"},
                  {"id":493,"type":"Evaluation","name":"《海沙风云》评测"},
                  {"id":88,"type":"Strategy","name":"路线说明"},
                  {"id":418,"type":"Strategy","name":"重复链接"}]}
                """),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }));
    var site = new CngalGuideSource(new GuidePageClient(http));
    var result = await site.SearchAsync(new GuideGame("海沙风云", null, null, null), default);
    Check(calls.SequenceEqual(["/api/home/Search", "/api/entries/GetEntryView/79"]), "CnGal must prefer an exact game name.");
    Check(result.Links.Select(link => link.Id).SequenceEqual(
        ["https://www.cngal.org/articles/index/418", "https://www.cngal.org/articles/index/88"]),
        "Keep strategy articles and guide-titled articles, excluding reviews and duplicates.");
    var knownCalls = new List<string>();
    using var directHttp = new HttpClient(new Handler(uri =>
    {
        knownCalls.Add(uri.AbsolutePath);
        return JsonResponse("""{"type":"Game","articleRelevances":[]}""");
    }));
    var direct = new CngalGuideSource(new GuidePageClient(directHttp));
    var empty = await direct.SearchAsync(new GuideGame("海沙风云", null, null, null, "79"), default);
    Check(empty.Issue == GuideIssue.NoResults && knownCalls.SequenceEqual(["/api/entries/GetEntryView/79"]),
        "A stored CnGal ID should bypass name search.");
    using var ambiguousHttp = new HttpClient(new Handler(uri => uri.AbsolutePath == "/api/home/Search"
        ? JsonResponse("""{"pagedResultDto":{"data":[{"entry":{"id":79,"name":"海沙风云"}},{"entry":{"id":8,"name":"海沙风云"}}]}}""")
        : throw new InvalidOperationException("Ambiguous name must not select a game.")));
    var ambiguous = new CngalGuideSource(new GuidePageClient(ambiguousHttp));
    Check((await ambiguous.SearchAsync(new GuideGame("海沙风云", null, null, null), default)).Issue == GuideIssue.NoResults,
        "Ambiguous site matches must not produce an unrelated guide.");
}

static HttpResponseMessage JsonResponse(string json) =>
    new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

static async Task SeiyaAsync()
{
    const string main = """
        <a href="key/game.html">ゲーム：攻略</a>
        <a href="key/game.html#start">ゲーム</a>
        <a href="key/sequel.html">ゲーム2</a>
        <a href="https://unrelated.invalid/game/false.html">ゲーム</a>
        <a href="../save/game.zip">ゲーム</a>
        """;
    const string allAges = """
        <a href="galge/switch/game_switch.html">ゲーム Switch版 PS4版</a>
        <a href="galge/switch/game_after.html">ゲーム AFTER Switch版</a>
        """;
    using var http = new HttpClient(new Handler(uri => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(CodePagesEncodingProvider.Instance.GetEncoding(932)!
            .GetBytes(uri.Host == "galge.seiya-saiga.com" ? allAges : main)),
    }));
    var source = new SeiyaGuideSource(new GuidePageClient(http));
    var result = await source.SearchAsync(new GuideGame("汉化名", "ゲーム", "汉化名", null), default);
    Check(result.Links.Select(link => link.Title).SequenceEqual(["ゲーム", "ゲーム Switch版 PS4版"]),
        "Decode Shift-JIS and keep exact title and platform variants, excluding sequels.");
    Check(result.Links.Select(link => link.Id).SequenceEqual(
        ["https://seiya-saiga.com/game/key/game.html", "https://galge.seiya-saiga.com/game/galge/switch/game_switch.html"]),
        "Resolve relative URLs and deduplicate by canonical guide link.");
}

static void WebLinks()
{
    Check(GuideWebLinks.TryParse(" https://2dfdf.de/topics/0012#route ", out Uri mirror) &&
        GuideWebLinks.ReaderReference(mirror) == new GuideReference(GuideSource.Df2an, "12"),
        "A known 2DFan mirror link opens the in-app reader.");
    Check(GuideWebLinks.TryParse("https://www.ymgal.games/co/article/56", out Uri moon) &&
        GuideWebLinks.ReaderReference(moon) == new GuideReference(GuideSource.Ymgal, "56"),
        "A Moon article link opens the in-app reader.");
    Check(GuideWebLinks.TryParse("https://2dfan.com.example/topics/12", out Uri other) &&
        GuideWebLinks.ReaderReference(other) is null &&
        !GuideWebLinks.TryParse("https://example.com@www.ymgal.games/co/article/56", out _) &&
        !GuideWebLinks.TryParse("file:///C:/file.txt", out _) &&
        !GuideWebLinks.TryParse("javascript:alert(1)", out _),
        "Only supported article hosts route to parsers and browser links must be plain HTTP(S).");
    Check(GuideWebLinks.SteamGuides("4262610")?.AbsoluteUri ==
        "https://steamcommunity.com/app/4262610/guides/?l=schinese" &&
        GuideWebLinks.SteamGuides("0") is null && GuideWebLinks.SteamGuides("1/guides") is null,
        "Only a valid app ID generates a Steam guide link.");
    var saved = new SavedGuideLink(Guid.NewGuid(), mirror.AbsoluteUri, "全路线", new GuideBookmark("route", 4, 0.2));
    Check(JsonSerializer.Deserialize<SavedGuideLink[]>(JsonSerializer.Serialize(new[] { saved })) is [var restored] &&
        restored == saved, "Saved links retain title, URL, ID and reading progress.");
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
    Check(saved[game].LastSource == GuideSource.Ymgal,
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
