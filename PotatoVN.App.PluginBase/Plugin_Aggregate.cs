using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GalgameManager.Models;
using PotatoVN.App.PluginBase.Guides;

namespace PotatoVN.App.PluginBase;

public partial class Plugin
{
    private Task ShowAggregatedGuidesAsync(Galgame game, GuideGame names, PanelState state)
    {
        state.CurrentSource = GuideSource.Aggregate;
        Uri? steam = GuideWebLinks.SteamGuides(names.SteamId);
        GuideSource[] selected = Data.AutoSearchSources;
        return RunPanelLoadAsync(state, "正在查找在线攻略…", async token =>
        {
            var sites = await GuideAggregation.SearchAsync(selected, steam is not null, async (site, ct) => site switch
            {
                GuideSource.Df2an => await _df2an.SearchArticlesAsync(names, ct),
                GuideSource.Ymgal => GuideSiteResults.From(site, await _ymgal.SearchAsync(names, ct)),
                GuideSource.Steam => GuideSiteResults.From(site, await _steam.SearchAsync(steam!, ct)),
                _ => GuideSiteResults.From(site, await SearchWebSourceAsync(names, site, ct)),
            }, (site, error) => _hostApi.DeveloperEvent(msg: $"聚合搜索：{SourceName(site)}查询失败", e: error), token);

            if (!IsCurrentPanelRequest(state, token)) return;
            IReadOnlyList<RankedGuide> all = GuideAggregation.Rank(names, sites);
            if (sites.Length == 0)
            {
                RenderResumeAction(game, state);
                state.Status.Text = selected.Length == 0
                    ? "请在插件设置中勾选参与在线自动搜索的网站。"
                    : "已选的 Steam 社区指南需要此游戏的 Steam App ID。";
                AddOtherGuideActions(game, names, state);
                return;
            }

            RenderAggregatedResults(game, names, state, sites, all, expanded: false);
        });
    }

    private void RenderResumeAction(Galgame game, PanelState state)
    {
        if (!Data.ReadingHistory.TryGetValue(game.Uuid, out GameGuideHistory? history) ||
            history?.Get(history.LastSource) is not { } reading) return;
        string title = string.IsNullOrWhiteSpace(reading.Title) ? SourceName(reading.Article.Source) : reading.Title;
        AddResultRow(state.Content, 0, $"继续阅读 · {title}", () =>
        {
            PushBack(state);
            return OpenArticleAsync(game, state, reading.Article, reading.Title);
        });
    }

    private void RenderAggregatedResults(Galgame game, GuideGame names, PanelState state, GuideSiteResults[] sites,
        IReadOnlyList<RankedGuide> all, bool expanded)
    {
        state.Content.Children.Clear();
        var results = expanded ? all : GuideAggregation.BestMatches(all);
        var unavailable = sites.Where(site => site.Issue is GuideIssue.Unavailable or GuideIssue.LayoutChanged)
            .Select(site => SourceName(site.Source)).ToArray();
        string heading = results.Count == 0 ? "未找到匹配的在线攻略。"
            : expanded ? $"显示全部 {all.Count} 条在线攻略。"
            : $"已找到 {all.Count} 条在线攻略，推荐 {results.Count} 条。";
        state.Status.Text = unavailable.Length == 0 ? heading
            : $"{heading}\n暂时无法查询：{string.Join("、", unavailable)}。";

        RenderResumeAction(game, state);

        foreach (var item in results)
        {
            string label = $"[{SourceName(item.Source)}] {item.Link.Title}";
            AddResultRow(state.Content, 0, label, () =>
            {
                if (item.Source is not (GuideSource.Df2an or GuideSource.Ymgal))
                    return OpenGuideInBrowserAsync(item.Link.Id);

                PushBack(state);
                return OpenArticleAsync(game, state, new GuideReference(item.Source, item.Link.Id), item.Link.Title);
            });
        }

        if (expanded || results.Count < all.Count)
        {
            AddResultRow(state.Content, 0, expanded ? "只看推荐" : "显示全部结果", () =>
            {
                RenderAggregatedResults(game, names, state, sites, all, !expanded);
                state.Scroll.ChangeView(null, 0, null, true);
                return Task.CompletedTask;
            });
        }

        if (all.Count == 0)
        {
            foreach (var site in sites.Where(item => item.Source != GuideSource.Steam))
            {
                if (site.BrowserUrl is { } url)
                    AddResultRow(state.Content, 0, $"在 {SourceName(site.Source)} 手动查看",
                        () => OpenGuideInBrowserAsync(url.AbsoluteUri));
            }
            AddOtherGuideActions(game, names, state);
        }
    }
}
