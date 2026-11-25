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
        source == GuideSource.Cngal ? _cngal.SearchAsync(game, token) : _seiya.SearchAsync(game, token);

    private Task ShowWebSourceAsync(Galgame game, GuideGame names, PanelState state, GuideSource source) =>
        RunPanelLoadAsync(state, $"正在查找{SourceName(source)}攻略…", async token =>
        {
            WebGuideResult results = await SearchWebSourceAsync(names, source, token);
            if (!IsCurrentPanelRequest(state, token)) return;
            SetOpenSite(state, results.BrowseUrl.AbsoluteUri);
            state.Status.Text = results.Links.Count > 0
                ? $"选择{SourceName(source)}攻略，在浏览器打开。"
                : DescribeIssue(source, results.Issue);
            AddWebResults(state, source, results);
            AddOtherGuideActions(game, names, state);
        });

    private void AddOtherGuideActions(Galgame game, GuideGame names, PanelState state)
    {
        bool hasLinks = GetSavedLinks(game.Uuid).Length > 0;
        AddResultRow(state.Content, 0, hasLinks ? "查看已保存的攻略链接" : "添加攻略链接", async () =>
        {
            state.PreferredSource = GuideSource.SavedLinks;
            await ShowSourceAsync(game, state);
            if (!hasLinks) ShowGuideLinkEditor(game, state);
        });
        if (GuideWebLinks.SteamGuides(names.SteamId) is { } steam)
            AddResultRow(state.Content, 0, "打开 Steam 社区指南", () => OpenGuideInBrowserAsync(steam.AbsoluteUri));
    }

    private static void AddWebResults(PanelState state, GuideSource source, WebGuideResult result)
    {
        foreach (GuideLink item in result.Links)
            AddResultRow(state.Content, 0, $"[{SourceName(source)}] {item.Title}",
                () => OpenGuideInBrowserAsync(item.Id));
    }

    private SavedGuideLink[] GetSavedLinks(Guid gameId) =>
        Data.SavedLinks.TryGetValue(gameId, out SavedGuideLink[]? links) ? links : [];

    private Task ShowSavedLinks(Galgame game, PanelState state)
    {
        state.CurrentSource = GuideSource.SavedLinks;
        state.ActiveSavedLink = null;
        return RunPanelLoadAsync(state, "已保存的攻略链接", _ =>
        {
            SavedGuideLink[] links = GetSavedLinks(game.Uuid);
            if (links.Length == 0) state.Status.Text = "此游戏还没有保存攻略链接。";
            foreach (SavedGuideLink link in links)
            {
                if (!GuideWebLinks.TryParse(link.Url, out Uri address)) continue;
                string title = string.IsNullOrWhiteSpace(link.Title) ? address.Host : link.Title;
                var row = new Grid { ColumnSpacing = 4 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                bool canRead = GuideWebLinks.ReaderReference(address) is not null;
                var open = AddResultRow(row, 0, title, () =>
                {
                    if (!canRead) return OpenGuideInBrowserAsync(link.Url);
                    PushBack(state);
                    return OpenSavedLinkAsync(game, state, link, address);
                });
                DescribeGuideControl(open, title, canRead
                    ? $"在浮窗阅读 {address.Host} 的攻略"
                    : $"在浏览器打开 {address.Host}");
                var edit = CreateGuideButton("编辑", $"编辑或删除「{title}」", () => ShowGuideLinkEditor(game, state, link));
                Grid.SetColumn(edit, 1);
                row.Children.Add(edit);
                state.Content.Children.Add(row);
            }
            AddResultRow(state.Content, 0, "添加攻略链接", () =>
            {
                ShowGuideLinkEditor(game, state);
                return Task.CompletedTask;
            });
            return Task.CompletedTask;
        });
    }

    private void ShowGuideLinkEditor(Galgame game, PanelState state, SavedGuideLink? existing = null)
    {
        PushBack(state);
        _ = RunPanelLoadAsync(state, existing is null ? "添加攻略链接" : "编辑攻略链接", _token =>
        {
            var addressBox = new TextBox
            {
                Header = "网页地址",
                PlaceholderText = "https://",
                Text = existing?.Url ?? "",
                MaxLength = 8192,
            };
            var titleBox = new TextBox
            {
                Header = "链接名称（可选）",
                PlaceholderText = "例如：全路线攻略",
                Text = existing?.Title ?? "",
                MaxLength = 160,
            };
            state.Content.Children.Add(addressBox);
            state.Content.Children.Add(titleBox);
            AddResultRow(state.Content, 0, "保存", async () =>
            {
                if (!GuideWebLinks.TryParse(addressBox.Text, out Uri address))
                {
                    state.Status.Text = "请输入完整的 HTTP 或 HTTPS 网页地址。";
                    addressBox.Focus(FocusState.Programmatic);
                    return;
                }
                SavedGuideLink[] links = GetSavedLinks(game.Uuid);
                string url = address.AbsoluteUri;
                if (links.Any(link => link.Id != existing?.Id && link.Url == url))
                {
                    state.Status.Text = "此游戏已保存这个链接。";
                    return;
                }
                string title = titleBox.Text.Trim();
                if (existing is null)
                    Data.SavedLinks[game.Uuid] = [..links, new SavedGuideLink(Guid.NewGuid(), url, title)];
                else
                    Data.SavedLinks[game.Uuid] = links.Select(link => link.Id == existing.Id
                        ? link with { Url = url, Title = title, Bookmark = link.Url == url ? link.Bookmark : null }
                        : link).ToArray();
                SaveData();
                await ShowSourceAsync(game, state);
            });
            if (existing is not null)
            {
                AddResultRow(state.Content, 0, "删除链接", async () =>
                {
                    SavedGuideLink[] remaining = GetSavedLinks(game.Uuid).Where(link => link.Id != existing.Id).ToArray();
                    if (remaining.Length == 0) Data.SavedLinks.TryRemove(game.Uuid, out _);
                    else Data.SavedLinks[game.Uuid] = remaining;
                    SaveData();
                    await ShowSourceAsync(game, state);
                });
            }
            AddResultRow(state.Content, 0, "取消", () =>
            {
                PopBack(state);
                return Task.CompletedTask;
            });
            addressBox.Loaded += (_, _) => addressBox.Focus(FocusState.Programmatic);
            return Task.CompletedTask;
        });
    }

    private Task OpenSavedLinkAsync(Galgame game, PanelState state, SavedGuideLink link, Uri address)
    {
        if (GuideWebLinks.ReaderReference(address) is not { } reference)
            return OpenGuideInBrowserAsync(address.AbsoluteUri);

        state.ActiveSavedLink = link.Id;
        return RunPanelLoadAsync(state, $"正在打开{SourceName(reference.Source)}正文…", async token =>
        {
            string title = string.IsNullOrWhiteSpace(link.Title) ? $"{SourceName(reference.Source)}攻略" : link.Title;
            var result = await ReadOnlineArticleAsync(reference, title, token);
            if (!IsCurrentPanelRequest(state, token)) return;
            SetOpenSite(state, address.AbsoluteUri);
            if (result.Value is { } article)
            {
                PresentArticle(state, article.Text, link.Bookmark,
                    bookmark => RememberSavedLinkPosition(game.Uuid, link, bookmark));
                return;
            }

            state.Status.Text = DescribeIssue(reference.Source, result.Issue);
            AddResultRow(state.Content, 0, "在浏览器打开原文", () => OpenGuideInBrowserAsync(address.AbsoluteUri));
        });
    }

    private void RememberSavedLinkPosition(Guid gameId, SavedGuideLink opened, GuideBookmark bookmark)
    {
        while (Data.SavedLinks.TryGetValue(gameId, out SavedGuideLink[]? links))
        {
            int index = Array.FindIndex(links, link => link.Id == opened.Id && link.Url == opened.Url);
            if (index < 0 || links[index].Bookmark == bookmark) return;

            SavedGuideLink[] changed = (SavedGuideLink[])links.Clone();
            changed[index] = changed[index] with { Bookmark = bookmark };
            if (!Data.SavedLinks.TryUpdate(gameId, changed, links)) continue;
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
