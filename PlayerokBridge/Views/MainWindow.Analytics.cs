using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using PlayerokBridge.Playerok;

namespace PlayerokBridge.Views;

/// <summary>
/// Расширенная аналитика (вкладка «Аналитика» внутри «Профиля»).
/// Все показатели считаются за всё время по данным, которые уже загружены на вкладке «Мои товары»:
/// история продаж (_mySales) и список объявлений (_myListings).
/// </summary>
public sealed partial class MainWindow
{
    private Action? _refreshAnalytics;

    // Палитра графиков (зелёный/янтарный — как на макете), карточки — стеклянные, как во всём приложении.
    private static readonly Color AnGreenColor = Color.Parse("#6AA84F");
    private static readonly Color AnAmberColor = Color.Parse("#FFB400");
    private static readonly Color AnRedColor = Color.Parse("#FF5C5C");
    private static readonly IBrush AnGreen = new SolidColorBrush(AnGreenColor);
    private static readonly IBrush AnAmber = new SolidColorBrush(AnAmberColor);
    private static readonly IBrush AnRed = new SolidColorBrush(AnRedColor);
    private static readonly IBrush AnCardBg = new SolidColorBrush(Color.Parse("#A61D2430"));
    private static readonly IBrush AnCardBorder = new SolidColorBrush(Color.Parse("#334A5568"));
    private static readonly IBrush AnTrack = new SolidColorBrush(Color.FromArgb(0x26, 255, 255, 255));

    private sealed record AnRow(string Name, int Views, int Purchases, decimal Revenue);

    private static string AnMoney(decimal v) => v.ToString("#,0.##", Ru) + " ₽";

    // ---------- Оболочка: верхние вкладки «Профиль» / «Аналитика» ----------

    /// <summary>Оборачивает страницу профиля сверху переключателем «Профиль | Аналитика» с плавной сменой.</summary>
    private Control BuildProfileShell(Control profilePage)
    {
        var body = new ContentControl
        {
            Content = profilePage,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
        };
        var fade = new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(110) };
        body.Transitions = new Transitions { fade };

        var analyticsScroll = new ScrollViewer();
        var dirty = true;
        var current = 0;
        var token = 0;
        var hovered = -1;
        var pills = new List<Border>();
        var titles = new[] { "👤   Профиль", "📊   Аналитика" };

        void Paint()
        {
            for (var i = 0; i < pills.Count; i++)
            {
                var a = i == current ? (byte)0x55 : i == hovered ? (byte)0x38 : (byte)0x1C;
                pills[i].Background = new SolidColorBrush(Color.FromArgb(a, 255, 255, 255));
                if (pills[i].Child is TextBlock t)
                    t.FontWeight = i == current ? FontWeight.Bold : FontWeight.SemiBold;
            }
        }

        void RebuildAnalytics()
        {
            dirty = false;
            try { analyticsScroll.Content = BuildAnalyticsContent(); }
            catch (Exception ex)
            {
                analyticsScroll.Content = new TextBlock
                {
                    Text = "Не удалось построить аналитику: " + ex.Message,
                    Margin = new Thickness(14), TextWrapping = TextWrapping.Wrap, Opacity = .8
                };
            }
        }

        void ShowPage(int i)
        {
            body.Content = i == 0 ? profilePage : analyticsScroll;
            if (i == 1 && dirty) RebuildAnalytics();
            body.Opacity = 1;
        }

        void Show(int i)
        {
            if (i == current) return;
            current = i;
            Paint();
            var my = ++token;
            var cfg = _gui.Transition;
            if (!cfg.Enabled)
            {
                ShowPage(i);
                return;
            }
            // Короткое затухание старой страницы, смена содержимого и проявление новой.
            var ms = Math.Clamp(cfg.DurationMs / 2, 60, 220);
            fade.Duration = TimeSpan.FromMilliseconds(ms);
            body.Opacity = 0;
            _ = Task.Delay(ms).ContinueWith(_ => Dispatcher.UIThread.Post(() =>
            {
                if (my != token) return;
                ShowPage(i);
            }));
        }

        _refreshAnalytics = () =>
        {
            dirty = true;
            if (current == 1) RebuildAnalytics();
        };
        _analyticsRefreshRequest = () => _onProfileShown?.Invoke();

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(14, 12, 14, 0) };
        var radius = new CornerRadius(Math.Max(8, Math.Clamp(_gui.CornerRadius, 0, 40) * 0.65));
        for (var i = 0; i < titles.Length; i++)
        {
            var index = i;
            var pill = new Border
            {
                Padding = new Thickness(18, 8),
                CornerRadius = radius,
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                Background = new SolidColorBrush(Color.FromArgb(0x1C, 255, 255, 255)),
                Child = new TextBlock { Text = titles[i], FontSize = 13, FontWeight = FontWeight.SemiBold },
                Transitions = new Transitions
                {
                    new BrushTransition { Property = Border.BackgroundProperty, Duration = TimeSpan.FromMilliseconds(180) }
                }
            };
            pill.PointerEntered += (_, _) => { hovered = index; Paint(); };
            pill.PointerExited += (_, _) => { hovered = -1; Paint(); };
            pill.PointerReleased += (_, _) => Show(index);
            pills.Add(pill);
            bar.Children.Add(pill);
        }
        Paint();

        var shell = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        shell.Children.Add(bar);
        Grid.SetRow(body, 1);
        shell.Children.Add(body);
        return shell;
    }

    private Action? _analyticsRefreshRequest;

    // ---------- Построение аналитики ----------

    private static TextBlock AnText(string text, double size = 12, double opacity = 1, bool bold = false) => new()
    {
        Text = text, FontSize = size, Opacity = opacity,
        FontWeight = bold ? FontWeight.Bold : FontWeight.Normal,
        TextWrapping = TextWrapping.Wrap,
    };

    private static Border AnCard(string title, Control body, string? hint = null, Control? right = null)
    {
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var titles = new StackPanel { Spacing = 2 };
        titles.Children.Add(AnText(title, 16, 1, true));
        if (!string.IsNullOrEmpty(hint)) titles.Children.Add(AnText(hint, 11, .6));
        head.Children.Add(titles);
        if (right is not null)
        {
            Grid.SetColumn(right, 1);
            head.Children.Add(right);
        }
        var stack = new StackPanel { Spacing = 12 };
        stack.Children.Add(head);
        stack.Children.Add(body);
        return new Border
        {
            Background = AnCardBg, BorderBrush = AnCardBorder, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14), Padding = new Thickness(16), Child = stack,
        };
    }

    private static Border AnKpi(string title, string value, string sub, IBrush accent)
    {
        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(AnText(title, 12, .75, true));
        stack.Children.Add(new TextBlock { Text = value, FontSize = 24, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.NoWrap });
        stack.Children.Add(new TextBlock { Text = sub, FontSize = 11, Foreground = accent, TextWrapping = TextWrapping.Wrap });
        return new Border
        {
            Background = AnCardBg, BorderBrush = AnCardBorder, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14), Padding = new Thickness(16, 14), Child = stack,
        };
    }

    /// <summary>Тонкая полоса прогресса: ratio — доля заливки 0..1.</summary>
    private static Control AnBar(double ratio, IBrush fill, double height = 6)
    {
        ratio = double.IsNaN(ratio) ? 0 : Math.Clamp(ratio, 0, 1);
        var g = new Grid { Height = height };
        g.Children.Add(new Border { Background = AnTrack, CornerRadius = new CornerRadius(height / 2) });
        if (ratio > 0.002)
        {
            g.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(ratio, GridUnitType.Star)));
            g.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1 - ratio + 0.0001, GridUnitType.Star)));
            // Заливка поверх трека, занимает только свою долю ширины.
            var fillBar = new Border { Background = fill, CornerRadius = new CornerRadius(height / 2) };
            Grid.SetColumn(fillBar, 0);
            g.Children.Add(fillBar);
            Grid.SetColumnSpan(g.Children[0], 2);
        }
        return g;
    }

    private static Control AnLegendDot(IBrush color, string text)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        p.Children.Add(new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(5), Background = color, VerticalAlignment = VerticalAlignment.Center });
        p.Children.Add(AnText(text, 11, .8));
        return p;
    }

    private Control BuildAnalyticsContent()
    {
        var sales = _mySales;
        var listings = _myListings;

        static bool IsOk(Deal d) => d.Status is DealStatus.Confirmed or DealStatus.ConfirmedAutomatically;
        static bool IsBack(Deal d) => d.Status == DealStatus.RolledBack;

        var ok = sales.Where(IsOk).ToList();
        var back = sales.Where(IsBack).ToList();
        var wip = sales.Where(d => !IsOk(d) && !IsBack(d)).ToList();

        var root = new StackPanel { Margin = new Thickness(14), Spacing = 12 };

        // ----- заголовок -----
        var refreshBtn = new Button { Content = "↻ Обновить данные", VerticalAlignment = VerticalAlignment.Center };
        refreshBtn.Click += (_, _) => _analyticsRefreshRequest?.Invoke();
        var headGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        headGrid.Children.Add(new StackPanel
        {
            Spacing = 2,
            Children =
            {
                AnText("Расширенная аналитика", 20, 1, true),
                AnText("За всё время · данные PlayerOK", 12, .65),
            }
        });
        Grid.SetColumn(refreshBtn, 1);
        headGrid.Children.Add(refreshBtn);
        root.Children.Add(headGrid);

        if (sales.Count == 0 && listings.Count == 0)
        {
            root.Children.Add(AnCard("Данных пока нет",
                AnText("Продажи и объявления ещё не загружены. Нажмите «Обновить данные» — аналитика построится автоматически. " +
                       "Нужно подключение к PlayerOK (токен в «Настройках»).", 12, .75)));
            return root;
        }

        // ----- основные показатели -----
        var revenue = ok.Sum(d => d.Price);
        var avg = ok.Count > 0 ? revenue / ok.Count : 0m;
        var views = listings.Sum(l => Math.Max(0, l.ViewsCounter));
        var purchases = listings.Sum(l => Math.Max(0, l.DealsCounter));
        if (purchases == 0) purchases = ok.Count + wip.Count;
        double? conv = views > 0 ? purchases * 100.0 / views : null;
        var refundRate = sales.Count > 0 ? back.Count * 100.0 / sales.Count : 0;

        var kpis = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*") };
        void AddKpi(int col, Border b)
        {
            b.Margin = new Thickness(col == 0 ? 0 : 6, 0, col == 3 ? 0 : 6, 0);
            Grid.SetColumn(b, col);
            kpis.Children.Add(b);
        }
        AddKpi(0, AnKpi("Доход", AnMoney(revenue),
            wip.Count > 0 ? $"+ {AnMoney(wip.Sum(d => d.Price))} в процессе" : $"{ok.Count.ToString("N0", Ru)} успешных сделок", AnGreen));
        AddKpi(1, AnKpi("Средний чек", AnMoney(Math.Round(avg, 2)), "по успешным сделкам", AnAmber));
        AddKpi(2, AnKpi("Конверсия", conv is { } convVal ? convVal.ToString("0.##", Ru) + "%" : "—",
            views > 0 ? $"{purchases.ToString("N0", Ru)} покупок из {views.ToString("N0", Ru)} просмотров" : "нет данных о просмотрах", AnGreen));
        AddKpi(3, AnKpi("Возвраты", back.Count.ToString("N0", Ru),
            sales.Count > 0 ? $"{refundRate.ToString("0.#", Ru)}% от всех сделок" : "сделок пока нет", back.Count > 0 ? AnRed : AnGreen));
        root.Children.Add(kpis);

        // ----- динамика продаж по дням -----
        root.Children.Add(AnCard("Динамика продаж по дням", BuildSalesChart(sales),
            "дата покупки из PlayerOK; для новых событий сохраняется локальное время покупки"));

        // ----- соответствие сделок объявлениям -----
        var byId = new Dictionary<string, ListingSummary>();
        var byName = new Dictionary<string, ListingSummary>();
        foreach (var l in listings)
        {
            if (!string.IsNullOrEmpty(l.Id)) byId.TryAdd(l.Id, l);
            var n = (l.Name ?? "").Trim().ToLowerInvariant();
            if (n.Length > 0) byName.TryAdd(n, l);
        }
        string KeyOf(Deal d)
        {
            if (!string.IsNullOrEmpty(d.ItemId) && byId.ContainsKey(d.ItemId)) return "id:" + d.ItemId;
            var n = (d.ItemName ?? "").Trim().ToLowerInvariant();
            if (n.Length > 0 && byName.TryGetValue(n, out var l)) return "id:" + l.Id;
            return n.Length > 0 ? "name:" + n : "name:";
        }
        string DealTitle(Deal d) => string.IsNullOrWhiteSpace(d.ItemName) ? "Без названия" : d.ItemName!.Trim();

        // ----- самые продаваемые + конверсия -----
        var best = sales.Where(d => IsOk(d) || IsBack(d))
            .GroupBy(KeyOf)
            .Select(g => new
            {
                Name = byId.TryGetValue(g.Key.StartsWith("id:") ? g.Key[3..] : "", out var l)
                    ? l.Name
                    : DealTitle(g.OrderByDescending(x => x.CreatedAt ?? DateTimeOffset.MinValue).First()),
                Sold = g.Count(IsOk),
                Returned = g.Count(IsBack),
                Revenue = g.Where(IsOk).Sum(x => x.Price),
            })
            .Where(x => x.Sold > 0)
            .OrderByDescending(x => x.Sold).ThenByDescending(x => x.Revenue)
            .Take(5).ToList();

        var bestBody = new StackPanel { Spacing = 10 };
        if (best.Count == 0)
            bestBody.Children.Add(AnText("Успешных продаж пока нет.", 12, .65));
        var bestMax = best.Count > 0 ? best.Max(x => x.Sold) : 1;
        for (var i = 0; i < best.Count; i++)
        {
            var b = best[i];
            var rank = new Border
            {
                Width = 24, Height = 24, CornerRadius = new CornerRadius(12), VerticalAlignment = VerticalAlignment.Center,
                Background = i == 0 ? AnAmber : new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255)),
                Child = new TextBlock
                {
                    Text = (i + 1).ToString(), FontSize = 12, FontWeight = FontWeight.Bold,
                    Foreground = i == 0 ? Brushes.Black : null,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                }
            };
            var title = new TextBlock { Text = b.Name, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            ToolTip.SetTip(title, b.Name);
            var sub = AnText($"Продано {b.Sold.ToString("N0", Ru)}" + (b.Returned > 0 ? $" · возвратов {b.Returned}" : ""), 11, .65);
            var money = new TextBlock { Text = AnMoney(b.Revenue), FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center };
            var line = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            line.Children.Add(rank);
            var mid = new StackPanel { Spacing = 3, Children = { title, sub, AnBar(b.Sold / (double)bestMax, i == 0 ? AnAmber : AnGreen, 5) } };
            Grid.SetColumn(mid, 1); line.Children.Add(mid);
            Grid.SetColumn(money, 2); line.Children.Add(money);
            bestBody.Children.Add(line);
        }

        // Карточка конверсии: крупное число, просмотры → покупки, структура сделок.
        var convBody = new StackPanel { Spacing = 10 };
        convBody.Children.Add(new TextBlock
        {
            Text = conv is { } cv ? cv.ToString("0.##", Ru) + "%" : "—",
            FontSize = 34, FontWeight = FontWeight.Bold, Foreground = AnAmber
        });
        convBody.Children.Add(AnText(views > 0
            ? $"{views.ToString("N0", Ru)} просмотров → {purchases.ToString("N0", Ru)} покупок"
            : "Просмотры появятся после загрузки объявлений.", 12, .75));
        convBody.Children.Add(AnBar(views > 0 ? purchases / (double)views * 10 : 0, AnAmber, 8)); // шкала ×10 для наглядности малых значений
        convBody.Children.Add(AnText("Шкала увеличена в 10 раз — конверсия обычно единицы процентов.", 10, .45));

        var totalDeals = Math.Max(1, sales.Count);
        var seg = new Grid { Height = 10, Margin = new Thickness(0, 6, 0, 0) };
        var segCol = 0;
        void Seg(int n, IBrush b)
        {
            if (n <= 0) return;
            seg.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(n, GridUnitType.Star)));
            var s = new Border { Background = b, Margin = new Thickness(segCol == 0 ? 0 : 2, 0, 0, 0), CornerRadius = new CornerRadius(5) };
            Grid.SetColumn(s, segCol++);
            seg.Children.Add(s);
        }
        Seg(ok.Count, AnGreen); Seg(wip.Count, AnAmber); Seg(back.Count, AnRed);
        convBody.Children.Add(seg);
        convBody.Children.Add(new WrapPanel
        {
            Children =
            {
                AnMarginRight(AnLegendDot(AnGreen, $"Успешно {ok.Count * 100.0 / totalDeals:0.#}%")),
                AnMarginRight(AnLegendDot(AnAmber, $"В процессе {wip.Count * 100.0 / totalDeals:0.#}%")),
                AnMarginRight(AnLegendDot(AnRed, $"Возврат {back.Count * 100.0 / totalDeals:0.#}%")),
            }
        });

        var row2 = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        var bestCard = AnCard("Самые продаваемые товары", bestBody, "по числу успешных продаж");
        var convCard = AnCard("Конверсия просмотров в покупки", convBody, "все объявления");
        bestCard.Margin = new Thickness(0, 0, 6, 0);
        convCard.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(convCard, 1);
        row2.Children.Add(bestCard);
        row2.Children.Add(convCard);
        root.Children.Add(row2);

        // ----- доход по каждому объявлению -----
        var revByKey = ok.GroupBy(KeyOf).ToDictionary(g => g.Key, g => g.Sum(x => x.Price));
        var rows = new List<AnRow>();
        foreach (var l in listings)
        {
            revByKey.TryGetValue("id:" + l.Id, out var rev);
            if (rev == 0 && l.ViewsCounter <= 0 && l.DealsCounter <= 0) continue;
            rows.Add(new AnRow(l.Name, Math.Max(0, l.ViewsCounter), Math.Max(0, l.DealsCounter), rev));
        }
        // Продажи по объявлениям, которых уже нет в списке (удалены/завершены).
        foreach (var g in ok.GroupBy(KeyOf).Where(g => g.Key.StartsWith("name:")))
            rows.Add(new AnRow(DealTitle(g.First()) + " (нет в списке)", 0, g.Count(), g.Sum(x => x.Price)));
        rows = rows.OrderByDescending(r => r.Revenue).ThenByDescending(r => r.Purchases).ThenByDescending(r => r.Views).ToList();

        var table = new StackPanel { Spacing = 6 };
        const string cols = "*,78,78,78,120";
        Grid TableLine() => new() { ColumnDefinitions = new ColumnDefinitions(cols) };
        var header = TableLine();
        void Cell(Grid g, int col, Control c, HorizontalAlignment align = HorizontalAlignment.Right)
        {
            c.HorizontalAlignment = align;
            c.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(c, col);
            g.Children.Add(c);
        }
        Cell(header, 0, AnText("Объявление", 11, .6), HorizontalAlignment.Left);
        Cell(header, 1, AnText("Просмотры", 11, .6));
        Cell(header, 2, AnText("Покупки", 11, .6));
        Cell(header, 3, AnText("Конверсия", 11, .6));
        Cell(header, 4, AnText("Доход", 11, .6));
        table.Children.Add(header);

        if (rows.Count == 0)
            table.Children.Add(AnText("Нет объявлений с просмотрами, покупками или доходом.", 12, .65));
        var maxRev = rows.Count > 0 ? Math.Max(1m, rows.Max(r => r.Revenue)) : 1m;
        foreach (var r in rows.Take(50))
        {
            var line = TableLine();
            var name = new TextBlock { Text = r.Name, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            ToolTip.SetTip(name, r.Name);
            var nameBox = new StackPanel { Spacing = 4, Children = { name, AnBar((double)(r.Revenue / maxRev), AnGreen, 4) } };
            Cell(line, 0, nameBox, HorizontalAlignment.Stretch);
            Cell(line, 1, AnText(r.Views.ToString("N0", Ru), 12));
            Cell(line, 2, AnText(r.Purchases.ToString("N0", Ru), 12));
            var rc = r.Views > 0 ? r.Purchases * 100.0 / r.Views : (double?)null;
            Cell(line, 3, new TextBlock { Text = rc is { } rcVal ? rcVal.ToString("0.##", Ru) + "%" : "—", FontSize = 12, Foreground = rc is > 0 ? AnAmber : null, Opacity = rc is > 0 ? 1 : .55 });
            Cell(line, 4, new TextBlock { Text = AnMoney(r.Revenue), FontSize = 12, FontWeight = FontWeight.Bold, Foreground = r.Revenue > 0 ? AnGreen : null, Opacity = r.Revenue > 0 ? 1 : .55 });
            table.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x14, 255, 255, 255)),
                CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8), Child = line
            });
        }
        if (rows.Count > 50)
            table.Children.Add(AnText($"Показаны первые 50 из {rows.Count}.", 11, .55));
        root.Children.Add(AnCard("Доход по каждому объявлению", table, "учитываются только успешные сделки"));

        return root;
    }

    private static Control AnMarginRight(Control c)
    {
        c.Margin = new Thickness(0, 4, 14, 0);
        return c;
    }

    // ---------- График «Динамика продаж по дням» ----------

    private Control BuildSalesChart(IReadOnlyList<Deal> sales)
    {
        var dated = sales.Where(d => d.CreatedAt.HasValue)
            .Select(d => (Day: d.CreatedAt!.Value.ToLocalTime().Date, Deal: d)).ToList();
        if (dated.Count == 0)
            return AnText("Нет сделок с датами — график появится после первой продажи.", 12, .65);

        var first = dated.Min(x => x.Day);
        var last = new[] { dated.Max(x => x.Day), DateTime.Now.Date }.Max();
        var days = (last - first).Days + 1;
        // До четырёх месяцев сохраняем именно дневную детализацию; старую историю сжимаем.
        var step = days > 365 ? 30 : days > 120 ? 7 : 1;
        var count = (int)Math.Ceiling(days / (double)step);

        var sold = new int[count];
        var returned = new int[count];
        var money = new decimal[count];
        foreach (var (day, deal) in dated)
        {
            var i = Math.Min(count - 1, (day - first).Days / step);
            if (deal.Status == DealStatus.RolledBack) returned[i]++;
            else
            {
                sold[i]++;
                if (deal.Status is DealStatus.Confirmed or DealStatus.ConfirmedAutomatically) money[i] += deal.Price;
            }
        }

        var max = Math.Max(1, Enumerable.Range(0, count).Max(i => sold[i] + returned[i]));
        const double chartH = 170;
        var plot = new Grid { Height = chartH };
        // Базовая линия и средняя линия сетки.
        plot.Children.Add(new Border { Height = 1, VerticalAlignment = VerticalAlignment.Bottom, Background = AnTrack });
        plot.Children.Add(new Border { Height = 1, VerticalAlignment = VerticalAlignment.Center, Background = new SolidColorBrush(Color.FromArgb(0x12, 255, 255, 255)) });
        plot.Children.Add(new Border { Height = 1, VerticalAlignment = VerticalAlignment.Top, Background = new SolidColorBrush(Color.FromArgb(0x12, 255, 255, 255)) });

        var barsGrid = new Grid { Height = chartH };
        for (var i = 0; i < count; i++)
        {
            barsGrid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
            var d0 = first.AddDays(i * step);
            var d1 = first.AddDays(Math.Min(days - 1, (i + 1) * step - 1));
            var when = step == 1 ? d0.ToString("d MMMM yyyy", Ru) : $"{d0:dd.MM.yyyy} – {d1:dd.MM.yyyy}";
            var tip = $"{when}\nПродаж: {sold[i]}\nВозвратов: {returned[i]}\nДоход: {AnMoney(money[i])}";

            var col = new Grid { Background = Brushes.Transparent, Margin = new Thickness(count > 45 ? 0.5 : 1.5, 0) };
            ToolTip.SetTip(col, tip);
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Spacing = 0 };
            var hSold = (chartH - 6) * sold[i] / max;
            var hRet = (chartH - 6) * returned[i] / max;
            if (sold[i] > 0)
                stack.Children.Add(new Border { Height = Math.Max(2, hSold), Background = AnGreen, CornerRadius = new CornerRadius(3, 3, 0, 0) });
            if (returned[i] > 0)
                stack.Children.Add(new Border { Height = Math.Max(2, hRet), Background = AnAmber, CornerRadius = new CornerRadius(sold[i] > 0 ? 0 : 3, sold[i] > 0 ? 0 : 3, 0, 0) });
            col.Children.Add(stack);
            Grid.SetColumn(col, i);
            barsGrid.Children.Add(col);
        }
        plot.Children.Add(barsGrid);

        var axis = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*") };
        string Lbl(int i) => first.AddDays(i * step).ToString(step >= 30 ? "MM.yyyy" : "dd.MM", Ru);
        var l1 = AnText(Lbl(0), 11, .6); l1.HorizontalAlignment = HorizontalAlignment.Left;
        var l2 = AnText(Lbl(count / 2), 11, .6); l2.HorizontalAlignment = HorizontalAlignment.Center;
        var l3 = AnText(Lbl(count - 1), 11, .6); l3.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(l2, 1); Grid.SetColumn(l3, 2);
        axis.Children.Add(l1); axis.Children.Add(l2); axis.Children.Add(l3);

        // Масштабирование расширяет полотно графика, а горизонтальная прокрутка позволяет
        // рассматривать отдельные дни без потери общей последовательности дат.
        var zoom = new Slider { Minimum = 1, Maximum = 5, Value = 1, Width = 190 };
        var zoomValue = AnText("1,0×", 11, .75, true);
        var zoomHeader = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { AnText("Масштаб графика", 11, .7), zoom, zoomValue }
        };
        var chartBody = new StackPanel { Spacing = 6 };
        chartBody.Children.Add(plot);
        chartBody.Children.Add(axis);
        var chartScroll = new ScrollViewer
        {
            Content = chartBody,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        };

        void UpdateChartWidth()
        {
            var viewportWidth = chartScroll.Bounds.Width;
            if (!double.IsFinite(viewportWidth) || viewportWidth < 120)
                viewportWidth = 680;
            var width = Math.Max(320, viewportWidth) * zoom.Value;
            chartBody.Width = width;
            plot.Width = width;
            barsGrid.Width = width;
            axis.Width = width;
        }
        void UpdateZoomLabel() => zoomValue.Text = zoom.Value.ToString("0.0", Ru) + "×";
        zoom.PropertyChanged += (_, e) =>
        {
            if (e.Property != Avalonia.Controls.Primitives.RangeBase.ValueProperty) return;
            UpdateZoomLabel();
            UpdateChartWidth();
        };
        chartScroll.SizeChanged += (_, _) => UpdateChartWidth();
        UpdateZoomLabel();
        UpdateChartWidth();

        var totalSold = sold.Sum();
        var totalRet = returned.Sum();
        var legend = new WrapPanel
        {
            Children =
            {
                AnMarginRight(AnLegendDot(AnGreen, $"Продано: {totalSold.ToString("N0", Ru)}")),
                AnMarginRight(AnLegendDot(AnAmber, $"Возвраты: {totalRet.ToString("N0", Ru)}")),
                AnMarginRight(AnText(step == 1 ? "Шаг: день" : step == 7 ? "Шаг: неделя" : "Шаг: месяц", 11, .5)),
                AnMarginRight(AnText($"Пик: {max.ToString("N0", Ru)} за период", 11, .5)),
                AnMarginRight(AnText(sales.Count > dated.Count ? $"Без даты: {sales.Count - dated.Count}" : "Все даты определены", 11, .5)),
            }
        };

        return new StackPanel { Spacing = 8, Children = { zoomHeader, chartScroll, legend } };
    }
}
