using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using PlayerokBridge.Core;

namespace PlayerokBridge.Views;

/// <summary>Журнал событий с фильтрами: уровень, тема и поиск по тексту. Ошибки подключения и опроса видны без чтения файлов.</summary>
internal static class LogView
{
    private const int MaxShown = 700;

    private static readonly (string Label, LogTopic Topic)[] Topics =
    {
        ("Все темы", LogTopic.All), ("Подключение", LogTopic.Connection), ("Опрос чатов", LogTopic.Polling),
        ("Покупки", LogTopic.Purchases), ("Telegram", LogTopic.Telegram), ("Python-бот", LogTopic.Bot),
    };

    private static readonly string[] Levels = { "Все сообщения", "Предупреждения и ошибки", "Только ошибки" };

    /// <param name="fixedHeight">Высота области текста; null — занять всё доступное место (для отдельного окна).</param>
    public static Control Build(LogBuffer buffer, double? fixedHeight)
    {
        var level = new ComboBox { ItemsSource = Levels, SelectedIndex = 0, MinWidth = 210 };
        var topic = new ComboBox { ItemsSource = Topics.Select(t => t.Label).ToList(), SelectedIndex = 0, MinWidth = 150 };
        var search = new TextBox { Watermark = "Поиск по тексту…", MinWidth = 200 };
        var follow = new CheckBox { Content = "Следить за новыми", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
        var clear = new Button { Content = "🗑 Очистить" };
        var copy = new Button { Content = "📋 Копировать" };
        var counts = new TextBlock { Opacity = .75, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };

        var list = new StackPanel { Spacing = 1 };
        var scroll = new ScrollViewer
        {
            Content = new Border { Padding = new Thickness(10, 8), Child = list },
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };
        if (fixedHeight is double h) scroll.Height = h;
        var scrollBox = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#66000000")),
            CornerRadius = new CornerRadius(10),
            Child = scroll,
        };

        var toolbar = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        foreach (var c in new Control[] { level, topic, search, follow, clear, copy })
        {
            c.Margin = new Thickness(0, 0, 8, 6);
            toolbar.Children.Add(c);
        }

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        Grid.SetRow(toolbar, 0); root.Children.Add(toolbar);
        Grid.SetRow(scrollBox, 1); root.Children.Add(scrollBox);
        counts.Margin = new Thickness(2, 8, 0, 0);
        Grid.SetRow(counts, 2); root.Children.Add(counts);

        bool Pass(LogEntry e)
        {
            var lv = level.SelectedIndex;
            if (lv == 1 && e.Level == LogLevel.Info) return false;
            if (lv == 2 && e.Level != LogLevel.Error) return false;
            var t = Topics[Math.Max(0, topic.SelectedIndex)].Topic;
            if (t != LogTopic.All && (e.Topics & t) == 0) return false;
            var q = search.Text?.Trim();
            if (!string.IsNullOrEmpty(q) && e.Text.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) return false;
            return true;
        }

        Control Line(LogEntry e)
        {
            var tb = new SelectableTextBlock
            {
                Text = e.Text,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Consolas,Menlo,monospace"),
                FontSize = 12.5,
            };
            if (e.Level == LogLevel.Error) tb.Foreground = new SolidColorBrush(Color.Parse("#FF6B6B"));
            else if (e.Level == LogLevel.Warning) tb.Foreground = new SolidColorBrush(Color.Parse("#FFC857"));
            else if ((e.Topics & LogTopic.Purchases) != 0) tb.Foreground = new SolidColorBrush(Color.Parse("#7BE08F"));
            return tb;
        }

        void UpdateCounts()
        {
            var all = buffer.Snapshot();
            var errors = all.Count(e => e.Level == LogLevel.Error);
            var warnings = all.Count(e => e.Level == LogLevel.Warning);
            counts.Text = $"Показано: {list.Children.Count} из {all.Count} · ошибок: {errors} · предупреждений: {warnings}";
        }

        void ToEnd() => Dispatcher.UIThread.Post(
            () => scroll.Offset = new Vector(0, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height)),
            DispatcherPriority.Background);

        void Rebuild()
        {
            list.Children.Clear();
            var matching = buffer.Snapshot().Where(Pass).ToList();
            foreach (var e in matching.Skip(Math.Max(0, matching.Count - MaxShown))) list.Children.Add(Line(e));
            UpdateCounts();
            if (follow.IsChecked == true) ToEnd();
        }

        void OnAdded(LogEntry e)
        {
            if (Pass(e))
            {
                list.Children.Add(Line(e));
                if (list.Children.Count > MaxShown) list.Children.RemoveAt(0);
                if (follow.IsChecked == true) ToEnd();
            }
            UpdateCounts();
        }

        level.SelectionChanged += (_, _) => Rebuild();
        topic.SelectionChanged += (_, _) => Rebuild();
        search.TextChanged += (_, _) => Rebuild();
        clear.Click += (_, _) => buffer.Clear();
        copy.Click += async (_, _) =>
        {
            try
            {
                var clipboard = TopLevel.GetTopLevel(root)?.Clipboard;
                if (clipboard is null) return;
                await clipboard.SetTextAsync(string.Join("\n", buffer.Snapshot().Where(Pass).Select(e => e.Text)));
                counts.Text = "Скопировано: " + list.Children.Count + " строк(и).";
            }
            catch (Exception ex) { counts.Text = "Не удалось скопировать: " + ex.Message; }
        };

        // Подписка только пока журнал на экране: при смене вкладки лишняя работа не делается.
        void OnCleared() => Rebuild();
        root.AttachedToVisualTree += (_, _) => { buffer.Added += OnAdded; buffer.Cleared += OnCleared; Rebuild(); };
        root.DetachedFromVisualTree += (_, _) => { buffer.Added -= OnAdded; buffer.Cleared -= OnCleared; };
        return root;
    }
}

/// <summary>Отдельное окно журнала (кнопка на «Обзоре» и пункт меню в трее).</summary>
internal sealed class LogWindow : Window
{
    public LogWindow(LogBuffer buffer)
    {
        Title = "Журнал — PlayerOK Bridge";
        Width = 940; Height = 580; MinWidth = 620; MinHeight = 320;
        Background = new SolidColorBrush(Color.Parse("#0E1015"));
        Foreground = new SolidColorBrush(Color.Parse("#F2F4F8"));
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Content = new Border { Padding = new Thickness(14), Child = LogView.Build(buffer, null) };
    }
}
