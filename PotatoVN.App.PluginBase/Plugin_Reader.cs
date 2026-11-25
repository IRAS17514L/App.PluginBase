using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GalgameManager.Enums;
using GalgameManager.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using PotatoVN.App.PluginBase.Guides;
using Windows.System;

namespace PotatoVN.App.PluginBase;

public partial class Plugin
{
    private readonly GuidePageClient _guidePages = new();
    private readonly Df2anGuideSource _df2an;
    private readonly YmgalGuideSource _ymgal;
    private readonly CngalGuideSource _cngal;
    private readonly SeiyaGuideSource _seiya;
    private readonly SteamGuideSource _steam;

    private sealed record PanelSnapshot(string StatusText, string? OpenSiteUrl, GuideSource Source, UIElement[] Children);

    private sealed class PanelState(Window window, StackPanel content, TextBlock status, ScrollViewer scroll, Grid findHost)
    {
        public Window Window { get; } = window;
        public StackPanel Content { get; } = content;
        public TextBlock Status { get; } = status;
        public ScrollViewer Scroll { get; } = scroll;
        public Grid FindHost { get; } = findHost;
        public GuideReader? Reader;
        public FloatingWindowPin? WindowPin;
        public GuideSource? PreferredSource;
        public GuideSource CurrentSource;
        public Guid? ActiveSavedLink;
        public string? OpenSiteUrl;
        public Button? SiteButton;
        public Button? LocalFileButton;
        public Button? FindButton;
        public Button? CompactFindButton;
        public List<(GuideSource? Source, ToggleButton Button)> SourceToggles { get; } = [];
        public ProgressRing? ProgressRing;
        public Button? CompactBackButton;
        public Button? BarBackButton;
        public CancellationTokenSource? RequestCancellation;
        public bool CompactMode;
        public Stack<PanelSnapshot> BackStack { get; } = [];
    }

    private FrameworkElement BuildGuidePanel(Window window, Guid gameId, out PanelState state)
    {
        TextBlock status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = GetSecondaryBrush() };
        StackPanel content = new() { Spacing = 4 };
        ScrollViewer scroll = new() { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid findHost = new() { Visibility = Visibility.Collapsed };

        var panel = new PanelState(window, content, status, scroll, findHost)
        {
            PreferredSource = GuideSearch.InitialSource(Data.DefaultSource, Data.LocalGuides.ContainsKey(gameId)),
        };
        state = panel;

        Grid root = new() { RowSpacing = 2 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(status);
        Grid.SetRow(findHost, 1);
        root.Children.Add(findHost);
        Grid.SetRow(scroll, 2);
        root.Children.Add(scroll);

        scroll.ViewChanged += (_, args) =>
        {
            if (!args.IsIntermediate) panel.Reader?.SavePosition();
        };
        var find = new KeyboardAccelerator { Key = VirtualKey.F, Modifiers = VirtualKeyModifiers.Control };
        find.Invoked += (_, args) =>
        {
            if (panel.Reader is not { } reader) return;
            reader.OpenFind();
            args.Handled = true;
        };
        root.KeyboardAccelerators.Add(find);
        return root;
    }

    private Task ShowSourceAsync(Galgame game, PanelState state)
    {
        CancelPanelRequest(state);
        ResetReader(state);
        state.BackStack.Clear();
        UpdateBackButtons(state);
        UpdateSourceToggleState(state);

        state.ActiveSavedLink = null;
        if (state.PreferredSource == GuideSource.Local)
            return ShowLocalAsync(game, state, resume: true);
        if (state.PreferredSource == GuideSource.SavedLinks)
            return ShowSavedLinks(game, state);

        GuideGame names = DescribeGame(game);
        if (state.PreferredSource is not { } source)
            return ShowAggregatedGuidesAsync(game, names, state);

        state.CurrentSource = source;
        if (source is GuideSource.Cngal or GuideSource.Seiya)
            return ShowWebSourceAsync(game, names, state, source);

        Data.ReadingHistory.TryGetValue(game.Uuid, out GameGuideHistory? history);
        if (history?.Get(source) is not { } reading)
            return SearchSingleSiteAsync(game, names, source, state);

        ShowSavedActions(game, names, state, reading);
        PushBack(state);
        return OpenArticleAsync(game, state, reading.Article, reading.Title);
    }

    private void ShowSavedActions(Galgame game, GuideGame names, PanelState state, GuideReading reading)
    {
        state.Content.Children.Clear();
        SetOpenSite(state, null);
        state.Status.Visibility = Visibility.Visible;
        state.Status.Text = string.IsNullOrWhiteSpace(reading.Title)
            ? "此游戏有阅读记录。"
            : $"上次阅读：{reading.Title}";
        AddResultRow(state.Content, 0, "打开上次读的攻略", () =>
        {
            PushBack(state);
            return OpenArticleAsync(game, state, reading.Article, reading.Title);
        });
        AddResultRow(state.Content, 0, "重新查找攻略", () =>
        {
            PushBack(state);
            return SearchSingleSiteAsync(game, names, reading.Article.Source, state);
        });
    }

    private Task SearchSingleSiteAsync(Galgame game, GuideGame names, GuideSource source, PanelState state) =>
        RunPanelLoadAsync(state, $"正在查找{SourceName(source)}攻略…",
            token => FindCandidatesAsync(game, names, source, state, token));

    private async Task FindCandidatesAsync(Galgame game, GuideGame names, GuideSource source, PanelState state,
        CancellationToken token)
    {
        var result = source == GuideSource.Df2an ? await _df2an.SearchAsync(names, token) : await _ymgal.SearchAsync(names, token);
        if (!IsCurrentPanelRequest(state, token)) return;

        state.CurrentSource = source;
        SetOpenSite(state, result.BrowserUrl?.AbsoluteUri);
        if (result.Value is not { } entries)
        {
            state.Status.Text = DescribeIssue(source, result.Issue);
            AddOtherGuideActions(game, names, state);
            return;
        }

        if (source == GuideSource.Ymgal)
        {
            ShowArticles(game, state, source, entries);
            return;
        }

        state.Status.Text = "选择对应的 2DFan 游戏条目";
        for (int i = 0; i < entries.Count; i++)
        {
            GuideLink subject = entries[i];
            AddResultRow(state.Content, i + 1, subject.Title, () =>
            {
                PushBack(state);
                return RunPanelLoadAsync(state, $"正在获取「{subject.Title}」的攻略…",
                    ct => FindTopicsAsync(game, names, subject, state, ct));
            });
        }
        if (entries.Count == 1 && GuideSearch.HasExactTitle(entries, names))
        {
            PushBack(state);
            state.Content.Children.Clear();
            state.Status.Text = $"正在获取「{entries[0].Title}」的攻略…";
            await FindTopicsAsync(game, names, entries[0], state, token);
        }
    }

    private async Task FindTopicsAsync(Galgame game, GuideGame names, GuideLink subject, PanelState state,
        CancellationToken token)
    {
        var topics = await _df2an.GetTopicsAsync(subject.Id, token);
        if (!IsCurrentPanelRequest(state, token)) return;
        SetOpenSite(state, topics.BrowserUrl?.AbsoluteUri);
        if (topics.Value is { } entries)
        {
            ShowArticles(game, state, GuideSource.Df2an, entries);
            return;
        }

        state.Status.Text = DescribeIssue(GuideSource.Df2an, topics.Issue);
        AddOtherGuideActions(game, names, state);
    }

    private void ShowArticles(Galgame game, PanelState state, GuideSource source, IReadOnlyList<GuideLink> articles)
    {
        state.CurrentSource = source;
        state.Status.Text = $"选择{SourceName(source)}文章";
        for (int i = 0; i < articles.Count; i++)
        {
            GuideLink article = articles[i];
            AddResultRow(state.Content, i + 1, article.Title, () =>
            {
                PushBack(state);
                return OpenArticleAsync(game, state, new GuideReference(source, article.Id), article.Title);
            });
        }
    }

    private Task OpenArticleAsync(Galgame game, PanelState state, GuideReference reference, string? title)
    {
        state.CurrentSource = reference.Source;
        return RunPanelLoadAsync(state, $"正在打开{SourceName(reference.Source)}正文…", async token =>
        {
            string titleOrFallback = string.IsNullOrWhiteSpace(title) ? $"{SourceName(reference.Source)}攻略" : title;
            var result = await ReadOnlineArticleAsync(reference, titleOrFallback, token);
            if (!IsCurrentPanelRequest(state, token)) return;
            SetOpenSite(state, result.BrowserUrl?.AbsoluteUri);
            if (result.Value is not { } article)
            {
                state.Status.Text = DescribeIssue(reference.Source, result.Issue);
                if (result.BrowserUrl is { } url)
                    AddResultRow(state.Content, 0, "在浏览器打开原文", () => OpenGuideInBrowserAsync(url.AbsoluteUri));
                return;
            }

            Data.ReadingHistory.TryGetValue(game.Uuid, out GameGuideHistory? history);
            GuideReading? previous = history?.Get(reference.Source);
            GuideBookmark? bookmark = previous?.Article == reference ? previous.Bookmark : null;
            PresentArticle(state, article.Text, bookmark, position =>
            {
                if (GuideHistory.RememberPosition(Data.ReadingHistory, game.Uuid, reference, position))
                    SaveData();
            });

            GuideHistory.Remember(Data.ReadingHistory, game.Uuid, new GuideReading(reference, article.Title, bookmark));
            SaveData();
        });
    }

    private Task<GuideResult<GuideArticle>> ReadOnlineArticleAsync(GuideReference reference, string title,
        CancellationToken token) => reference.Source == GuideSource.Df2an
            ? _df2an.ReadArticleAsync(reference.Id, title, token)
            : _ymgal.ReadArticleAsync(reference.Id, title, token);

    private static void PresentArticle(PanelState state, string text, GuideBookmark? bookmark, Action<GuideBookmark> remember)
    {
        GuideReader reader = new(text, bookmark, state.Scroll, state.FindHost, remember);
        state.Content.Children.Add(reader.Body);
        state.Reader = reader;
        state.Status.Visibility = Visibility.Collapsed;
        UpdateFindButton(state);
    }

    private async Task RunPanelLoadAsync(PanelState state, string loadingText, Func<CancellationToken, Task> load)
    {
        CancelPanelRequest(state);
        var request = new CancellationTokenSource();
        state.RequestCancellation = request;
        CancellationToken token = request.Token;
        ResetReader(state);
        state.Content.Children.Clear();
        state.Scroll.ChangeView(null, 0, null, true);
        state.Status.Text = loadingText;
        state.Status.Visibility = Visibility.Visible;
        SetOpenSite(state, null);
        UpdateProgress(state, true);
        try
        {
            await load(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!IsCurrentPanelRequest(state, token)) return;
            state.Status.Text = "攻略加载失败，请重试。";
            _hostApi.DeveloperEvent(msg: "攻略面板加载失败", e: error);
        }
        finally
        {
            if (IsCurrentPanelRequest(state, token)) UpdateProgress(state, false);
        }
    }

    private static bool IsCurrentPanelRequest(PanelState state, CancellationToken token) =>
        state.RequestCancellation is { } current && current.Token == token && !token.IsCancellationRequested;

    private static void CancelPanelRequest(PanelState state)
    {
        state.RequestCancellation?.Cancel();
        state.RequestCancellation?.Dispose();
        state.RequestCancellation = null;
    }

    private static void ResetReader(PanelState state)
    {
        state.Reader?.SavePosition();
        state.Reader = null;
        state.FindHost.Children.Clear();
        state.FindHost.Visibility = Visibility.Collapsed;
        UpdateFindButton(state);
    }

    private static void PushBack(PanelState state)
    {
        state.BackStack.Push(new PanelSnapshot(state.Status.Text, state.OpenSiteUrl, state.CurrentSource,
            state.Content.Children.ToArray()));
        UpdateBackButtons(state);
    }

    private static void PopBack(PanelState state)
    {
        if (!state.BackStack.TryPop(out PanelSnapshot? snapshot)) return;
        CancelPanelRequest(state);
        ResetReader(state);
        state.Content.Children.Clear();
        foreach (UIElement child in snapshot.Children) state.Content.Children.Add(child);
        state.Status.Text = snapshot.StatusText;
        state.Status.Visibility = Visibility.Visible;
        state.CurrentSource = snapshot.Source;
        state.ActiveSavedLink = null;
        SetOpenSite(state, snapshot.OpenSiteUrl);
        UpdateBackButtons(state);
        UpdateProgress(state, false);
    }

    private static void UpdateSourceToggleState(PanelState state)
    {
        foreach (var (source, button) in state.SourceToggles)
            button.IsChecked = source == state.PreferredSource;
    }

    private static void UpdateBackButtons(PanelState state)
    {
        bool canGoBack = state.BackStack.Count > 0;
        if (state.BarBackButton is not null) state.BarBackButton.IsEnabled = canGoBack;
        if (state.CompactBackButton is not null)
            state.CompactBackButton.Visibility = state.CompactMode && canGoBack ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void UpdateFindButton(PanelState state)
    {
        bool canFind = state.Reader is not null;
        if (state.FindButton is not null) state.FindButton.IsEnabled = canFind;
        if (state.CompactFindButton is not null)
            state.CompactFindButton.Visibility = state.CompactMode && canFind ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void UpdateProgress(PanelState state, bool loading)
    {
        if (state.ProgressRing is null) return;
        state.ProgressRing.IsActive = loading;
        state.ProgressRing.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
    }

    private static GuideGame DescribeGame(Galgame game) => new(game.CnName, game.OriginalName.Value,
        game.Name.Value, game.Ids?.ElementAtOrDefault((int)RssType.Ymgal),
        game.Ids?.ElementAtOrDefault((int)RssType.Cngal), game.Ids?.ElementAtOrDefault((int)RssType.Steam));

    private static string SourceName(GuideSource source) => source switch
    {
        GuideSource.Df2an => "2DFan",
        GuideSource.Ymgal => "月幕",
        GuideSource.Cngal => "CnGal",
        GuideSource.Seiya => "誠也の部屋",
        GuideSource.Steam => "Steam",
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };

    private static string DescribeIssue(GuideSource source, GuideIssue issue)
    {
        string label = SourceName(source);
        return issue switch
        {
            GuideIssue.MissingGameId => "此游戏缺少月幕档案编号和可用于匹配的名称。",
            GuideIssue.NoResults => $"{label}没有找到攻略。",
            GuideIssue.LayoutChanged => $"暂时无法识别{label}的页面，可在浏览器打开。",
            GuideIssue.ArticleMissing => $"{label}的文章已移除或正文为空，可返回重新查找。",
            _ => $"{label}暂时不可用，可稍后刷新或在浏览器打开。",
        };
    }
}
