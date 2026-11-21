using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using PlayerokBridge.Core;
using PlayerokBridge.Playerok;

namespace PlayerokBridge.Views;

/// <summary>Вкладка «Автоответы»: удобное добавление и редактирование ответов по товарам.</summary>
public sealed partial class MainWindow
{
    private static readonly (string Title, string Text)[] ReplyTemplates =
    {
        ("Нужны данные от покупателя",
         "Здравствуйте, {buyer}! Спасибо за покупку «{item}» 🙂 Напишите, пожалуйста, данные, необходимые для выдачи заказа. После получения подтвердите сделку и оставьте отзыв ⭐\n— {seller}"),
        ("Ссылка на инструкцию",
         "Здравствуйте, {buyer}! Спасибо за покупку 🙂 Ваша инструкция: ВСТАВЬТЕ_ССЫЛКУ\nЕсли ссылка не открывается, скопируйте её и вставьте в адресную строку браузера. После получения подтвердите сделку и оставьте отзыв ⭐\n— {seller}"),
        ("Скоро выдам заказ",
         "Здравствуйте, {buyer}! Спасибо за покупку «{item}» 🙂 Уже занимаюсь заказом — выдам в течение нескольких минут. После получения подтвердите сделку и оставьте отзыв ⭐\n— {seller}"),
        ("Короткое «спасибо»",
         "Спасибо за покупку, {buyer}! 🙌 Если всё устроило, подтвердите сделку и оставьте отзыв ⭐\n— {seller}"),
    };

    private static readonly (string Token, string Tip)[] ReplyTokens =
    {
        ("{buyer}", "Ник покупателя"),
        ("{seller}", "Ваш ник"),
        ("{item}", "Название купленного товара"),
        ("{price}", "Цена сделки"),
        ("{deal_id}", "Номер сделки"),
    };

    /// <summary>Ключевые слова из названия лота: значимые слова через «+» — все они должны встретиться в названии.</summary>
    private static string SuggestKeyword(string? name)
    {
        var words = Regex.Split(name ?? "", @"[^\p{L}\p{N}]+")
            .Where(w => w.Length >= 2)
            .Select(w => w.ToLowerInvariant())
            .Distinct()
            .Take(4)
            .ToList();
        return words.Count > 0 ? string.Join("+", words) : (name ?? "").Trim().ToLowerInvariant();
    }

    private Control BuildProductsTab()
    {
        var items = _host.Products.Items;

        // ----- левая колонка: поиск и список -----
        var newBtn = new Button
        {
            Content = "➕  Новый ответ", HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center, FontWeight = FontWeight.SemiBold,
        };
        var search = new TextBox { Watermark = "🔍  Поиск по ответам" };
        var list = new ListBox();
        var countLabel = new TextBlock { Opacity = .6, FontSize = 11, Margin = new Thickness(2, 4, 0, 0) };

        // ----- правая колонка: форма -----
        var modeLabel = new TextBlock { FontSize = 18, FontWeight = FontWeight.Bold };
        var keyword = new TextBox { Watermark = "Например: brawl+гем, бравл+гем — или выберите товар кнопкой выше" };
        var keyHint = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Opacity = .75 };
        var text = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 170 };
        var preview = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = .9 };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = .9 };
        var testName = new TextBox { Watermark = "Название лота, например: Brawl Stars 30 гемов" };
        var testResult = new TextBlock { TextWrapping = TextWrapping.Wrap };

        var visible = new List<int>();   // индексы записей, показанных в списке (с учётом поиска)
        var editing = -1;                // редактируемая запись; -1 — новая
        var busy = false;                // защита от рекурсии при программной смене полей

        var save = new Button { FontWeight = FontWeight.SemiBold };
        var dup = new Button { Content = "⧉ Дублировать" };
        var del = new Button { Content = "🗑 Удалить" };

        void UpdateMode()
        {
            if (editing >= 0 && editing < items.Count)
            {
                modeLabel.Text = $"Редактирование: {items[editing].Title}";
                save.Content = "💾 Сохранить изменения";
            }
            else
            {
                modeLabel.Text = "Новый ответ";
                save.Content = "💾 Добавить ответ";
            }
            dup.IsEnabled = del.IsEnabled = editing >= 0;
        }

        void UpdateKeyHint()
        {
            var aliases = (keyword.Text ?? "").Split(',').Select(a => a.Trim()).Where(a => a.Length > 0).ToList();
            if (aliases.Count == 0) { keyHint.Text = "Впишите слова, по которым ответ подбирается к названию лота."; keyHint.Foreground = null; return; }
            var norm = aliases.Select(a => TextNorm.Normalize(a)).ToHashSet();
            for (var i = 0; i < items.Count; i++)
            {
                if (i == editing) continue;
                var other = items[i].Keyword.Split(',').Select(a => TextNorm.Normalize(a.Trim())).FirstOrDefault(a => a.Length > 0 && norm.Contains(a));
                if (other is not null)
                {
                    keyHint.Text = $"⚠ «{other}» уже используется в записи {i + 1} ({items[i].Title}). Сработает более точное совпадение.";
                    keyHint.Foreground = AnAmber;
                    return;
                }
            }
            keyHint.Text = $"Вариантов слов: {aliases.Count}. «+» — все части должны быть в названии лота.";
            keyHint.Foreground = null;
        }

        void UpdatePreview()
        {
            var t = text.Text ?? "";
            preview.Text = t.Trim().Length == 0
                ? "Предпросмотр появится, когда вы введёте текст."
                : ReplyRenderer.Render(t, new DealInfo("12345", "Иван", null, "Пример товара", 100, null), "продавец");
        }

        void Fill(string kw, string body)
        {
            busy = true;
            try { keyword.Text = kw; text.Text = body; }
            finally { busy = false; }
            UpdateKeyHint(); UpdatePreview();
        }

        void Refresh(int select)
        {
            var q = TextNorm.Normalize(search.Text).Trim();
            visible.Clear();
            for (var i = 0; i < items.Count; i++)
                if (q.Length == 0 || TextNorm.Normalize(items[i].Keyword + " " + items[i].Text).Contains(q))
                    visible.Add(i);
            busy = true;
            try
            {
                list.ItemsSource = visible.Select(i => $"{i + 1}. {items[i].Title}").ToList();
                var pos = select >= 0 ? visible.IndexOf(select) : -1;
                list.SelectedIndex = pos;
            }
            finally { busy = false; }
            countLabel.Text = q.Length == 0 ? $"Всего ответов: {items.Count}" : $"Найдено {visible.Count} из {items.Count}";
            UpdateMode();
        }

        void StartNew(string kw = "", string body = "")
        {
            editing = -1;
            Fill(kw, body);
            Refresh(-1);
            status.Text = "";
            keyword.Focus();
        }

        list.SelectionChanged += (_, _) =>
        {
            if (busy) return;
            var si = list.SelectedIndex;
            if (si < 0 || si >= visible.Count) return;
            editing = visible[si];
            Fill(items[editing].Keyword, items[editing].Text);
            UpdateMode();
            status.Text = "";
        };
        search.TextChanged += (_, _) => Refresh(editing);
        keyword.TextChanged += (_, _) => { if (!busy) UpdateKeyHint(); };
        text.TextChanged += (_, _) => { if (!busy) UpdatePreview(); };
        newBtn.Click += (_, _) => StartNew();

        // ----- действия -----
        void Save()
        {
            var kw = (keyword.Text ?? "").Trim();
            var body = text.Text ?? "";
            if (kw.Length == 0) { status.Text = "⚠ Укажите ключевые слова — по ним ответ подбирается к товару."; keyword.Focus(); return; }
            if (string.IsNullOrWhiteSpace(body)) { status.Text = "⚠ Введите текст ответа."; text.Focus(); return; }
            var isNew = editing < 0 || editing >= items.Count;
            if (isNew) { items.Add(new ProductReply(kw, body)); editing = items.Count - 1; }
            else items[editing] = new ProductReply(kw, body);
            try { _host.Products.Save(); status.Text = isNew ? "✅ Ответ добавлен." : "✅ Изменения сохранены."; }
            catch (Exception ex) { status.Text = "❌ Не удалось сохранить: " + ex.Message; }
            search.Text = "";
            Refresh(editing);
        }
        save.Click += (_, _) => Save();
        // Ctrl+Enter — быстрое сохранение из любого поля.
        void CtrlEnter(object? s, Avalonia.Input.KeyEventArgs e)
        {
            if (e.Key == Avalonia.Input.Key.Enter && e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control))
            {
                e.Handled = true;
                Save();
            }
        }
        text.AddHandler(InputElement.KeyDownEvent, CtrlEnter, RoutingStrategies.Tunnel);
        keyword.AddHandler(InputElement.KeyDownEvent, CtrlEnter, RoutingStrategies.Tunnel);

        dup.Click += (_, _) =>
        {
            if (editing < 0 || editing >= items.Count) return;
            var src = items[editing];
            StartNew(src.Keyword, src.Text);
            status.Text = "Копия создана — измените ключевые слова и нажмите «Добавить ответ».";
        };

        del.Click += async (_, _) =>
        {
            if (editing < 0 || editing >= items.Count) return;
            var title = items[editing].Title;
            if (!await ConfirmDialogAsync("Удалить ответ?", $"Запись «{title}» будет удалена безвозвратно.")) return;
            if (editing < 0 || editing >= items.Count) return;
            items.RemoveAt(editing);
            try { _host.Products.Save(); } catch (Exception logEx) { global::PlayerokBridge.Program.LogCrash("MainWindow.Replies.cs:218", logEx); }
            editing = -1;
            Fill("", "");
            Refresh(-1);
            status.Text = $"🗑 Удалено: {title}";
        };

        // ----- подстановки одним кликом -----
        var chips = new WrapPanel();
        foreach (var (token, tip) in ReplyTokens)
        {
            var t = token;
            var chip = new Button { Content = t, FontSize = 11, Padding = new Thickness(9, 3), Margin = new Thickness(0, 0, 6, 6) };
            ToolTip.SetTip(chip, tip + " — вставится в позицию курсора");
            chip.Click += (_, _) =>
            {
                var s = text.Text ?? "";
                var pos = Math.Clamp(text.CaretIndex, 0, s.Length);
                text.Text = s.Insert(pos, t);
                text.CaretIndex = pos + t.Length;
                text.Focus();
            };
            chips.Children.Add(chip);
        }

        // ----- шаблоны текста -----
        var templates = new ComboBox
        {
            PlaceholderText = "📝 Вставить шаблон текста…", MinWidth = 260,
            ItemsSource = ReplyTemplates.Select(x => x.Title).ToList(),
        };
        templates.SelectionChanged += (_, _) =>
        {
            if (busy || templates.SelectedIndex < 0) return;
            var tpl = ReplyTemplates[templates.SelectedIndex].Text;
            var cur = text.Text ?? "";
            text.Text = string.IsNullOrWhiteSpace(cur) ? tpl : cur.TrimEnd() + "\n\n" + tpl;
            busy = true;
            try { templates.SelectedIndex = -1; } finally { busy = false; }
            UpdatePreview();
        };

        // ----- выбор из моих товаров -----
        var pickBtn = new Button { Content = "📦 Из моих товаров" };
        var pickSearch = new TextBox { Watermark = "🔍  Найти товар", MinWidth = 360 };
        var pickList = new ListBox { MaxHeight = 300, MinWidth = 360 };
        var pickHint = new TextBlock { FontSize = 11, Opacity = .65, TextWrapping = TextWrapping.Wrap, MaxWidth = 360 };
        var shownListings = new List<ListingSummary>();
        var pickFlyout = new Flyout
        {
            Placement = PlacementMode.BottomEdgeAlignedLeft,
            Content = new StackPanel { Spacing = 8, Margin = new Thickness(4), Children = { pickSearch, pickList, pickHint } }
        };
        pickBtn.Flyout = pickFlyout;

        void FillPick()
        {
            var q = TextNorm.Normalize(pickSearch.Text).Trim();
            shownListings = _myListings
                .Where(l => q.Length == 0 || TextNorm.Normalize(l.Name).Contains(q))
                .Take(100).ToList();
            busy = true;
            try
            {
                pickList.ItemsSource = shownListings
                    .Select(l => (ProductMatcher.FindIndex(items, l.Name) is null ? "➕ " : "✅ ") + l.Name)
                    .ToList();
            }
            finally { busy = false; }
            pickHint.Text = _myListings.Count == 0
                ? "Объявления ещё не загружены — проверьте подключение к PlayerOK."
                : "➕ — ответа нет, ✅ — уже есть. Нажмите на товар, чтобы подставить ключевые слова.";
        }

        async Task EnsureListingsAsync()
        {
            if (_myListings.Count > 0 || _refreshMyProducts is null) return;
            try { await _refreshMyProducts(); } catch (Exception logEx) { global::PlayerokBridge.Program.LogCrash("MainWindow.Replies.cs:295", logEx); }
        }

        pickFlyout.Opening += async (_, _) =>
        {
            pickSearch.Text = "";
            pickHint.Text = "Загружаю объявления…";
            FillPick();
            if (_myListings.Count == 0)
            {
                await EnsureListingsAsync();
                FillPick();
            }
        };
        pickSearch.TextChanged += (_, _) => { if (!busy) FillPick(); };
        pickList.SelectionChanged += (_, _) =>
        {
            if (busy) return;
            var i = pickList.SelectedIndex;
            if (i < 0 || i >= shownListings.Count) return;
            var l = shownListings[i];
            busy = true;
            try { pickList.SelectedIndex = -1; } finally { busy = false; }
            pickFlyout.Hide();
            testName.Text = l.Name;
            var existing = ProductMatcher.FindIndex(items, l.Name);
            if (existing is { } ex)
            {
                editing = ex;
                Fill(items[ex].Keyword, items[ex].Text);
                Refresh(ex);
                status.Text = "ℹ Для этого товара ответ уже есть — открыл его для редактирования.";
                return;
            }
            StartNew(SuggestKeyword(l.Name), ReplyTemplates[0].Text);
            status.Text = $"Ключевые слова подставлены из «{l.Name}». Проверьте текст и нажмите «Добавить ответ».";
        };

        // ----- заготовки для всех товаров без ответа -----
        var bulkBtn = new Button { Content = "📥 Заготовки для товаров без ответа" };
        ToolTip.SetTip(bulkBtn, "Создаст ответы с шаблонным текстом для всех ваших объявлений, к которым ещё не подобран ответ");
        bulkBtn.Click += async (_, _) =>
        {
            bulkBtn.IsEnabled = false;
            try
            {
                await EnsureListingsAsync();
                if (_myListings.Count == 0) { status.Text = "⚠ Объявления не загружены — проверьте подключение к PlayerOK."; return; }
                var missing = new List<ListingSummary>();
                var plan = new List<ProductReply>(items);
                foreach (var l in _myListings)
                {
                    if (string.IsNullOrWhiteSpace(l.Name) || ProductMatcher.FindIndex(plan, l.Name) is not null) continue;
                    plan.Add(new ProductReply(SuggestKeyword(l.Name), ReplyTemplates[0].Text));
                    missing.Add(l);
                }
                if (missing.Count == 0) { status.Text = "✅ У всех ваших товаров уже есть автоответ."; return; }
                var names = string.Join("\n", missing.Take(8).Select(m => "• " + m.Name)) + (missing.Count > 8 ? $"\n…и ещё {missing.Count - 8}" : "");
                if (!await ConfirmDialogAsync("Создать заготовки?",
                        $"Будет добавлено ответов: {missing.Count}. Ключевые слова берутся из названий, текст — шаблонный; позже их можно изменить.\n\n{names}")) return;
                items.Clear();
                items.AddRange(plan);
                try { _host.Products.Save(); } catch (Exception ex) { status.Text = "❌ Не удалось сохранить: " + ex.Message; return; }
                editing = -1;
                Fill("", "");
                Refresh(-1);
                status.Text = $"✅ Добавлено заготовок: {missing.Count}. Выберите запись слева и поправьте текст.";
            }
            finally { bulkBtn.IsEnabled = true; }
        };

        // ----- проверка подбора -----
        var check = new Button { Content = "🔎 Проверить подбор" };
        check.Click += (_, _) =>
        {
            var matches = ProductMatcher.Match(items, testName.Text);
            if (matches.Count == 0) { testResult.Text = "❌ Ни одно ключевое слово не подошло — автоответ не отправится."; return; }
            var best = matches[0];
            var entry = items[best.Index];
            var rendered = ReplyRenderer.Render(entry.Text, new DealInfo("TEST", "Тест", null, testName.Text, 100, null), "продавец");
            testResult.Text = $"✅ Сработает: {entry.Title} (запись {best.Index + 1}), совпало «{best.Alias}»\n\n{rendered}";
        };

        var autoOn = new CheckBox
        {
            Content = "🤖 Автоответ по ключевым словам прямо в приложении (когда Telegram-бот не запущен)",
            IsChecked = _host.Settings.AppAutoReply
        };
        autoOn.IsCheckedChanged += (_, _) =>
        {
            _host.Settings.AppAutoReply = autoOn.IsChecked == true;
            try { _host.Settings.Save(); } catch (Exception logEx) { global::PlayerokBridge.Program.LogCrash("MainWindow.Replies.cs:386", logEx); }
        };

        Border Section(string title, Control body) => new()
        {
            Background = AnCardBg, BorderBrush = AnCardBorder, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14), Padding = new Thickness(14),
            Child = new StackPanel { Spacing = 8, Children = { AnText(title, 13, .8, true), body } }
        };

        var pickers = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { pickBtn, bulkBtn } };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { save, dup, del } };
        var previewBox = new Border
        {
            Background = new Avalonia.Media.SolidColorBrush(Color.FromArgb(0x14, 255, 255, 255)),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 10), Child = preview
        };

        var editor = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                modeLabel,
                pickers,
                Caption("Ключевые слова через запятую. Части через «+» должны встретиться все: brawl+гем."),
                keyword, keyHint,
                Caption("Текст ответа — кнопки ниже вставляют подстановки в позицию курсора (Ctrl+Enter — сохранить)"),
                chips, templates, text,
                Caption("Так увидит покупатель (на примере)"),
                previewBox,
                actions, status,
            }
        };

        var right = Page(
            autoOn,
            Section("ОТВЕТ ПО ТОВАРУ", editor),
            Section("ПРОВЕРКА ПОДБОРА", new StackPanel
            {
                Spacing = 8,
                Children = { Caption("Какой ответ получит лот с таким названием"), testName, check, testResult }
            }),
            BuildAfkCard(),
            BuildReviewCard());

        var left = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), Margin = new Thickness(0, 0, 8, 0) };
        newBtn.Margin = new Thickness(0, 0, 0, 8);
        search.Margin = new Thickness(0, 0, 0, 8);
        Grid.SetRow(search, 1); Grid.SetRow(list, 2); Grid.SetRow(countLabel, 3);
        left.Children.Add(newBtn); left.Children.Add(search); left.Children.Add(list); left.Children.Add(countLabel);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("260,*"), Margin = new Thickness(8) };
        var scroll = new ScrollViewer { Content = right };
        Grid.SetColumn(scroll, 1);
        grid.Children.Add(left);
        grid.Children.Add(scroll);

        Refresh(-1);
        Fill("", "");
        UpdateMode();
        return grid;
    }
}
