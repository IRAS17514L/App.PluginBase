using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GalgameManager.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PotatoVN.App.PluginBase.Guides;

namespace PotatoVN.App.PluginBase;

public partial class Plugin
{
    private Task<WebGuideResult> SearchWebSourceAsync(GuideGame game, GuideSource source, CancellationToken token) =>
        source switch
        {
            GuideSource.Cngal => _cngal.SearchAsync(game, token),
            GuideSource.Seiya => _seiya.SearchAsync(game, token),
            GuideSource.Steam when GuideWebLinks.SteamGuides(game.SteamId) is { } url => _steam.SearchAsync(url, token),
            GuideSource.Steam => Task.FromResult(WebGuideResult.Failure(GuideIssue.MissingGameId,
                new Uri("https://steamcommunity.com/"))),
            _ => throw new ArgumentOutOfRangeException(nameof(source)),
        };

    private Task ShowWebSourceAsync(Galgame game, GuideGame names, PanelState state, GuideSource source) =>
        RunPanelLoadAsync(state, $"正在查找{SourceName(source)}攻略…", async token =>
        {
            WebGuideResult results = await SearchWebSourceAsync(names, source, token);
            if (!IsCurrentPanelRequest(state, token)) return;
            SetOpenSite(state, results.BrowseUrl.AbsoluteUri);
            state.Status.Text = results.Links.Count > 0
                ? $"选择{SourceName(source)}攻略"
                : DescribeIssue(source, results.Issue);
            foreach (GuideLink article in results.Links)
                AddOnlineGuideRow(game, state, source, article);
            AddOtherGuideActions(game, names, state);
        });

    private void AddOtherGuideActions(Galgame game, GuideGame names, PanelState state)
    {
        AddResultRow(state.Content, 0, "自定义攻略", () =>
        {
            state.PreferredSource = GuideSource.SavedLinks;
            return ShowSourceAsync(game, state);
        });
        if (GuideWebLinks.SteamGuides(names.SteamId) is { } steam)
            AddResultRow(state.Content, 0, "打开 Steam 社区指南", () => OpenGuideInBrowserAsync(steam.AbsoluteUri));
    }

    private void AddOnlineGuideRow(Galgame game, PanelState state, GuideSource source, GuideLink article)
    {
        AddResultRow(state.Content, 0, $"[{SourceName(source)}] {article.Title}", () =>
        {
            GuideReference? reference = source is GuideSource.Df2an or GuideSource.Ymgal
                ? new GuideReference(source, article.Id)
                : GuideWebLinks.TryParse(article.Id, out Uri url) ? GuideWebLinks.ReaderReference(url) : null;
            if (reference is not { IsValid: true } match || match.Source != source)
                return OpenGuideInBrowserAsync(article.Id);

            PushBack(state);
            return OpenArticleAsync(game, state, match, article.Title);
        });
    }

    private SavedGuide[] GetSavedGuides(Guid gameId) =>
        Data.SavedGuides.TryGetValue(gameId, out SavedGuide[]? guides) ? guides : [];

    private Task ShowCustomGuides(Galgame game, PanelState state)
    {
        state.CurrentSource = GuideSource.SavedLinks;
        return RunPanelLoadAsync(state, "自定义攻略", _token =>
        {
            SavedGuide[] guides = GetSavedGuides(game.Uuid);
            if (guides.Length == 0) state.Status.Text = "为此游戏保存网页链接，或直接粘贴攻略文本。";
            foreach (SavedGuide guide in guides)
            {
                string title = SavedGuideTitle(guide);
                var row = new Grid { ColumnSpacing = 4 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var open = AddResultRow(row, 0, title, () =>
                {
                    PushBack(state);
                    return OpenSavedGuideAsync(game, state, guide);
                });
                DescribeGuideControl(open, title, guide.Text is null
                    ? $"尝试在浮窗阅读 {title}，也可在浏览器打开原文"
                    : $"在浮窗阅读已保存的文本「{title}」");
                var edit = CreateGuideButton("编辑", $"编辑或删除「{title}」",
                    () => _ = ShowCustomGuideEditor(game, state, guide.Text is not null, guide));
                Grid.SetColumn(edit, 1);
                row.Children.Add(edit);
                state.Content.Children.Add(row);
            }
            AddResultRow(state.Content, 0, "添加网页链接", () => ShowCustomGuideEditor(game, state, isText: false));
            AddResultRow(state.Content, 0, "粘贴攻略文本", () => ShowCustomGuideEditor(game, state, isText: true));
            return Task.CompletedTask;
        });
    }

    private static string SavedGuideTitle(SavedGuide guide) => !string.IsNullOrWhiteSpace(guide.Title)
        ? guide.Title
        : GuideWebLinks.TryParse(guide.Url, out Uri url) ? url.Host : "粘贴的攻略";

    private Task ShowCustomGuideEditor(Galgame game, PanelState state, bool isText, SavedGuide? existing = null)
    {
        PushBack(state);
        string heading = isText
            ? existing is null ? "粘贴攻略文本" : "编辑攻略文本"
            : existing is null ? "添加网页链接" : "编辑网页链接";
        return RunPanelLoadAsync(state, heading, token =>
        {
            var titleBox = new TextBox
            {
                Header = "攻略名称（可选）",
                PlaceholderText = "例如：全路线攻略",
                Text = existing?.Title ?? "",
                MaxLength = 160,
            };
            var input = new TextBox
            {
                Header = isText ? "攻略正文" : "网页地址",
                PlaceholderText = isText ? "在这里粘贴或输入纯文本" : "https://",
                AcceptsReturn = isText,
                TextWrapping = isText ? TextWrapping.Wrap : TextWrapping.NoWrap,
            };
            if (isText)
            {
                input.MinHeight = 116;
                input.MaxHeight = 260;
                ScrollViewer.SetVerticalScrollBarVisibility(input, ScrollBarVisibility.Auto);
                state.Content.Children.Add(titleBox);
                state.Content.Children.Add(input);
            }
            else
            {
                input.MaxLength = 8192;
                state.Content.Children.Add(input);
                state.Content.Children.Add(titleBox);
            }
            input.Text = (isText ? existing?.Text : existing?.Url) ?? "";

            AddResultRow(state.Content, 0, "保存", async () =>
            {
                if (!IsCurrentPanelRequest(state, token)) return;
                SavedGuide[] guides = GetSavedGuides(game.Uuid);
                string title = titleBox.Text.Trim();
                var updated = PrepareSavedGuide(state, input.Text, title, isText, game.Uuid, existing);
                if (updated is null)
                {
                    input.Focus(FocusState.Programmatic);
                    return;
                }
                Data.SavedGuides[game.Uuid] = existing is null
                    ? [..guides, updated]
                    : guides.Select(guide => guide.Id == existing.Id ? updated : guide).ToArray();
                if (existing is not null && updated.Url != existing.Url)
                    ForgetSavedReading(game.Uuid, existing.Id);
                SaveData();
                await ShowSourceAsync(game, state);
            });
            if (existing is not null)
            {
                AddResultRow(state.Content, 0, isText ? "删除文本" : "删除链接", async () =>
                {
                    if (!IsCurrentPanelRequest(state, token)) return;
                    SavedGuide[] remaining = GetSavedGuides(game.Uuid).Where(guide => guide.Id != existing.Id).ToArray();
                    if (remaining.Length == 0) Data.SavedGuides.TryRemove(game.Uuid, out _);
                    else Data.SavedGuides[game.Uuid] = remaining;
                    ForgetSavedReading(game.Uuid, existing.Id);
                    SaveData();
                    await ShowSourceAsync(game, state);
                });
            }
            AddResultRow(state.Content, 0, "取消", () =>
            {
                PopBack(state);
                return Task.CompletedTask;
            });
            input.Loaded += (_, _) => input.Focus(FocusState.Programmatic);
            return Task.CompletedTask;
        });
    }

    private SavedGuide? PrepareSavedGuide(PanelState state, string input, string title, bool isText,
        Guid gameId, SavedGuide? existing)
    {
        Guid id = existing?.Id ?? Guid.NewGuid();
        if (isText)
        {
            string text = input.ReplaceLineEndings("\n");
            if (string.IsNullOrWhiteSpace(text))
                state.Status.Text = "请输入或粘贴攻略正文。";
            else if (text.Length > SavedGuide.MaxTextLength)
                state.Status.Text = "单篇最多可保存 25 万字符。可以分篇保存或挂载文本文件。";
            else if (Data.SavedGuides.SelectMany(pair => pair.Value)
                         .Where(guide => guide.Id != id).Sum(guide => (long)(guide.Text?.Length ?? 0)) +
                     text.Length > SavedGuide.MaxTotalTextLength)
                state.Status.Text = "已达到粘贴文本的总容量。较长的攻略可改用本地文件。";
            else
                return new SavedGuide(id, null, title, existing?.Bookmark, text);
            return null;
        }

        if (!GuideWebLinks.TryParse(input, out Uri address))
            state.Status.Text = "请输入完整的 HTTP 或 HTTPS 网页地址。";
        else if (GetSavedGuides(gameId).Any(guide => guide.Id != id && guide.Url == address.AbsoluteUri))
            state.Status.Text = "此游戏已保存这个链接。";
        else
            return new SavedGuide(id, address.AbsoluteUri, title,
                existing?.Url == address.AbsoluteUri ? existing?.Bookmark : null);
        return null;
    }

    private Task OpenSavedGuideAsync(Galgame game, PanelState state, SavedGuide guide, CachedGuideText? cached = null)
    {
        state.CurrentSource = GuideSource.SavedLinks;
        string title = SavedGuideTitle(guide);
        return RunPanelLoadAsync(state, guide.Text is null ? "正在提取网页正文…" : "正在打开攻略文本…", async token =>
        {
            guide = GetSavedGuides(game.Uuid).FirstOrDefault(item => item.HasSameContent(guide)) ?? guide;
            GuideResume reading = GuideResume.Saved(guide);
            state.ActiveReading = reading;
            void Remember(GuideBookmark bookmark) => RememberSavedGuidePosition(game.Uuid, guide, bookmark);

            if (guide.Text is { } text)
            {
                PresentArticle(state, text, guide.Bookmark, Remember);
                RememberReading(game.Uuid, reading);
                return;
            }
            if (!GuideWebLinks.TryParse(guide.Url, out Uri address)) return;

            if (cached is { IsValid: true })
            {
                Uri cachedOriginal = cached.GetOriginal() ?? address;
                SetOpenSite(state, cachedOriginal.AbsoluteUri);
                PresentOnlineArticle(state, cached.ToArticle(), cachedOriginal, guide.Bookmark, Remember);
                RememberReading(game.Uuid, reading with { Cached = cached });
                return;
            }

            SetOpenSite(state, address.AbsoluteUri);
            GuideReference? reference = GuideWebLinks.ReaderReference(address);
            GuideResult<GuideArticle> result = reference is { } match
                ? await ReadOnlineArticleAsync(match, title, token)
                : await _externalGuidePage.ReadAsync(address, title, token);
            if (!IsCurrentPanelRequest(state, token)) return;
            Uri original = reference?.Source == GuideSource.Seiya ? result.BrowserUrl ?? address : address;
            if (reference is not null && result.Value is null && result.Issue != GuideIssue.BrowserOnly)
            {
                result = await _externalGuidePage.ReadAsync(original, title, token);
                if (!IsCurrentPanelRequest(state, token)) return;
            }

            SetOpenSite(state, original.AbsoluteUri);
            if (result.Value is { } article)
            {
                PresentOnlineArticle(state, article, original, guide.Bookmark, Remember);
                RememberReading(game.Uuid, reading with { Cached = CachedGuideText.Create(article, original) });
                return;
            }

            state.Status.Text = "浮窗未能提取正文。可以在浏览器查看原文。";
            AddResultRow(state.Content, 0, "在浏览器打开原文", () => OpenGuideInBrowserAsync(original.AbsoluteUri));
        });
    }

    private void RememberSavedGuidePosition(Guid gameId, SavedGuide opened, GuideBookmark bookmark)
    {
        while (Data.SavedGuides.TryGetValue(gameId, out SavedGuide[]? guides))
        {
            int index = Array.FindIndex(guides, guide => guide.HasSameContent(opened));
            if (index < 0 || guides[index].Bookmark == bookmark) return;
            SavedGuide[] changed = (SavedGuide[])guides.Clone();
            changed[index] = changed[index] with { Bookmark = bookmark };
            if (!Data.SavedGuides.TryUpdate(gameId, changed, guides)) continue;
            SaveData();
            return;
        }
    }

    private static async Task OpenGuideInBrowserAsync(string url)
    {
        if (!GuideWebLinks.TryParse(url, out Uri address)) return;
        try
        {
            if (!await Windows.System.Launcher.LaunchUriAsync(address))
                HostApi.Info(InfoBarSeverity.Warning, "无法打开浏览器", url);
        }
        catch (Exception e)
        {
            HostApi.Info(InfoBarSeverity.Warning, "无法打开浏览器", e.Message);
        }
    }
}
