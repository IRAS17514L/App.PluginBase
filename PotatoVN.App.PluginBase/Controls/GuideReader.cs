using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using PotatoVN.App.PluginBase.Guides;
using Windows.Foundation;
using Windows.System;

namespace PotatoVN.App.PluginBase;

internal sealed class GuideReader
{
    private readonly GuideText _document;
    private readonly ScrollViewer _scroll;
    private readonly Grid _findHost;
    private readonly Action<GuideBookmark> _remember;
    private readonly Run _run;
    private readonly TextBox _query = new() { PlaceholderText = "查找正文", MaxLength = 200 };
    private readonly TextBlock _count = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _previous = new() { Content = "↑", IsEnabled = false };
    private readonly Button _next = new() { Content = "↓", IsEnabled = false };
    private IReadOnlyList<int> _matches = [];
    private GuideBookmark? _bookmark;
    private int _selected;
    private int _queryLength;
    private bool _ready;

    internal TextBlock Body { get; }

    internal GuideReader(string body, GuideBookmark? bookmark, ScrollViewer scroll, Grid findHost,
        Action<GuideBookmark> remember)
    {
        _document = new GuideText(body);
        _bookmark = bookmark;
        _scroll = scroll;
        _findHost = findHost;
        _remember = remember;
        Body = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            LineHeight = 20,
            IsTextSelectionEnabled = true,
            Padding = new Thickness(4, 0, 4, 4),
        };
        _run = new Run { Text = body };
        Body.Inlines.Add(_run);
        Body.Loaded += OnLoaded;
        AutomationProperties.SetName(_query, "查找正文");
        SetLabel(_previous, "上一个匹配项");
        SetLabel(_next, "下一个匹配项");
        _previous.Click += (_, _) => Select(-1);
        _next.Click += (_, _) => Select(1);
        _query.TextChanged += (_, _) => Search();
        _query.KeyDown += (_, args) =>
        {
            if (args.Key == VirtualKey.Enter)
            {
                Select(1);
                args.Handled = true;
            }
            else if (args.Key == VirtualKey.Escape)
            {
                CloseFind();
                args.Handled = true;
            }
        };
    }

    internal void OpenFind()
    {
        if (_findHost.Children.Count == 0)
        {
            var close = new Button { Content = "×" };
            SetLabel(close, "关闭查找");
            close.Click += (_, _) => CloseFind();

            Grid row = new() { ColumnSpacing = 4, Margin = new Thickness(4, 2, 4, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < 4; i++) row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            FrameworkElement[] controls = [_query, _count, _previous, _next, close];
            for (int i = 0; i < controls.Length; i++)
            {
                Grid.SetColumn(controls[i], i);
                row.Children.Add(controls[i]);
            }
            _findHost.Children.Add(row);
        }
        _findHost.Visibility = Visibility.Visible;
        _query.Focus(FocusState.Programmatic);
        _query.SelectAll();
    }

    internal void SavePosition()
    {
        if (!_ready || Body.XamlRoot is null || _scroll.ScrollableHeight <= 0) return;
        double y = _scroll.VerticalOffset;
        int paragraph = ParagraphAt(y);
        double progress = Math.Round(Math.Clamp(y / _scroll.ScrollableHeight, 0, 1), 3);
        GuideBookmark position = _document.Bookmark(paragraph, progress);
        if (position == _bookmark) return;
        _bookmark = position;
        _remember(position);
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        Body.Loaded -= OnLoaded;
        Body.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (Body.XamlRoot is null) return;
            double y = 0;
            if (_bookmark is { } bookmark)
            {
                int paragraph = _document.FindParagraph(bookmark);
                double progress = double.IsFinite(bookmark.Progress) ? Math.Clamp(bookmark.Progress, 0, 1) : 0;
                y = paragraph >= 0 && TryLocate(_document.ParagraphStarts[paragraph], out Rect rect)
                    ? rect.Top
                    : progress * _scroll.ScrollableHeight;
            }
            ScrollTo(y);
            _ready = true;
        });
    }

    private int ParagraphAt(double y)
    {
        int first = 0;
        int last = _document.ParagraphStarts.Count - 1;
        int nearest = -1;
        while (first <= last)
        {
            int middle = first + (last - first) / 2;
            if (!TryLocate(_document.ParagraphStarts[middle], out Rect rect)) return -1;
            if (rect.Top <= y + 1)
            {
                nearest = middle;
                first = middle + 1;
            }
            else
            {
                last = middle - 1;
            }
        }
        return Math.Max(0, nearest);
    }

    private void Search()
    {
        Body.TextHighlighters.Clear();
        string query = _query.Text.Trim();
        _queryLength = query.Length;
        _matches = _document.Find(query);
        _selected = 0;
        _previous.IsEnabled = _next.IsEnabled = _matches.Count > 0;
        if (_matches.Count == 0)
        {
            _count.Text = query.Length == 0 ? "" : "无匹配";
            return;
        }
        HighlightCurrent();
    }

    private void Select(int step)
    {
        if (_matches.Count == 0) return;
        _selected = (_selected + step + _matches.Count) % _matches.Count;
        HighlightCurrent();
    }

    private void HighlightCurrent()
    {
        Body.TextHighlighters.Clear();
        int start = _matches[_selected];
        var highlight = new TextHighlighter
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 251, 213, 116)),
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 25, 22, 18)),
        };
        highlight.Ranges.Add(new TextRange { StartIndex = start, Length = _queryLength });
        Body.TextHighlighters.Add(highlight);
        _count.Text = $"{_selected + 1}/{_matches.Count}";

        if (TryLocate(start, out Rect rect) && (rect.Top < _scroll.VerticalOffset ||
            rect.Bottom > _scroll.VerticalOffset + _scroll.ViewportHeight))
            ScrollTo(rect.Top - 12);
    }

    private bool TryLocate(int offset, out Rect rect)
    {
        rect = default;
        if (Body.XamlRoot is null || Body.ActualWidth <= 0) return false;
        try
        {
            TextPointer? position = offset == 0 ? _run.ContentStart :
                _run.ContentStart.GetPositionAtOffset(offset, LogicalDirection.Forward);
            if (position is null) return false;
            rect = position.GetCharacterRect(LogicalDirection.Forward);
            return rect.Height > 0 && double.IsFinite(rect.Top);
        }
        catch (COMException) { return false; }
        catch (ArgumentException) { return false; }
    }

    private void ScrollTo(double y) =>
        _scroll.ChangeView(null, Math.Clamp(y, 0, _scroll.ScrollableHeight), null, true);

    private void CloseFind()
    {
        Body.TextHighlighters.Clear();
        _query.Text = "";
        _findHost.Visibility = Visibility.Collapsed;
        _scroll.Focus(FocusState.Programmatic);
    }

    private static void SetLabel(Button button, string text)
    {
        AutomationProperties.SetName(button, text);
        ToolTipService.SetToolTip(button, text);
    }
}
