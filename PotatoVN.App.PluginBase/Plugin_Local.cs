using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GalgameManager.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PotatoVN.App.PluginBase.Guides;
using Windows.Storage.Pickers;

namespace PotatoVN.App.PluginBase;

public partial class Plugin
{
    private async Task ShowLocalAsync(Galgame game, PanelState state, bool resume = false)
    {
        state.CurrentSource = GuideSource.Local;
        Data.LocalGuides.TryGetValue(game.Uuid, out LocalGuideMount? mount);
        if (resume && mount is not null)
        {
            await OpenLocalAsync(game, state, mount);
            return;
        }

        string? folder = game.LocalPath;
        string? hostText = game.TextPath;
        await RunPanelLoadAsync(state, "正在查找本地攻略文本…", async token =>
        {
            var candidates = await Task.Run(() => LocalGuideFile.Discover(folder, hostText, token), token);
            if (!IsCurrentPanelRequest(state, token)) return;

            state.Status.Text = mount is not null
                ? $"已挂载：{Path.GetFileName(mount.Path)}"
                : candidates.Count > 0
                    ? "选择要挂载的攻略文本。"
                    : "可手动选择攻略文本。压缩包中的攻略请先解压。";

            var encodingBox = new ComboBox
            {
                Header = "文件编码",
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            foreach (var (value, label) in LocalGuideFile.Encodings)
                encodingBox.Items.Add(new ComboBoxItem { Content = label, Tag = value });
            encodingBox.SelectedItem = encodingBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => item.Tag is LocalGuideEncoding value && value == (mount?.Encoding ?? LocalGuideEncoding.Auto))
                ?? encodingBox.Items[0];
            AutomationProperties.SetHelpText(encodingBox, "正文乱码时，可选择攻略原来的编码再打开。");
            state.Content.Children.Add(encodingBox);

            LocalGuideEncoding SelectedEncoding() => encodingBox.SelectedItem is ComboBoxItem { Tag: LocalGuideEncoding value }
                ? value : LocalGuideEncoding.Auto;

            if (mount is not null)
            {
                AddResultRow(state.Content, 0, "打开已挂载的攻略", () =>
                    OpenLocalAsync(game, state, mount with { Encoding = SelectedEncoding() }));
            }

            AddResultRow(state.Content, 0, "手动选择文本文件…", async () =>
            {
                if (state.IsPickingLocalFile) return;
                var request = state.RequestCancellation;
                string? path;
                state.IsPickingLocalFile = true;
                try { path = await PickLocalTextAsync(state.Window); }
                finally { state.IsPickingLocalFile = false; }
                if (path is null || !ReferenceEquals(request, state.RequestCancellation) ||
                    state.CurrentSource != GuideSource.Local || !state.Window.AppWindow.IsVisible) return;
                await OpenLocalAsync(game, state, LocalGuideFile.Mount(path, game.LocalPath, SelectedEncoding()));
            });

            string? mountedPath = mount is null ? null : LocalGuideFile.Resolve(mount, folder);
            foreach (var candidate in candidates)
            {
                if (string.Equals(candidate.Path, mountedPath, StringComparison.OrdinalIgnoreCase)) continue;
                string filename = Path.GetFileName(candidate.Path);
                string label = candidate.RememberedByHost ? $"PotatoVN 已记住：{filename}" : filename;
                AddResultRow(state.Content, 0, label, () =>
                    OpenLocalAsync(game, state, LocalGuideFile.Mount(candidate.Path, game.LocalPath, SelectedEncoding())));
            }

            if (mount is not null)
            {
                AddResultRow(state.Content, 0, "解除攻略挂载", async () =>
                {
                    if (Data.LocalGuides.TryRemove(game.Uuid, out _))
                    {
                        if (Data.LastReadings.TryGetValue(game.Uuid, out var reading) && reading.Source == GuideSource.Local)
                            Data.LastReadings.TryRemove(game.Uuid, out _);
                        SaveData();
                    }
                    await ShowLocalAsync(game, state);
                });
            }
        });
    }

    private Task OpenLocalAsync(Galgame game, PanelState state, LocalGuideMount mount)
    {
        state.CurrentSource = GuideSource.Local;
        string? path = LocalGuideFile.Resolve(mount, game.LocalPath);
        return RunPanelLoadAsync(state, "正在打开本地攻略…", async token =>
        {
            var reading = new GuideResume(GuideSource.Local);
            state.ActiveReading = reading;
            string? text = null;
            string? error = null;
            try
            {
                if (path is null) throw new FileNotFoundException();
                text = await Task.Run(() => LocalGuideFile.ReadAsync(path, mount.Encoding, token), token);
            }
            catch (InvalidDataException e) { error = e.Message; }
            catch (FileNotFoundException) { error = "找不到攻略文件。可以更换文件，或在移动游戏后重新挂载。"; }
            catch (DirectoryNotFoundException) { error = "找不到攻略文件夹。请更换文件。"; }
            catch (UnauthorizedAccessException) { error = "无法读取攻略文件，请检查权限或更换文件。"; }
            catch (IOException) { error = "暂时无法读取攻略文件，请重试。"; }
            if (!IsCurrentPanelRequest(state, token)) return;

            if (text is null)
            {
                state.Status.Text = error ?? "无法读取攻略文件。";
                AddResultRow(state.Content, 0, "更换文件或选择编码", () => ShowLocalAsync(game, state));
                return;
            }

            Data.LocalGuides.TryGetValue(game.Uuid, out LocalGuideMount? previous);
            GuideBookmark? marker = previous is not null && SameLocalFile(previous, mount) ? previous.Bookmark : null;
            LocalGuideMount saved = mount with { Bookmark = marker };
            PresentArticle(state, text, marker, position => RememberLocalPosition(game.Uuid, saved, position));
            if (previous != saved)
                Data.LocalGuides[game.Uuid] = saved;
            RememberReading(game.Uuid, reading);
        });
    }

    private void RememberLocalPosition(Guid gameId, LocalGuideMount reader, GuideBookmark bookmark)
    {
        while (Data.LocalGuides.TryGetValue(gameId, out LocalGuideMount? current) &&
               current is not null && SameLocalFile(current, reader) && current.Encoding == reader.Encoding &&
               current.Bookmark != bookmark)
        {
            if (!Data.LocalGuides.TryUpdate(gameId, current with { Bookmark = bookmark }, current)) continue;
            SaveData();
            return;
        }
    }

    private static bool SameLocalFile(LocalGuideMount first, LocalGuideMount second) =>
        first.InGameFolder == second.InGameFolder &&
        string.Equals(first.Path, second.Path, StringComparison.OrdinalIgnoreCase);

    private static async Task<string?> PickLocalTextAsync(Window window)
    {
        var picker = new FileOpenPicker { CommitButtonText = "挂载攻略", ViewMode = PickerViewMode.List };
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        picker.FileTypeFilter.Add("*");
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }
}
