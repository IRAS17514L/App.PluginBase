using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GalgameManager.Enums;
using GalgameManager.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using PotatoVN.App.PluginBase.Guides;

namespace PotatoVN.App.PluginBase;

public partial class Plugin
{
    private GuideResume? GetLastReading(Guid gameId) =>
        Data.LastReadings.TryGetValue(gameId, out GuideResume? reading) && CanOpenReading(gameId, reading)
            ? reading : null;

    private bool CanOpenReading(Guid gameId, GuideResume reading) => reading.Source switch
    {
        GuideSource.Local => Data.LocalGuides.TryGetValue(gameId, out var local) && local is not null,
        GuideSource.SavedLinks => FindSavedReading(gameId, reading) is not null,
        _ => reading.Article is { IsValid: true } article && article.Source == reading.Source,
    };

    private SavedGuide? FindSavedReading(Guid gameId, GuideResume reading) =>
        GetSavedGuides(gameId).FirstOrDefault(guide => guide.Id == reading.SavedId && guide.Url == reading.SavedUrl);

    private Task OpenInitialGuideAsync(Galgame game, PanelState state)
    {
        state.PreferredSource = GuideSearch.InitialSource(Data.DefaultSource, Data.LocalGuides.ContainsKey(game.Uuid));
        if (GetLastReading(game.Uuid) is not { } reading)
            return ShowSourceAsync(game, state, openMounted: true);

        state.BackStack.Clear();
        state.ResumedOnOpen = true;
        UpdateBackButtons(state);
        UpdateSourceToggleState(state);
        return OpenReadingAsync(game, state, reading, useCache: true);
    }

    private Task OpenReadingAsync(Galgame game, PanelState state, GuideResume reading, bool useCache)
    {
        var cached = useCache ? reading.Cached : null;
        return reading.Source switch
        {
            GuideSource.Local when Data.LocalGuides.TryGetValue(game.Uuid, out var local) && local is not null =>
                OpenLocalAsync(game, state, local),
            GuideSource.SavedLinks when FindSavedReading(game.Uuid, reading) is { } saved =>
                OpenSavedGuideAsync(game, state, saved, cached),
            _ when reading.Article is { IsValid: true } article =>
                OpenArticleAsync(game, state, article,
                    Data.ReadingHistory.TryGetValue(game.Uuid, out var history) ? history?.Get(article.Source)?.Title : null, cached),
            _ => ShowSourceAsync(game, state),
        };
    }

    private Task GoBackAsync(Galgame game, PanelState state)
    {
        if (state.BackStack.Count > 0)
        {
            PopBack(state);
            return Task.CompletedTask;
        }
        return state.ResumedOnOpen ? ShowSourceAsync(game, state) : Task.CompletedTask;
    }

    private void RememberReading(Guid gameId, GuideResume reading)
    {
        Data.LastReadings[gameId] = reading;
        CachedGuideText.Trim(Data.LastReadings);
        SaveData();
    }

    private void ForgetSavedReading(Guid gameId, Guid savedId)
    {
        if (Data.LastReadings.TryGetValue(gameId, out var reading) && reading.SavedId == savedId)
            Data.LastReadings.TryRemove(gameId, out _);
    }

    private IEnumerable<Guid> GetReadingGameIds() =>
        Data.LastReadings.Keys.Union(Data.ReadingHistory.Keys)
            .Union(Data.LocalGuides.Where(pair => pair.Value?.Bookmark is not null).Select(pair => pair.Key))
            .Union(Data.SavedGuides.Where(pair => pair.Value.Any(guide => guide.Bookmark is not null))
                .Select(pair => pair.Key));

    private IEnumerable<Guid> GetGameCacheIds() => GetReadingGameIds()
        .Union(_guidePages.GetCachedGameIds())
        .Union(FloatingPanels.Values.Where(panel => panel.ProgressRing?.IsActive == true)
            .Select(panel => panel.Game.Uuid));

    private Guid[] GetCompletedGameCacheIds()
    {
        var cachedGames = GetGameCacheIds().ToHashSet();
        return _hostApi.GetAllGames()
            .Where(game => game.PlayType == PlayType.Played && cachedGames.Contains(game.Uuid))
            .Select(game => game.Uuid)
            .Distinct()
            .ToArray();
    }

    private void ClearGameGuideCache(Guid gameId)
    {
        FloatingPanels.TryGetValue(gameId, out PanelState? panel);
        if (panel is not null)
        {
            CancelPanelRequest(panel);
            ResetReader(panel);
        }

        _guidePages.Clear(gameId);
        Data.LastReadings.TryRemove(gameId, out _);
        Data.ReadingHistory.TryRemove(gameId, out _);
        if (Data.LocalGuides.TryGetValue(gameId, out var local) && local is { Bookmark: not null })
            Data.LocalGuides.TryUpdate(gameId, local with { Bookmark = null }, local);
        if (Data.SavedGuides.TryGetValue(gameId, out var guides) && guides.Any(guide => guide.Bookmark is not null))
            Data.SavedGuides.TryUpdate(gameId, guides.Select(guide => guide with { Bookmark = null }).ToArray(), guides);

        if (panel is null) return;
        panel.BackStack.Clear();
        panel.ResumedOnOpen = false;
        panel.CurrentSource = GuideSource.Aggregate;
        panel.PreferredSource = GuideSearch.InitialSource(Data.DefaultSource, Data.LocalGuides.ContainsKey(gameId));
        panel.Content.Children.Clear();
        panel.Status.Visibility = Visibility.Visible;
        panel.Status.Text = "已清除此游戏的攻略缓存和阅读记录。";
        SetOpenSite(panel, null);
        UpdateBackButtons(panel);
        UpdateProgress(panel, false);
        UpdateSourceToggleState(panel);
        AddResultRow(panel.Content, 0, "重新查找攻略", () => ShowSourceAsync(panel.Game, panel));
    }

    private Flyout CreateGameCacheConfirmation(Galgame game)
    {
        var content = new StackPanel { Width = 205, Spacing = 8 };
        content.Children.Add(new TextBlock
        {
            Text = "清除这个游戏的网页缓存和阅读记录？挂载文件、已保存的链接和粘贴的文本仍可打开。",
            TextWrapping = TextWrapping.Wrap,
        });
        var flyout = new Flyout
        {
            Content = content,
            Placement = FlyoutPlacementMode.BottomEdgeAlignedRight,
            ShouldConstrainToRootBounds = false,
        };
        var clear = CreateGuideButton("清除", "只清除此游戏的攻略缓存和阅读记录");
        var cancel = CreateGuideButton("取消", "保留攻略缓存", flyout.Hide);
        content.Children.Add(GuideCommandRow(clear, cancel));
        clear.Click += async (_, _) =>
        {
            flyout.Hide();
            ClearGameGuideCache(game.Uuid);
            try
            {
                await SaveDataNowAsync();
            }
            catch (Exception error)
            {
                _hostApi.DeveloperEvent(msg: "保存攻略缓存清理结果失败", e: error);
                SaveData();
            }
        };
        return flyout;
    }
}
