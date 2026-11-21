using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using PlayerokBridge.Core;
using PlayerokBridge.Playerok;

namespace PlayerokBridge.Views;

/// <summary>Вкладка «Мои товары»: закреплённая шапка, поиск, сортировка, карточки/список, адаптивная сетка.</summary>
public sealed partial class MainWindow
{
    private static readonly string[] MySortTitles =
    {
        "По игре", "Сначала дорогие", "Сначала дешёвые", "Больше просмотров",
        "Больше покупок", "Выше конверсия", "Больше выручки", "Сначала новые",
    };

    /// <summary>Выручка по успешным сделкам для каждого объявления (по ID товара, иначе по названию).</summary>
    private Dictionary<string, decimal> RevenueByListing()
    {
        var ids = _myListings.Select(l => l.Id).ToHashSet();
        var byName = new Dictionary<string, string>();
        foreach (var l in _myListings) byName.TryAdd(TextNorm.Normalize(l.Name).Trim(), l.Id);
        var map = new Dictionary<string, decimal>();
        foreach (var d in _mySales)
        {
            if (d.Status is not (DealStatus.Confirmed or DealStatus.ConfirmedAutomatically)) continue;
            string? id = null;
            if (!string.IsNullOrEmpty(d.ItemId) && ids.Contains(d.ItemId)) id = d.ItemId;
            else if (byName.TryGetValue(TextNorm.Normalize(d.ItemName).Trim(), out var found)) id = found;
            if (id is null) continue;
            map[id] = (map.TryGetValue(id, out var cur) ? cur : 0m) + d.Price;
        }
        return map;
    }

    private Control BuildMyProductsTab()
    {
        // ---------- состояние (объявляем первым: локальные функции ниже его используют) ----------
        var tabGroup = 0;                              // 0 — все, 1 — активные, 2 — на проверке, 3 — черновики, 4 — завершённые
        var viewMode = _gui.MyProductsList ? 1 : 0;    // 0 — карточки, 1 — список
        var cardWidth = 232.0;
        var statusToken = 0;
        var cardBorders = new List<Border>();

        var cards = new WrapPanel { Margin = new Thickness(14, 4, 14, 0) };
        var listPanel = new StackPanel { Margin = new Thickness(14, 4, 14, 0), Spacing = 6, IsVisible = false };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        var statusBox = new Border
        {
            Margin = new Thickness(14, 0, 14, 6), Padding = new Thickness(12, 7), IsVisible = false,
            CornerRadius = new CornerRadius(10), Background = AnCardBg, BorderBrush = AnCardBorder,
            BorderThickness = new Thickness(1), Child = status,
        };
        var loading = new ProgressBar { IsIndeterminate = true, Height = 3, IsVisible = false, Margin = new Thickness(14, 0) };
        var updated = new TextBlock { FontSize = 12, Opacity = .65 };

        var search = new TextBox { Watermark = "🔍  Поиск по названию товара или игре", HorizontalAlignment = HorizontalAlignment.Stretch };
        var clearSearch = new Button
        {
            Content = "✕", Padding = new Thickness(8, 2), Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), IsVisible = false, FontSize = 11,
        };
        search.InnerRightContent = clearSearch;
        var gameFilter = new ComboBox { MinWidth = 170 };
        var sort = new ComboBox { MinWidth = 180, ItemsSource = MySortTitles, SelectedIndex = Math.Clamp(_gui.MyProductsSort, 0, MySortTitles.Length - 1) };
        var period = new ComboBox { Width = 150, ItemsSource = new[] { "За день", "За неделю", "За месяц", "За всё время" }, SelectedIndex = 0 };

        TextBlock Val() => new() { Text = "—", FontSize = 20, FontWeight = FontWeight.Bold };
        var purchaseValue = Val();
        var revenueValue = Val();
        var viewsValue = Val();
        var activeValue = Val();
        var viewsHint = new TextBlock { Text = "История просмотров начнёт собираться после первого обновления.", Opacity = .55, FontSize = 11, TextWrapping = TextWrapping.Wrap };

        var refresh = new Button { Content = "↻ Обновить" };
        var create = new Button { Content = "＋ Создать товар", FontWeight = FontWeight.SemiBold };
        ToolTip.SetTip(create, "Откроет встроенный мастер создания объявления PlayerOK");

        void Say(string text)
        {
            status.Text = text;
            statusBox.IsVisible = !string.IsNullOrWhiteSpace(text);
            var my = ++statusToken;
            // Успешные сообщения сами исчезают, ошибки и вопросы остаются.
            if (text.StartsWith("✅"))
                _ = Task.Delay(6000).ContinueWith(_ => Dispatcher.UIThread.Post(() =>
                {
                    if (my == statusToken) { status.Text = ""; statusBox.IsVisible = false; }
                }));
        }

        Border Kpi(string label, TextBlock value) => new()
        {
            Background = AnCardBg, BorderBrush = AnCardBorder, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 9), Margin = new Thickness(0, 0, 8, 0),
            Child = new StackPanel { Spacing = 2, Children = { new TextBlock { Text = label, FontSize = 11, Opacity = .68 }, value } }
        };

        var kpiGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*") };
        var kpiCards = new[]
        {
            Kpi("Покупки за период", purchaseValue), Kpi("Оборот за период", revenueValue),
            Kpi("Просмотры за период", viewsValue), Kpi("Активные товары", activeValue),
        };
        for (var i = 0; i < kpiCards.Length; i++)
        {
            if (i == kpiCards.Length - 1) kpiCards[i].Margin = new Thickness(0);
            Grid.SetColumn(kpiCards[i], i);
            kpiGrid.Children.Add(kpiCards[i]);
        }

        // ---------- вкладки по статусам ----------
        var tabLabels = new[] { "Все", "Активные", "На проверке", "Черновики", "Завершённые" };
        var tabItems = new List<(Border Box, TextBlock Text)>();
        var tabsPanel = new WrapPanel { Margin = new Thickness(14, 0, 14, 0) };
        for (var i = 0; i < tabLabels.Length; i++)
        {
            var idx = i;
            var label = new TextBlock { Text = tabLabels[i], FontWeight = FontWeight.SemiBold, FontSize = 13 };
            var box = new Border
            {
                CornerRadius = new CornerRadius(16), Padding = new Thickness(14, 6), Margin = new Thickness(0, 0, 8, 6),
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand), Child = label,
            };
            box.PointerReleased += (_, _) => { tabGroup = idx; ApplyFilters(); };
            tabItems.Add((box, label));
            tabsPanel.Children.Add(box);
        }

        // ---------- переключатель вида ----------
        var viewBtns = new List<Border>();
        void PaintView()
        {
            for (var i = 0; i < viewBtns.Count; i++)
                viewBtns[i].Background = new SolidColorBrush(Color.Parse(i == viewMode ? "#4D83FF" : "#2A303C"));
        }
        var viewBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        foreach (var (idx, glyph, tip) in new[] { (0, "▦", "Карточки"), (1, "☰", "Список") })
        {
            var mode = idx;
            var b = new Border
            {
                Padding = new Thickness(11, 6), CornerRadius = new CornerRadius(9),
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                Child = new TextBlock { Text = glyph, FontSize = 15 },
            };
            ToolTip.SetTip(b, tip);
            b.PointerReleased += (_, _) =>
            {
                if (viewMode == mode) return;
                viewMode = mode;
                _gui.MyProductsList = mode == 1;
                try { _gui.Save(); } catch (Exception logEx) { global::PlayerokBridge.Program.LogCrash("MainWindow.MyProducts.cs:157", logEx); }
                PaintView();
                ApplyFilters();
            };
            viewBtns.Add(b);
            viewBar.Children.Add(b);
        }
        PaintView();

        // ---------- показатели ----------
        DateTimeOffset? PeriodStart()
        {
            var today = DateTime.Now.Date;
            var weekStart = today.AddDays(-(((int)today.DayOfWeek + 6) % 7)); // неделя с понедельника
            var monthStart = new DateTime(today.Year, today.Month, 1);
            return period.SelectedIndex switch
            {
                0 => new DateTimeOffset(today),
                1 => new DateTimeOffset(weekStart),
                2 => new DateTimeOffset(monthStart),
                _ => null,
            };
        }

        IEnumerable<Deal> SalesInPeriod()
        {
            var since = PeriodStart();
            // «За всё время» не фильтрует по дате: у старых сделок дата может отсутствовать.
            return _mySales.Where(d => d.Status != DealStatus.RolledBack &&
                (since is null || (d.CreatedAt.HasValue && d.CreatedAt.Value >= since.Value)));
        }

        void UpdateMetrics()
        {
            var sales = SalesInPeriod().ToList();
            purchaseValue.Text = sales.Count.ToString("N0", Ru);
            revenueValue.Text = $"{sales.Sum(d => d.Price):0.##} ₽";
            activeValue.Text = _myListings.Count(x => ListingStatus(x.StatusName).Group == 1).ToString("N0", Ru);

            if (PeriodStart() is not { } since)
            {
                viewsValue.Text = _myListings.Sum(x => Math.Max(0, x.ViewsCounter)).ToString("N0", Ru);
                viewsHint.Text = "Просмотры накопительные, по данным PlayerOK.";
            }
            else
            {
                var deltas = _myListings
                    .Select(x => _listingStats.GetViewsSince(x.Id, x.ViewsCounter, since))
                    .Where(x => x.HasValue).Select(x => x!.Value).ToList();
                viewsValue.Text = deltas.Count == 0 ? "—" : deltas.Sum().ToString("N0", Ru);
                viewsHint.Text = deltas.Count == 0
                    ? "Для периода ещё нет двух снимков просмотров — обновляйте товары, чтобы накопить историю."
                    : "Прирост просмотров от первого снимка внутри периода; история копится с первого обновления.";
            }
        }

        // ---------- действия ----------
        async Task RaiseAsync(ListingSummary item)
        {
            var isDraft = item.StatusName is "DRAFT" or "EXPIRED" or "DECLINED";
            try
            {
                Say("Получаю доступные варианты продвижения…");
                var options = await _host.GetPriorityStatusesForUiAsync(item.Id, item.Price, CancellationToken.None);
                if (options.Count == 0)
                {
                    Say("PlayerOK не вернул доступные варианты для этого объявления.");
                    return;
                }
                var chosen = await ChoosePriorityStatusAsync(options, item, isDraft);
                if (chosen is null) { Say(""); return; }
                Say(chosen.Price == 0 ? "Выполняю действие…" : $"Подтверждено списание {chosen.Price} ₽. Выполняю…");
                await _host.SetListingPriorityForUiAsync(item.Id, chosen.Id, isDraft, CancellationToken.None);
                Say(isDraft ? "✅ Объявление отправлено на публикацию." : "✅ Приоритет объявления обновлён.");
                await RefreshDataAsync();
                await RefreshBalanceAsync();
            }
            catch (Exception ex) { Say("❌ Ошибка операции: " + ex.Message); }
        }

        string ListingUrl(ListingSummary item) =>
            string.IsNullOrWhiteSpace(item.Slug) ? "https://playerok.com" : "https://playerok.com/products/" + item.Slug;

        void OpenListing(ListingSummary item)
        {
            try { Process.Start(new ProcessStartInfo(ListingUrl(item)) { UseShellExecute = true }); }
            catch (Exception ex) { Say("Не удалось открыть PlayerOK: " + ex.Message); }
        }

        async Task CopyLinkAsync(ListingSummary item)
        {
            try
            {
                if (Clipboard is null) { Say("Буфер обмена недоступен."); return; }
                await Clipboard.SetTextAsync(ListingUrl(item));
                Say("✅ Ссылка на объявление скопирована.");
            }
            catch (Exception ex) { Say("Не удалось скопировать ссылку: " + ex.Message); }
        }

        Button IconButton(string glyph, string tip, Action click)
        {
            var btn = new Button { Content = glyph, Width = 38, HorizontalContentAlignment = HorizontalAlignment.Center };
            ToolTip.SetTip(btn, tip);
            btn.Click += (_, _) => click();
            return btn;
        }

        Button ActionButton(ListingSummary x, bool stretch)
        {
            var (_, _, group) = ListingStatus(x.StatusName);
            var canAct = x.StatusName is not ("PENDING_APPROVAL" or "PENDING_MODERATION" or "BLOCKED" or "SOLD");
            var isDraft = x.StatusName is "DRAFT" or "EXPIRED" or "DECLINED";
            var act = new Button
            {
                Content = isDraft ? "Опубликовать" : group == 2 ? "На проверке" : "🚀 Поднять",
                HorizontalContentAlignment = HorizontalAlignment.Center, IsEnabled = canAct,
            };
            if (stretch) act.HorizontalAlignment = HorizontalAlignment.Stretch;
            act.Click += async (_, _) =>
            {
                act.IsEnabled = false;
                try { await RaiseAsync(x); }
                finally { act.IsEnabled = canAct; }
            };
            return act;
        }

        Control StatusChip(string text, string color, double fontSize = 11) => new Border
        {
            Background = new SolidColorBrush(Color.Parse(color)), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 3),
            Child = new TextBlock { Text = text, FontSize = fontSize, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White },
        };

        Control BuildCard(ListingSummary x, decimal revenue)
        {
            var (stText, stColor, _) = ListingStatus(x.StatusName);
            var seed = (x.GameName ?? x.Name).Aggregate(17u, (a, c) => unchecked(a * 31 + c));
            var pal = CardPalette[(int)(seed % (uint)CardPalette.Length)];
            var letter = (x.GameName ?? x.Name).Trim();
            letter = letter.Length > 0 ? letter[..1].ToUpperInvariant() : "?";

            var tileBrush = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.Parse(pal.A), 0), new GradientStop(Color.Parse(pal.B), 1) },
            };
            var chip = StatusChip(stText, stColor);
            chip.Margin = new Thickness(8);
            chip.HorizontalAlignment = HorizontalAlignment.Left;
            chip.VerticalAlignment = VerticalAlignment.Top;
            var tile = new Grid { Height = 78 };
            tile.Children.Add(new Border { Background = tileBrush });
            tile.Children.Add(new TextBlock
            {
                Text = letter, FontSize = 46, FontWeight = FontWeight.Bold, Opacity = .35, Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            });
            tile.Children.Add(chip);

            var conv = x.ViewsCounter > 0 ? x.DealsCounter * 100.0 / x.ViewsCounter : (double?)null;
            var stats = new WrapPanel();
            void Stat(string t, string tip)
            {
                var tb = new TextBlock { Text = t, FontSize = 12, Opacity = .8, Margin = new Thickness(0, 0, 12, 0) };
                ToolTip.SetTip(tb, tip);
                stats.Children.Add(tb);
            }
            Stat($"👁 {x.ViewsCounter:N0}", "Просмотры");
            Stat($"🛒 {x.DealsCounter:N0}", "Покупки");
            if (conv is { } cv) Stat($"📈 {cv:0.#}%", "Конверсия просмотров в покупки");

            var buttons = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(0, 6, 0, 0) };
            var act = ActionButton(x, true);
            var open = IconButton("↗", "Открыть объявление на PlayerOK", () => OpenListing(x));
            var copy = IconButton("📋", "Скопировать ссылку", () => _ = CopyLinkAsync(x));
            open.Margin = new Thickness(6, 0, 0, 0);
            copy.Margin = new Thickness(6, 0, 0, 0);
            buttons.Children.Add(act);
            Grid.SetColumn(open, 1); buttons.Children.Add(open);
            Grid.SetColumn(copy, 2); buttons.Children.Add(copy);

            var name = new TextBlock
            {
                Text = x.Name, FontWeight = FontWeight.SemiBold, FontSize = 14, TextWrapping = TextWrapping.Wrap,
                MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis, MinHeight = 38,
            };
            ToolTip.SetTip(name, x.Name);
            var body = new StackPanel
            {
                Spacing = 3, Margin = new Thickness(12),
                Children =
                {
                    name,
                    new TextBlock { Text = string.Join(" · ", new[] { x.GameName, x.CategoryName }.Where(s => !string.IsNullOrWhiteSpace(s))), FontSize = 11, Opacity = .65, TextTrimming = TextTrimming.CharacterEllipsis },
                    new TextBlock { Text = $"{x.Price:0.##} ₽", FontSize = 19, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 4, 0, 0) },
                    stats,
                }
            };
            if (revenue > 0)
                body.Children.Add(new TextBlock { Text = $"💰 Выручка: {AnMoney(revenue)}", FontSize = 12, Foreground = AnGreen });
            body.Children.Add(buttons);

            var normalBorder = AnCardBorder;
            var card = new Border
            {
                Width = cardWidth, Margin = new Thickness(0, 0, 12, 12), ClipToBounds = true,
                Background = AnCardBg, BorderBrush = normalBorder, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Child = new StackPanel { Children = { tile, body } },
            };
            card.PointerEntered += (_, _) => card.BorderBrush = new SolidColorBrush(Color.Parse("#804D83FF"));
            card.PointerExited += (_, _) => card.BorderBrush = normalBorder;
            cardBorders.Add(card);
            return card;
        }

        // Компактная строка списка: статус · название · цена · просмотры · покупки · конверсия · действия.
        Control BuildRow(ListingSummary x, decimal revenue)
        {
            var (stText, stColor, _) = ListingStatus(x.StatusName);
            var name = new TextBlock { Text = x.Name, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            ToolTip.SetTip(name, x.Name);
            var sub = new TextBlock
            {
                Text = string.Join(" · ", new[] { x.GameName, x.CategoryName }.Where(s => !string.IsNullOrWhiteSpace(s))),
                FontSize = 11, Opacity = .6, TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var conv = x.ViewsCounter > 0 ? x.DealsCounter * 100.0 / x.ViewsCounter : (double?)null;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("104,*,86,66,66,66,Auto") };
            void Put(Control c, int col, HorizontalAlignment align = HorizontalAlignment.Right)
            {
                c.VerticalAlignment = VerticalAlignment.Center;
                c.HorizontalAlignment = align;
                Grid.SetColumn(c, col);
                row.Children.Add(c);
            }
            Put(StatusChip(stText, stColor, 10), 0, HorizontalAlignment.Left);
            Put(new StackPanel { Spacing = 1, Children = { name, sub } }, 1, HorizontalAlignment.Stretch);
            var priceBox = new StackPanel { Spacing = 1 };
            priceBox.Children.Add(new TextBlock { Text = $"{x.Price:0.##} ₽", FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Right });
            if (revenue > 0)
                priceBox.Children.Add(new TextBlock { Text = AnMoney(revenue), FontSize = 10, Foreground = AnGreen, HorizontalAlignment = HorizontalAlignment.Right });
            Put(priceBox, 2);
            Put(new TextBlock { Text = $"{x.ViewsCounter:N0}", FontSize = 12 }, 3);
            Put(new TextBlock { Text = $"{x.DealsCounter:N0}", FontSize = 12 }, 4);
            Put(new TextBlock { Text = conv is { } c2 ? $"{c2:0.#}%" : "—", FontSize = 12, Opacity = conv is > 0 ? 1 : .55, Foreground = conv is > 0 ? AnAmber : null }, 5);
            var acts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            acts.Children.Add(ActionButton(x, false));
            acts.Children.Add(IconButton("↗", "Открыть объявление на PlayerOK", () => OpenListing(x)));
            acts.Children.Add(IconButton("📋", "Скопировать ссылку", () => _ = CopyLinkAsync(x)));
            Put(acts, 6);

            var box = new Border
            {
                Background = AnCardBg, BorderBrush = AnCardBorder, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(11), Padding = new Thickness(12, 8), Child = row,
            };
            box.PointerEntered += (_, _) => box.BorderBrush = new SolidColorBrush(Color.Parse("#804D83FF"));
            box.PointerExited += (_, _) => box.BorderBrush = AnCardBorder;
            return box;
        }

        Control ListHeader()
        {
            var h = new Grid { ColumnDefinitions = new ColumnDefinitions("104,*,86,66,66,66,Auto"), Margin = new Thickness(12, 0, 12, 0) };
            void Col(string t, int col, HorizontalAlignment al)
            {
                var tb = new TextBlock { Text = t, FontSize = 11, Opacity = .55, HorizontalAlignment = al };
                Grid.SetColumn(tb, col);
                h.Children.Add(tb);
            }
            Col("Статус", 0, HorizontalAlignment.Left);
            Col("Объявление", 1, HorizontalAlignment.Left);
            Col("Цена", 2, HorizontalAlignment.Right);
            Col("Просм.", 3, HorizontalAlignment.Right);
            Col("Покупки", 4, HorizontalAlignment.Right);
            Col("Конв.", 5, HorizontalAlignment.Right);
            // Последняя колонка — действия: резервируем ширину под три кнопки.
            var spacer = new Border { Width = 182, Height = 1 };
            Grid.SetColumn(spacer, 6);
            h.Children.Add(spacer);
            return h;
        }

        void ApplyFilters()
        {
            clearSearch.IsVisible = !string.IsNullOrEmpty(search.Text);
            var query = search.Text?.Trim() ?? "";
            var game = gameFilter.SelectedItem?.ToString();
            var byGame = _myListings.Where(x =>
                (string.IsNullOrWhiteSpace(game) || game == "Все игры" || string.Equals(x.GameName, game, StringComparison.OrdinalIgnoreCase)) &&
                (query.Length == 0 || x.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || (x.GameName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)))
                .ToList();
            for (var i = 0; i < tabItems.Count; i++)
            {
                var n = i == 0 ? byGame.Count : byGame.Count(x => ListingStatus(x.StatusName).Group == i);
                tabItems[i].Text.Text = $"{tabLabels[i]} · {n}";
                tabItems[i].Box.Background = new SolidColorBrush(Color.Parse(i == tabGroup ? "#4D83FF" : "#2A303C"));
            }

            var revenue = RevenueByListing();
            decimal Rev(ListingSummary l) => revenue.TryGetValue(l.Id, out var r) ? r : 0m;
            double Conv(ListingSummary l) => l.ViewsCounter > 0 ? l.DealsCounter / (double)l.ViewsCounter : 0;
            var filtered = byGame.Where(x => tabGroup == 0 || ListingStatus(x.StatusName).Group == tabGroup);
            var shown = (sort.SelectedIndex switch
            {
                1 => filtered.OrderByDescending(x => x.Price),
                2 => filtered.OrderBy(x => x.Price),
                3 => filtered.OrderByDescending(x => x.ViewsCounter),
                4 => filtered.OrderByDescending(x => x.DealsCounter),
                5 => filtered.OrderByDescending(Conv),
                6 => filtered.OrderByDescending(Rev),
                7 => filtered.OrderByDescending(x => x.CreatedAt ?? DateTimeOffset.MinValue),
                _ => filtered.OrderBy(x => x.GameName).ThenBy(x => x.Name),
            }).ToList();

            cards.IsVisible = viewMode == 0;
            listPanel.IsVisible = viewMode == 1;
            cards.Children.Clear();
            listPanel.Children.Clear();
            cardBorders.Clear();

            if (shown.Count == 0)
            {
                var empty = new Border
                {
                    Margin = new Thickness(0, 6, 0, 0), Padding = new Thickness(20), CornerRadius = new CornerRadius(14),
                    Background = AnCardBg, BorderBrush = AnCardBorder, BorderThickness = new Thickness(1),
                    Child = new StackPanel
                    {
                        Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center,
                        Children =
                        {
                            new TextBlock { Text = _myListings.Count == 0 ? "🛍️" : "🔎", FontSize = 34, HorizontalAlignment = HorizontalAlignment.Center },
                            new TextBlock
                            {
                                Text = _myListings.Count == 0 ? "Объявлений пока нет" : "Ничего не найдено",
                                FontSize = 16, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center,
                            },
                            new TextBlock
                            {
                                Text = _myListings.Count == 0
                                    ? "Нажмите «Обновить», чтобы загрузить объявления, или «Создать товар»."
                                    : "Измените поиск, игру или вкладку статуса.",
                                Opacity = .65, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
                            },
                        }
                    }
                };
                (viewMode == 0 ? (Panel)cards : listPanel).Children.Add(empty);
                return;
            }

            if (viewMode == 0)
                foreach (var x in shown) cards.Children.Add(BuildCard(x, Rev(x)));
            else
            {
                listPanel.Children.Add(ListHeader());
                foreach (var x in shown) listPanel.Children.Add(BuildRow(x, Rev(x)));
            }
        }

        async Task RefreshDataAsync()
        {
            if (_myProductsRefreshBusy) return;
            _myProductsRefreshBusy = true;
            refresh.IsEnabled = false;
            loading.IsVisible = true;
            Say("Загружаю объявления и историю продаж PlayerOK…");
            try
            {
                var listingTask = _host.GetListingsForUiAsync(CancellationToken.None);
                var salesTask = _host.GetSalesForUiAsync(CancellationToken.None);
                await Task.WhenAll(listingTask, salesTask);
                _myListings = await listingTask;
                MergeSalesForUi(await salesTask);
                _listingStats.Capture(_myListings, DateTimeOffset.Now);
                var games = _myListings.Select(x => x.GameName).Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).Cast<string>().ToList();
                var prevGame = gameFilter.SelectedItem?.ToString();
                gameFilter.ItemsSource = new[] { "Все игры" }.Concat(games).ToList();
                var keep = prevGame is null ? -1 : games.FindIndex(g => string.Equals(g, prevGame, StringComparison.OrdinalIgnoreCase));
                gameFilter.SelectedIndex = keep >= 0 ? keep + 1 : 0;
                try { UpdateMetrics(); }
                catch (Exception ex)
                {
                    purchaseValue.Text = revenueValue.Text = viewsValue.Text = activeValue.Text = "—";
                    viewsHint.Text = "Не удалось рассчитать статистику: " + ex.Message;
                }
                ApplyFilters();
                updated.Text = "Обновлено в " + DateTime.Now.ToString("HH:mm");
                Say($"✅ Обновлено: {_myListings.Count} объявлений · {_mySales.Count} сделок в истории.");
            }
            catch (Exception ex)
            {
                Say("❌ Не удалось загрузить товары: " + ex.Message);
            }
            finally
            {
                _myProductsRefreshBusy = false;
                refresh.IsEnabled = true;
                loading.IsVisible = false;
            }
        }

        _refreshMyProducts = RefreshDataAsync;
        search.TextChanged += (_, _) => ApplyFilters();
        clearSearch.Click += (_, _) => { search.Text = ""; search.Focus(); };
        gameFilter.SelectionChanged += (_, _) => ApplyFilters();
        sort.SelectionChanged += (_, _) =>
        {
            if (sort.SelectedIndex < 0) return;
            _gui.MyProductsSort = sort.SelectedIndex;
            try { _gui.Save(); } catch (Exception logEx) { global::PlayerokBridge.Program.LogCrash("MainWindow.MyProducts.cs:574", logEx); }
            ApplyFilters();
        };
        period.SelectionChanged += (_, _) =>
        {
            try { UpdateMetrics(); }
            catch (Exception ex)
            {
                Say("⚠️ Не удалось пересчитать статистику: " + ex.Message);
                purchaseValue.Text = revenueValue.Text = viewsValue.Text = activeValue.Text = "—";
            }
        };
        refresh.Click += async (_, _) => await RefreshDataAsync();
        create.Click += async (_, _) =>
        {
            var draft = await ShowCreateListingWizardAsync();
            if (draft is null) return;
            Say($"✅ Черновик «{draft.Name}» создан. Загружаю тарифы публикации…");
            await RefreshDataAsync();
            try
            {
                var options = await _host.GetPriorityStatusesForUiAsync(draft.Id, draft.Price, CancellationToken.None);
                if (options.Count == 0)
                {
                    Say("Черновик создан и сохранён в PlayerOK, но варианты публикации сейчас недоступны.");
                    return;
                }
                var chosen = await ChoosePriorityStatusAsync(options, draft, publishDraft: true);
                if (chosen is null)
                {
                    Say("Черновик сохранён. Публикацию можно выполнить позже из списка товаров.");
                    return;
                }
                Say(chosen.Price == 0 ? "Публикую объявление…" : $"Подтверждено списание {chosen.Price} ₽. Публикую…");
                await _host.SetListingPriorityForUiAsync(draft.Id, chosen.Id, publishDraft: true, CancellationToken.None);
                Say("✅ Объявление опубликовано.");
                await RefreshDataAsync();
                await RefreshBalanceAsync();
            }
            catch (Exception ex) { Say("Черновик создан, но публикация не удалась: " + ex.Message); }
        };

        // ---------- компоновка: шапка закреплена, прокручивается только список ----------
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), Margin = new Thickness(14, 12, 14, 8) };
        header.Children.Add(new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new TextBlock { Text = "Мои товары", FontSize = 23, FontWeight = FontWeight.Bold },
                new TextBlock { Text = "Продажи, просмотры, публикация и продвижение объявлений", FontSize = 12, Opacity = .65 },
            }
        });
        updated.VerticalAlignment = VerticalAlignment.Center;
        updated.Margin = new Thickness(0, 0, 12, 0);
        Grid.SetColumn(updated, 1); header.Children.Add(updated);
        refresh.Margin = new Thickness(0, 0, 8, 0);
        Grid.SetColumn(refresh, 2); header.Children.Add(refresh);
        Grid.SetColumn(create, 3); header.Children.Add(create);

        var kpiHead = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(14, 0, 14, 6) };
        kpiHead.Children.Add(viewsHint);
        viewsHint.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(period, 1);
        period.Margin = new Thickness(10, 0, 0, 0);
        kpiHead.Children.Add(period);
        kpiGrid.Margin = new Thickness(14, 0, 14, 8);

        var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), Margin = new Thickness(14, 0, 14, 8) };
        gameFilter.Margin = new Thickness(8, 0, 0, 0);
        sort.Margin = new Thickness(8, 0, 0, 0);
        viewBar.Margin = new Thickness(8, 0, 0, 0);
        toolbar.Children.Add(search);
        Grid.SetColumn(gameFilter, 1); toolbar.Children.Add(gameFilter);
        Grid.SetColumn(sort, 2); toolbar.Children.Add(sort);
        Grid.SetColumn(viewBar, 3); toolbar.Children.Add(viewBar);

        var top = new StackPanel { Children = { header, loading, statusBox, kpiGrid, kpiHead, toolbar, tabsPanel } };

        var listHost = new StackPanel { Children = { cards, listPanel } };
        var scroll = new ScrollViewer
        {
            Content = listHost, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
        // Адаптивная сетка: карточки делят ширину поровну, без рваного правого края.
        scroll.SizeChanged += (_, e) =>
        {
            const double minCard = 215, gap = 12, pad = 28 + 14;
            var avail = Math.Max(minCard, e.NewSize.Width - pad);
            var cols = Math.Max(1, (int)Math.Floor((avail + gap) / (minCard + gap)));
            cardWidth = Math.Floor((avail + gap) / cols - gap);
            foreach (var b in cardBorders) b.Width = cardWidth;
        };

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        Grid.SetRow(scroll, 1);
        root.Children.Add(top);
        root.Children.Add(scroll);

        ApplyFilters();
        return root;
    }
}
