using System.Text;
using Avalonia;
using Avalonia.Styling;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using PlayerokBridge.Core;
using PlayerokBridge.Playerok;
using PlayerokBridge.Services;

namespace PlayerokBridge.Views;

/// <summary>Главное окно. Интерфейс собран в коде, чтобы не зависеть от XAML-разметки.</summary>
public sealed partial class MainWindow : Window
{
    private readonly AppHost _host = new();
    private readonly WindowsNotificationService _windowsNotifications = new();
    private readonly SoundService _sound = new(SoundSettings.Load());
    private readonly TrayIcon _trayIcon = new();
    private bool _exitRequested;
    private bool _trayReady;
    private bool _musicReady;          // музыка стартует только после показа окна
    private string? _lastMusicKey;
    private Action? _refreshMusicLabel;

    /// <summary>Останавливает фон на время перехода — только если включена настройка «Останавливать анимацию фона».</summary>
    private void PauseBackgroundForTransition() => _bg.PauseAnimation(_gui.Transition.PauseBackground);

    private readonly TextBlock _status = new() { FontSize = 16, FontWeight = FontWeight.SemiBold };
    private readonly Button _toggle = new() { Content = "▶ Запустить", MinWidth = 140, Padding = new Thickness(14, 8) };
    private readonly ComboBox _mode = new() { Width = 250, SelectedIndex = 0 };
    private bool _appModeRunning;
    private string? _selectedChatId;
    private List<ChatSummary> _chatCache = new();
    private bool _chatsVisible;                 // открыта ли сейчас вкладка «Чаты»
    private Action? _onChatsShown;              // что сделать при возврате на вкладку «Чаты»
    private readonly HashSet<string> _dealAlertChats = new(); // чаты с новой покупкой (красная метка)
    private readonly Border _chatsBadge = new()
    {
        Background = new SolidColorBrush(Color.Parse("#FF3B30")),
        CornerRadius = new CornerRadius(10), MinWidth = 20, Height = 20,
        Padding = new Thickness(6, 0), IsVisible = false
    };
    private readonly TextBlock _chatsBadgeText = new()
    {
        FontSize = 12, FontWeight = FontWeight.Bold, Foreground = Brushes.White,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
    };
    private readonly GuiSettings _gui = GuiSettings.Load();
    private readonly ListingStatsStore _listingStats = new();
    private readonly SalesHistoryStore _salesHistory = new();
    private readonly TextBlock _balanceAmount = new() { Text = "—", FontSize = 20, FontWeight = FontWeight.Bold };
    private readonly TextBlock _balanceMeta = new() { Text = "Ожидание подключения", FontSize = 11, Opacity = .7, TextWrapping = TextWrapping.Wrap };
    private readonly Button _balanceRefresh = new()
    {
        // Стрелка — отдельный TextBlock по центру кнопки (символ ↻ сам по себе сидит ниже и левее центра).
        Content = new TextBlock { Text = "↻", FontSize = 17, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -2, 0, 0) },
        Width = 34, Height = 34, Padding = new Thickness(0),
        HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private bool _balanceRefreshBusy;
    private Func<Task>? _refreshMyProducts;
    private bool _myProductsRefreshBusy;
    private IReadOnlyList<ListingSummary> _myListings = Array.Empty<ListingSummary>();
    private IReadOnlyList<Deal> _mySales = Array.Empty<Deal>();

    /// <summary>Объединяет историю PlayerOK и пойманные события покупок, сохраняя дату первой покупки.</summary>
    private void MergeSalesForUi(IEnumerable<Deal> incoming)
    {
        static int StatusRank(DealStatus status) => status switch
        {
            DealStatus.RolledBack => 6,
            DealStatus.Confirmed or DealStatus.ConfirmedAutomatically => 5,
            DealStatus.Sent => 4,
            DealStatus.Paid => 3,
            DealStatus.Pending => 2,
            _ => 0,
        };

        var dated = _salesHistory.Enrich(incoming);
        var merged = _mySales.GroupBy(d => d.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        foreach (var deal in dated)
        {
            if (!merged.TryGetValue(deal.Id, out var previous))
            {
                merged[deal.Id] = deal;
                continue;
            }

            var incomingIsAtLeastAsCurrent = StatusRank(deal.Status) >= StatusRank(previous.Status);
            merged[deal.Id] = deal with
            {
                Status = incomingIsAtLeastAsCurrent ? deal.Status : previous.Status,
                HasProblem = incomingIsAtLeastAsCurrent ? deal.HasProblem : previous.HasProblem,
                ItemName = deal.ItemName ?? previous.ItemName,
                Price = deal.Price == 0m && previous.Price != 0m ? previous.Price : deal.Price,
                BuyerName = deal.BuyerName ?? previous.BuyerName,
                SellerName = deal.SellerName ?? previous.SellerName,
                ChatId = deal.ChatId ?? previous.ChatId,
                BuyerComment = deal.BuyerComment ?? previous.BuyerComment,
                CreatedAt = deal.CreatedAt ?? previous.CreatedAt,
                HasReview = deal.HasReview || previous.HasReview,
                ReviewRating = deal.ReviewRating ?? previous.ReviewRating,
                ReviewText = deal.ReviewText ?? previous.ReviewText,
                ItemId = deal.ItemId ?? previous.ItemId,
            };
        }
        _mySales = merged.Values
            .OrderByDescending(d => d.CreatedAt ?? DateTimeOffset.MinValue)
            .ToList();
        _refreshAnalytics?.Invoke();
    }
    private readonly AnimatedBackground _bg = new();
    private readonly TransitionOverlay _overlay = new();
    private Action? _previewTransition;
    private readonly List<Border> _glass = new();
    private readonly List<Border> _navButtons = new();
    private readonly List<Style> _radiusStyles = new();
    private readonly LogBuffer _logBuffer = new();
    private LogWindow? _logWindow;

    // Трей: мигание значка, пока окно скрыто или не в фокусе, а появилась новая покупка или невыданный заказ.
    private WindowIcon? _trayNormalIcon;
    private WindowIcon? _trayAlertIcon;
    private readonly DispatcherTimer _trayBlinkTimer = new() { Interval = TimeSpan.FromMilliseconds(650) };
    private bool _trayBlinkOn;
    private int _trayAlerts;

    public MainWindow()
    {
        Title = "PlayerOK Bridge";
        InitializeTray();
        _chatsBadge.Child = _chatsBadgeText;
        Width = 1180; Height = 780; MinWidth = 900; MinHeight = 600;
        Foreground = new SolidColorBrush(Color.Parse("#F2F4F8"));
        // Окно без системной рамки: прозрачный фон + собственная рамка со скруглёнными углами.
        SystemDecorations = SystemDecorations.None;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Background = Brushes.Transparent;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var pages = new List<(string Title, Control Page)>
        {
            ("🏠   Обзор", BuildRunTab()),
            ("💬   Чаты", BuildChatsTab()),
            ("🤖   Автоответы", BuildProductsTab()),
            ("👤   Профиль", BuildStatsTab()),
            ("🔔   Уведомления", BuildNotifyTab()),
            ("🛍️   Мои товары", BuildMyProductsTab()),
            ("🛠️   Настройки", BuildSettingsTab()),
            ("🎨   GUI", BuildGuiTab()),
        };

        // Постоянная панель слева с балансом; рабочая страница занимает оставшуюся ширину.
        var host = new ContentControl { Margin = new Thickness(6) };
        // Слой перехода точно совпадает с областью host. Это важно: снимки делаются только
        // с host, поэтому предыдущий кадр overlay никогда не попадёт в следующий переход.
        _overlay.Margin = host.Margin;
        var pageArea = new Grid { ClipToBounds = true };
        pageArea.Children.Add(host);
        _overlay.IsVisible = false;
        pageArea.Children.Add(_overlay);
        var selected = -1;
        var hovered = -1;
        var navGrid = new Grid();
        byte Alpha(int i) => i == selected ? (byte)0x55 : i == hovered ? (byte)0x38 : (byte)0x1C;
        void Paint(int i) => _navButtons[i].Background = new SolidColorBrush(Color.FromArgb(Alpha(i), 255, 255, 255));

        DispatcherTimer? fadeTimer = null;

        void EndTransition()
        {
            fadeTimer?.Stop();
            host.Opacity = 1;
            host.IsHitTestVisible = true;
            _bg.PauseAnimation(false);
        }

        void DisposeLater(params IDisposable?[] items)
        {
            // Дать Avalonia завершить текущий render pass, затем освободить bitmap-снимки.
            _ = Task.Delay(400).ContinueWith(_ => Dispatcher.UIThread.Post(() =>
            {
                foreach (var it in items)
                {
                    try { it?.Dispose(); } catch { }
                }
            }));
        }

        // Предпросмотр запускается только при включённой анимации. Снимок demo-страницы
        // также делается отдельно от overlay, а текущая вкладка гарантированно возвращается.
        _previewTransition = () =>
        {
            _overlay.Cancel();
            EndTransition();

            var cfg = _gui.Transition;
            if (!cfg.Enabled) return;

            var current = host.Content;
            var demo = pages[selected == 0 ? 1 : 0].Page;
            RenderTargetBitmap? a = null;
            RenderTargetBitmap? b = null;
            try
            {
                pageArea.UpdateLayout();
                a = Snapshot(host);
                host.Content = demo;
                pageArea.UpdateLayout();
                b = Snapshot(host);
            }
            catch
            {
                // Предпросмотр необязателен; обычная навигация продолжает работать.
            }
            finally
            {
                host.Content = current;
                pageArea.UpdateLayout();
            }

            if (a is null || b is null)
            {
                DisposeLater(a, b);
                return;
            }

            var (c1, c2) = TransitionColors(cfg);
            var back = cfg.Clone();
            back.Direction = cfg.Direction switch
            {
                TransitionDirection.RightToLeft => TransitionDirection.LeftToRight,
                TransitionDirection.LeftToRight => TransitionDirection.RightToLeft,
                TransitionDirection.TopToBottom => TransitionDirection.BottomToTop,
                _ => TransitionDirection.TopToBottom,
            };

            host.Opacity = 0;
            host.IsHitTestVisible = false;
            PauseBackgroundForTransition();
            _overlay.Play(a, b, cfg.Clone(), c1, c2, ok =>
            {
                if (!ok)
                {
                    EndTransition();
                    DisposeLater(a, b);
                    return;
                }

                _overlay.Play(b, a, back, c1, c2, _ =>
                {
                    EndTransition();
                    DisposeLater(a, b);
                });
            });
        };

        void StopFade()
        {
            fadeTimer?.Stop();
            fadeTimer = null;
        }

        // Лёгкое затухание без снимков: страница меняется мгновенно и плавно проявляется.
        // Старая вкладка физически убрана из дерева, поэтому наложения быть не может.
        void FadeIn(double ms, Action done)
        {
            StopFade();
            host.Opacity = 0;
            var clock = Stopwatch.StartNew();
            double last = 0, acc = 0;
            var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
            fadeTimer = timer;
            timer.Tick += (_, _) =>
            {
                var now = clock.Elapsed.TotalMilliseconds;
                acc += Math.Min(now - last, 50);
                last = now;
                var t = Math.Clamp(acc / ms, 0, 1);
                host.Opacity = t * t * (3 - 2 * t);
                if (t < 1) return;
                timer.Stop();
                if (ReferenceEquals(fadeTimer, timer)) fadeTimer = null;
                host.Opacity = 1;
                done();
            };
            timer.Start();
        }

        void Select(int index)
        {
            var old = selected;
            if (index == old) return;
            selected = index;
            void Swap()
            {
                host.Content = pages[index].Page;
                Loc.Apply(pages[index].Page); // язык мог смениться, пока страница не была на экране
                _chatsVisible = pages[index].Title.Contains("Чаты");
                // Баланс уходит из правого верхнего угла в профиль и возвращается при выходе из вкладки.
                SetSideBalanceVisible(!pages[index].Title.Contains("Профиль"));
            }

            // Тяжёлая работа (пересборка списков, сетевые обновления) выполняется только ПОСЛЕ перехода:
            // раньше она блокировала UI-поток посреди анимации, и поверх новой вкладки
            // на пару секунд оставался кадр старой.
            var deferredDone = false;
            void Deferred()
            {
                if (deferredDone || selected != index) return;
                deferredDone = true;
                Dispatcher.UIThread.Post(() =>
                {
                    if (selected != index) return;
                    if (_chatsVisible) _onChatsShown?.Invoke();
                    if (pages[index].Title.Contains("Профиль")) _onProfileShown?.Invoke();
                    else if (pages[index].Title.Contains("Мои товары") && _refreshMyProducts is not null)
                        _ = _refreshMyProducts();
                }, DispatcherPriority.Background);
            }

            _overlay.Cancel();
            StopFade();
            host.Opacity = 1;
            host.IsHitTestVisible = true;
            _bg.PauseAnimation(false);

            var cfg = _gui.Transition.Clone();
            var animate = old >= 0 && cfg.Enabled;

            if (animate && cfg.Kind == TransitionKind.Fade)
            {
                Swap();
                PauseBackgroundForTransition();
                FadeIn(Math.Clamp(cfg.DurationMs, 80, 2000), () =>
                {
                    _bg.PauseAnimation(false);
                    Deferred();
                });
            }
            else
            {
                RenderTargetBitmap? from = null;
                RenderTargetBitmap? to = null;
                if (animate)
                {
                    try
                    {
                        pageArea.UpdateLayout();
                        from = Snapshot(host);
                    }
                    catch { animate = false; }
                }

                Swap();
                pageArea.UpdateLayout();

                if (animate)
                {
                    try { to = Snapshot(host); }
                    catch { animate = false; }
                }

                if (animate && from is not null && to is not null)
                {
                    var (c1, c2) = TransitionColors(cfg);
                    host.Opacity = 0;
                    host.IsHitTestVisible = false;
                    PauseBackgroundForTransition();
                    _overlay.Play(from, to, cfg, c1, c2, _ =>
                    {
                        host.Opacity = 1;
                        host.IsHitTestVisible = true;
                        _bg.PauseAnimation(false);
                        DisposeLater(from, to);
                        Deferred();
                    });
                }
                else
                {
                    from?.Dispose();
                    to?.Dispose();
                    // Первый экран проявляется после открытия окна; настройки длительности
                    // и включения берём из общей системы переходов.
                    host.Opacity = old < 0 && cfg.Enabled ? 0 : 1;
                    host.IsHitTestVisible = !(old < 0 && cfg.Enabled);
                    Deferred();
                }
            }
            if (old >= 0) Paint(old);
            Paint(index);
        }
        for (var i = 0; i < pages.Count; i++)
        {
            var index = i;
            navGrid.RowDefinitions.Add(new RowDefinition(58, GridUnitType.Pixel));
            var item = new Border
            {
                Margin = new Thickness(0, i == 0 ? 0 : 8, 0, 0),
                Padding = new Thickness(16, 6),
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse("scale(1)"),
                Background = new SolidColorBrush(Color.FromArgb(0x1C, 255, 255, 255)),
                Child = BuildNavContent(pages[i].Title),
                Transitions = new Avalonia.Animation.Transitions
                {
                    new Avalonia.Animation.BrushTransition { Property = Border.BackgroundProperty, Duration = TimeSpan.FromMilliseconds(180) },
                    new Avalonia.Animation.TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(150) },
                }
            };
            // Анимация наведения и нажатия: подсветка и небольшое изменение масштаба.
            item.PointerEntered += (_, _) =>
            {
                hovered = index; Paint(index);
                item.RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse("scale(1.03)");
            };
            item.PointerExited += (_, _) =>
            {
                hovered = -1; Paint(index);
                item.RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse("scale(1)");
            };
            item.PointerPressed += (_, _) => item.RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse("scale(0.95)");
            item.PointerReleased += (_, _) =>
            {
                item.RenderTransform = Avalonia.Media.Transformation.TransformOperations.Parse("scale(1.03)");
                Select(index);
            };
            Grid.SetRow(item, i);
            navGrid.Children.Add(item);
            _navButtons.Add(item);
        }

        var balanceRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(2, 8, 2, 12) };
        var balanceLabels = new StackPanel { Spacing = 3, Children =
        {
            new TextBlock { Text = "БАЛАНС PLAYEROK", FontSize = 10, FontWeight = FontWeight.Bold, Opacity = .65 },
            _balanceAmount,
            _balanceMeta,
        }};
        balanceRow.Children.Add(balanceLabels);
        Grid.SetColumn(_balanceRefresh, 1);
        balanceRow.Children.Add(_balanceRefresh);
        _balanceRefresh.Click += async (_, _) => await RefreshBalanceAsync();
        var balanceCard = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#A61D2430")),
            BorderBrush = new SolidColorBrush(Color.Parse("#334A5568")),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(13),
            Padding = new Thickness(12), Child = balanceRow,
        };
        balanceCard.Margin = new Thickness(0, 0, 0, 12);
        _sideBalanceCard = balanceCard;
        balanceCard.Transitions = new Avalonia.Animation.Transitions { new Avalonia.Animation.DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(220) } };
        var sideTop = new StackPanel { Spacing = 0, Children =
        {
            balanceCard,
            new TextBlock { Text = "PLAYEROK BRIDGE", FontSize = 16, FontWeight = FontWeight.Bold, Margin = new Thickness(2, 0, 2, 0) },
            new TextBlock { Text = "Панель продавца", FontSize = 11, Opacity = .6, Margin = new Thickness(2, 0, 2, 12) },
        }};
        var sideLayout = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        sideLayout.Children.Add(sideTop);
        var navScroll = new ScrollViewer { Content = navGrid, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        Grid.SetRow(navScroll, 1);
        sideLayout.Children.Add(navScroll);

        var navGlass = new Border { Width = 252, Padding = new Thickness(12), Child = sideLayout };
        var pageGlass = new Border { Margin = new Thickness(0, 0, 12, 0), Child = pageArea, ClipToBounds = true };
        _glass.Add(navGlass); _glass.Add(pageGlass);

        var shell = new Grid { Margin = new Thickness(12, 0, 12, 12), ColumnDefinitions = new ColumnDefinitions("*,252") };
        shell.Children.Add(pageGlass);
        Grid.SetColumn(navGlass, 1);
        shell.Children.Add(navGlass);

        var root = new Grid { RowDefinitions = new RowDefinitions("40,*") };
        Grid.SetRowSpan(_bg, 2);
        root.Children.Add(_bg);
        var titleBar = BuildTitleBar();
        root.Children.Add(titleBar);
        Grid.SetRow(shell, 1);
        root.Children.Add(shell);

        var frameGrid = new Grid();
        frameGrid.Children.Add(root);
        AddResizeGrips(frameGrid);
        _frame = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#0E1015")),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(WindowRadius),
            ClipToBounds = true,
            Child = frameGrid,
        };
        Content = _frame;
        ApplyGui();
        Select(0);

        _host.Log += line => Dispatcher.UIThread.Post(() => AppendLog(line));
        _host.StaleDeals += list => Dispatcher.UIThread.Post(() => OnStaleDeals(list));
        // Если токен уже сохранён — подключаемся сразу: чаты обновляются сами, покупки не пропускаются.
        Opened += async (_, _) =>
        {
            try { _windowsNotifications.Initialize(TryGetPlatformHandle()?.Handle ?? IntPtr.Zero); } catch (Exception logEx) { global::PlayerokBridge.Program.LogCrash("MainWindow.cs:463", logEx); }
            _musicReady = true;
            UpdateMusicTheme(force: true);
            if (_gui.Transition.Enabled)
                FadeIn(Math.Clamp(_gui.Transition.DurationMs, 120, 2500), () => host.IsHitTestVisible = true);
            await AutoStartAsync();
            await RefreshBalanceAsync();
        };
        Closing += (_, e) =>
        {
            if (!_exitRequested)
            {
                // Обычное закрытие прячет окно в трей, не останавливая фоновые задачи.
                e.Cancel = true;
                // При уходе в трей останавливаем только фоновую музыку.
                // Звуки событий и уведомлений продолжают работать.
                _bg.Suspend(true);
                _sound.SuspendMusic(true);
                Hide();
                return;
            }

            _trayIcon.IsVisible = false;
            try { _sound.StopMusicForExit(); } catch { }
            try { _gui.Save(); } catch { }
            try { _windowsNotifications.Dispose(); } catch { }
            try { _sound.Settings.Save(); } catch { }
            _host.Dispose();
        };
        UpdateStatus();

        // Язык выбран при первом входе: переводим уже собранный интерфейс до первого показа.
        foreach (var pg in pages) Loc.Apply(pg.Page);
        Loc.Apply(this);
        Loc.Changed += UpdateTrayLanguage;
        Closed += (_, _) => Loc.Changed -= UpdateTrayLanguage;
    }

    // ---------- Окно без рамки ----------

    private const double WindowRadius = 18;
    private Border? _frame;
    private readonly TextBlock _maxGlyph = new() { Text = "▢", FontSize = 14 };

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != WindowStateProperty || _frame is null) return;
        // Свёрнутое окно не должно тратить CPU на фоновую анимацию.
        _bg.Suspend(WindowState == WindowState.Minimized);
        _sound.SuspendMusic(WindowState == WindowState.Minimized);
        if (WindowState == WindowState.Minimized && _trayReady)
            Dispatcher.UIThread.Post(Hide);
        var max = WindowState == WindowState.Maximized;
        // В развёрнутом виде углы и рамка не нужны.
        _frame.CornerRadius = new CornerRadius(max ? 0 : WindowRadius);
        _frame.BorderThickness = new Thickness(max ? 0 : 1);
        _maxGlyph.Text = max ? "❐" : "▢";
    }

    private NativeMenuItem? _trayOpenItem, _trayLogItem, _trayExitItem;

    private void UpdateTrayLanguage()
    {
        if (_trayOpenItem is not null) _trayOpenItem.Header = Loc.T("Открыть PlayerOK Bridge", "Open PlayerOK Bridge");
        if (_trayLogItem is not null) _trayLogItem.Header = Loc.T("Журнал событий", "Event log");
        if (_trayExitItem is not null) _trayExitItem.Header = Loc.T("Выход", "Exit");
    }

    private void InitializeTray()
    {
        var menu = new NativeMenu();
        var openItem = new NativeMenuItem { Header = Loc.T("Открыть PlayerOK Bridge", "Open PlayerOK Bridge") };
        _trayOpenItem = openItem;
        openItem.Click += (_, _) => RestoreFromTray();
        var logItem = new NativeMenuItem { Header = Loc.T("Журнал событий", "Event log") };
        _trayLogItem = logItem;
        logItem.Click += (_, _) => ShowLogWindow();
        var exitItem = new NativeMenuItem { Header = Loc.T("Выход", "Exit") };
        _trayExitItem = exitItem;
        exitItem.Click += (_, _) => { _exitRequested = true; Close(); };
        menu.Items.Add(openItem);
        menu.Items.Add(logItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(exitItem);

        _trayIcon.ToolTipText = "PlayerOK Bridge";
        _trayIcon.Menu = menu;
        _trayIcon.Clicked += (_, _) => RestoreFromTray();

        var iconPath = Path.Combine(AppContext.BaseDirectory, "playerokbridge.ico");
        if (File.Exists(iconPath))
        {
            using (var stream = File.OpenRead(iconPath))
                _trayNormalIcon = new WindowIcon(stream);
            _trayIcon.Icon = _trayNormalIcon;
            _trayAlertIcon = MakeAlertIcon(iconPath);
        }
        _trayBlinkTimer.Tick += (_, _) =>
        {
            _trayBlinkOn = !_trayBlinkOn;
            if (_trayAlertIcon is not null && _trayNormalIcon is not null)
                _trayIcon.Icon = _trayBlinkOn ? _trayAlertIcon : _trayNormalIcon;
            else
                _trayIcon.IsVisible = !_trayBlinkOn; // запасной вариант: значок просто мигает появлением
        };
        Activated += (_, _) => StopTrayAlert();
        TrayIcon.SetIcons(Application.Current!, new TrayIcons { _trayIcon });
        _trayIcon.IsVisible = true;
        _trayReady = true;
    }

    /// <summary>Значок с красной точкой для мигания в трее.</summary>
    private static WindowIcon? MakeAlertIcon(string iconPath)
    {
        try
        {
            using var stream = File.OpenRead(iconPath);
            using var bmp = new Bitmap(stream);
            var size = new Size(64, 64);
            var visual = new Grid
            {
                Width = 64, Height = 64,
                Children =
                {
                    new Image { Source = bmp, Stretch = Stretch.Uniform },
                    new Border
                    {
                        Width = 28, Height = 28, CornerRadius = new CornerRadius(14),
                        Background = new SolidColorBrush(Color.Parse("#FF3B30")),
                        BorderBrush = Brushes.White, BorderThickness = new Thickness(3),
                        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                    },
                },
            };
            visual.Measure(size);
            visual.Arrange(new Rect(size));
            var target = new RenderTargetBitmap(new PixelSize(64, 64), new Vector(96, 96));
            target.Render(visual);
            return new WindowIcon(target);
        }
        catch { return null; }
    }

    /// <summary>Мигает значком в трее, пока вы не откроете окно. Если окно уже перед глазами, ничего не делает.</summary>
    private void TrayAlert(string text)
    {
        if (!_trayReady) return;
        if (IsVisible && IsActive && WindowState != WindowState.Minimized) return;
        _trayAlerts++;
        var shortText = text.Length > 70 ? text[..67] + "…" : text;
        _trayIcon.ToolTipText = $"PlayerOK Bridge — новых событий: {_trayAlerts}\n{shortText}";
        if (!_trayBlinkTimer.IsEnabled) { _trayBlinkOn = false; _trayBlinkTimer.Start(); }
    }

    private void StopTrayAlert()
    {
        if (!_trayReady || (_trayAlerts == 0 && !_trayBlinkTimer.IsEnabled)) return;
        _trayAlerts = 0;
        _trayBlinkTimer.Stop();
        _trayBlinkOn = false;
        _trayIcon.IsVisible = true;
        if (_trayNormalIcon is not null) _trayIcon.Icon = _trayNormalIcon;
        _trayIcon.ToolTipText = "PlayerOK Bridge";
    }

    private void RestoreFromTray()
    {
        StopTrayAlert();
        Show();
        WindowState = WindowState.Normal;
        _bg.Suspend(false);
        _sound.SuspendMusic(false);
        Activate();
    }

    private Control BuildTitleBar()
    {
        Button CapBtn(string glyph, Action click, bool danger = false)
        {
            var b = new Button
            {
                Content = glyph, Width = 40, Height = 28, Padding = new Thickness(0), FontSize = 14,
                Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
                CornerRadius = new CornerRadius(8), Margin = new Thickness(2, 0),
            };
            b.Click += (_, _) => click();
            if (danger)
            {
                b.PointerEntered += (_, _) => b.Background = new SolidColorBrush(Color.Parse("#E5484D"));
                b.PointerExited += (_, _) => b.Background = Brushes.Transparent;
            }
            return b;
        }

        void ToggleMax() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        var max = CapBtn("▢", ToggleMax);
        max.Content = _maxGlyph;
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
            Children = { CapBtn("—", () => WindowState = WindowState.Minimized), max, CapBtn("✕", Close, danger: true) },
        };
        var drag = new Border { Background = Brushes.Transparent };
        drag.Child = new TextBlock
        {
            Text = "PlayerOK Bridge", FontSize = 12, Opacity = .55, FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 0, 0), IsHitTestVisible = false,
        };
        drag.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(drag).Properties.IsLeftButtonPressed) return;
            if (e.ClickCount == 2) { _snapped = false; ToggleMax(); return; }
            OnTitlePressed(e, drag);
        };
        drag.PointerMoved += (_, e) => OnTitleMoved(e, drag);
        drag.PointerReleased += (_, _) => OnTitleReleased();
        var bar = new Grid();
        bar.Children.Add(drag);
        bar.Children.Add(buttons);
        return bar;
    }

    private void AddResizeGrips(Grid host)
    {
        void Grip(WindowEdge edge, Avalonia.Input.StandardCursorType cursor, HorizontalAlignment h, VerticalAlignment v, double w, double hgt)
        {
            var g = new Border
            {
                Background = Brushes.Transparent, Width = w, Height = hgt, HorizontalAlignment = h, VerticalAlignment = v,
                Cursor = new Avalonia.Input.Cursor(cursor),
            };
            g.PointerPressed += (_, e) =>
            {
                if (WindowState == WindowState.Normal && e.GetCurrentPoint(g).Properties.IsLeftButtonPressed)
                {
                    _snapped = false;
                    BeginResizeDrag(edge, e);
                }
            };
            host.Children.Add(g);
        }
        const double t = 5, c = 12;
        var nan = double.NaN;
        Grip(WindowEdge.North, Avalonia.Input.StandardCursorType.SizeNorthSouth, HorizontalAlignment.Stretch, VerticalAlignment.Top, nan, t);
        Grip(WindowEdge.South, Avalonia.Input.StandardCursorType.SizeNorthSouth, HorizontalAlignment.Stretch, VerticalAlignment.Bottom, nan, t);
        Grip(WindowEdge.West, Avalonia.Input.StandardCursorType.SizeWestEast, HorizontalAlignment.Left, VerticalAlignment.Stretch, t, nan);
        Grip(WindowEdge.East, Avalonia.Input.StandardCursorType.SizeWestEast, HorizontalAlignment.Right, VerticalAlignment.Stretch, t, nan);
        Grip(WindowEdge.NorthWest, Avalonia.Input.StandardCursorType.TopLeftCorner, HorizontalAlignment.Left, VerticalAlignment.Top, c, c);
        Grip(WindowEdge.NorthEast, Avalonia.Input.StandardCursorType.TopRightCorner, HorizontalAlignment.Right, VerticalAlignment.Top, c, c);
        Grip(WindowEdge.SouthWest, Avalonia.Input.StandardCursorType.BottomLeftCorner, HorizontalAlignment.Left, VerticalAlignment.Bottom, c, c);
        Grip(WindowEdge.SouthEast, Avalonia.Input.StandardCursorType.BottomRightCorner, HorizontalAlignment.Right, VerticalAlignment.Bottom, c, c);
    }

    // ---------- Общие помощники ----------

    private static TextBlock Caption(string text) =>
        new() { Text = text, Opacity = 0.7, Margin = new Thickness(0, 10, 0, 2), TextWrapping = TextWrapping.Wrap };

    private static StackPanel Page(params Control[] children)
    {
        var panel = new StackPanel { Margin = new Thickness(14), Spacing = 6 };
        foreach (var c in children) panel.Children.Add(c);
        return panel;
    }

    private void AppendLog(string line) => _logBuffer.Add(line);

    private void ShowLogWindow()
    {
        if (_logWindow is not null) { _logWindow.Activate(); return; }
        var w = new LogWindow(_logBuffer);
        w.Closed += (_, _) => _logWindow = null;
        _logWindow = w;
        w.Show();
    }

    private async Task AutoStartAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_host.Settings.PlayerokToken) || _host.IsRunning || _appModeRunning) return;
            await _host.ConnectAppOnlyAsync(CancellationToken.None, explicitStart: true);
            _appModeRunning = true;
            AppendLog("Приложение подключено автоматически. Автоответ по ключевым словам " +
                      (_host.Settings.AppAutoReply ? "включён." : "выключен (вкладка «Ответы по товарам»)."));
        }
        catch (Exception ex) { AppendLog("⚠️ Автоподключение не удалось: " + ex.Message); }
        finally { UpdateStatus(); }
    }

    private void UpdateStatus()
    {
        var running = _host.IsRunning || _appModeRunning;
        _status.Text = running ? (_host.IsRunning ? "🟢 Приложение и Telegram-бот работают" : "🟢 Приложение подключено к PlayerOK") : "⚪ Не запущено";
        _toggle.Content = running ? "■ Остановить" : "▶ Запустить";
    }

    private async Task RefreshBalanceAsync()
    {
        if (_balanceRefreshBusy) return;
        _balanceRefreshBusy = true;
        _balanceRefresh.IsEnabled = false;
        _balanceMeta.Text = "Обновление…";
        try
        {
            var balance = await _host.GetBalanceForUiAsync(CancellationToken.None);
            // В некоторых ответах PlayerOK поле frozen приходит нулевым, хотя часть
            // общего баланса недоступна. В таком случае вычисляем заморозку как разницу.
            var frozen = balance.Frozen > 0
                ? balance.Frozen
                : Math.Max(0m, balance.Value - balance.Available);
            _balanceAmount.Text = $"{balance.Value:0.##} ₽";
            _balanceMeta.Text = $"Доступно {balance.Available:0.##} ₽ · Заморожено {frozen:0.##} ₽";
            _profBalance.Text = _balanceAmount.Text;
            _profBalanceMeta.Text = _balanceMeta.Text;
        }
        catch (Exception ex)
        {
            _balanceAmount.Text = "—";
            _balanceMeta.Text = string.IsNullOrWhiteSpace(_host.Settings.PlayerokToken)
                ? "Добавьте токен в настройках"
                : "Нет подключения · нажмите ↻";
            _profBalance.Text = _balanceAmount.Text;
            _profBalanceMeta.Text = _balanceMeta.Text;
            AppendLog("⚠️ Не удалось обновить баланс: " + ex.Message);
        }
        finally
        {
            _balanceRefreshBusy = false;
            _balanceRefresh.IsEnabled = true;
        }
    }

    // ---------- Работа ----------

    private Control BuildRunTab()
    {
        _mode.ItemsSource = new[] { "Только приложение", "Приложение + Telegram-бот" };
        _mode.SelectedIndex = 0;
        _toggle.Click += async (_, _) =>
        {
            _toggle.IsEnabled = false;
            try
            {
                if (_host.IsRunning)
                {
                    await _host.StopAsync();
                    _host.DisconnectApp();
                    _appModeRunning = false;
                }
                else if (_appModeRunning && _mode.SelectedIndex == 1)
                {
                    // Приложение уже подключено — просто добавляем Telegram-бота (он берёт автоответ на себя).
                    _host.ReloadSettings();
                    _host.Start();
                }
                else if (_appModeRunning)
                {
                    _appModeRunning = false;
                    _host.DisconnectApp();
                    AppendLog("Приложение отключено от PlayerOK. Автообновление чатов и автоответ остановлены.");
                }
                else
                {
                    _host.ReloadSettings();
                    if (_mode.SelectedIndex == 0)
                    {
                        await _host.ConnectAppOnlyAsync(CancellationToken.None, explicitStart: true);
                        _appModeRunning = true;
                        AppendLog("Режим «Только приложение»: Telegram-бот не запускается, автоответ выполняет приложение.");
                    }
                    else
                    {
                        _host.Start();
                        // Окно чатов тоже подключаем, чтобы список обновлялся сам (автоответ при этом делает бот).
                        try { await _host.ConnectAppOnlyAsync(CancellationToken.None, explicitStart: true); }
                        catch (Exception ex) { AppendLog("⚠️ Окно чатов не подключилось: " + ex.Message); }
                    }
                }
            }
            catch (Exception ex) { AppendLog("❌ " + ex.Message); }
            finally { _toggle.IsEnabled = true; UpdateStatus(); }
        };

        var hero = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#A620232B")),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(22),
            Child = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = "PlayerOK", FontSize = 30, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(Color.Parse("#4D83FF")) },
                    new TextBlock { Text = "Управляйте товарами, переписками и автоматизацией в одном окне.", FontSize = 15, Opacity = .85, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = "Режим запуска", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0,10,0,0) },
                    _mode,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Children = { _toggle, _status } }
                }
            }
        };
        _status.VerticalAlignment = VerticalAlignment.Center;
        var openLog = new Button { Content = "↗ В отдельном окне", Padding = new Thickness(12, 6) };
        openLog.Click += (_, _) => ShowLogWindow();
        var logHeader = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        logHeader.Children.Add(new TextBlock { Text = "Журнал событий", FontSize = 17, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(openLog, 1); logHeader.Children.Add(openLog);
        var logCard = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#A6191C23")),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 14, 0, 0),
            Child = new StackPanel
            {
                Spacing = 8,
                Children = { logHeader, LogView.Build(_logBuffer, 300) }
            }
        };
        return new ScrollViewer { Content = new StackPanel { Margin = new Thickness(18), Children = { hero, logCard } } };
    }

    // ---------- Чаты PlayerOK ----------

    /// <summary>Состояние открытого (и закэшированного) чата: загруженные сообщения и курсор для подгрузки вверх.</summary>
    private sealed class ChatState
    {
        public List<ChatMessage> Messages { get; } = new();
        public string? Cursor;
        public bool HasOlder;
        public bool Loaded;
        public bool Refreshing;
        public bool RefreshAgain;
        public string? SummaryKey;
        public HashSet<string> UsedCursors { get; } = new(StringComparer.Ordinal);
    }

    private static string SummaryKeyOf(ChatSummary c) => $"{c.LastAt?.ToUnixTimeMilliseconds()}|{c.LastText}";

    /// <summary>Покупка по сообщениям чата (данные сделки приходят вместе с сообщением {{ITEM_PAID}} и т.п.).</summary>
    private static Deal? DealFromMessages(IReadOnlyList<ChatMessage> messages, string chatId)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var m = messages[i];
            if (string.IsNullOrEmpty(m.DealId) || string.IsNullOrWhiteSpace(m.PurchasedItemName)) continue;
            return new Deal(m.DealId, DealStatus.Unknown, false, m.PurchasedItemName, m.ItemPrice ?? 0m,
                m.DealUserName, m.SellerName, chatId, null, m.CreatedAt, false);
        }
        return null;
    }

    /// <summary>
    /// Восстанавливает даты продаж из служебных сообщений оплаты в уже загруженной истории чата.
    /// Старым сообщениям без timestamp дату не придумываем: такие покупки фиксируются при получении
    /// нового события ITEM_PAID, а не попадают задним числом в сегодняшний день.
    /// </summary>
    private void CapturePurchaseDatesFromMessages(IEnumerable<ChatMessage> messages, string chatId)
    {
        var recovered = new List<Deal>();
        foreach (var message in messages)
        {
            var marker = message.Text?.Trim();
            if (marker is not ("{{ITEM_PAID}}" or "{{NEW_DEAL}}" or "{{DEAL_PAID}}")
                || string.IsNullOrWhiteSpace(message.DealId)
                || message.CreatedAt is not { } purchaseAt)
                continue;

            var deal = new Deal(message.DealId, DealStatus.Unknown, false, message.PurchasedItemName,
                message.ItemPrice ?? 0m, message.DealUserName, message.SellerName, chatId, null,
                purchaseAt, false);
            recovered.Add(_salesHistory.CapturePurchase(deal, purchaseAt));
        }

        if (recovered.Count > 0)
            MergeSalesForUi(recovered);
    }

    /// <summary>Красная метка на пункте меню «Чаты» и счётчик в заголовке окна.</summary>
    private void UpdateChatsBadge()
    {
        var attention = new HashSet<string>();
        foreach (var c in _chatCache)
            if (c.Unread > 0 && !(c.Id == _selectedChatId && _chatsVisible)) attention.Add(c.Id);
        foreach (var id in _dealAlertChats)
            if (!(id == _selectedChatId && _chatsVisible)) attention.Add(id);

        var count = attention.Count;
        _chatsBadge.IsVisible = count > 0;
        _chatsBadgeText.Text = count > 99 ? "99+" : count.ToString();
        Title = count > 0 ? $"({count}) PlayerOK Bridge" : "PlayerOK Bridge";
    }

    private Control BuildNavContent(string title)
    {
        var label = new TextBlock
        {
            Text = title, FontSize = 15, Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis
        };
        if (!title.Contains("Чаты")) return label;

        label.Margin = new Thickness(0, 0, 34, 0);
        _chatsBadge.HorizontalAlignment = HorizontalAlignment.Right;
        _chatsBadge.VerticalAlignment = VerticalAlignment.Center;
        var grid = new Grid();
        grid.Children.Add(label);
        grid.Children.Add(_chatsBadge);
        return grid;
    }

    private Control BuildChatsTab()
    {
        var chats = new ListBox { MinWidth = 280, Background = new SolidColorBrush(Color.Parse("#A6191C23")) };
        var chatRows = new System.Collections.ObjectModel.ObservableCollection<Control>();
        chats.ItemsSource = chatRows;
        var visibleChats = new List<ChatSummary>();
        var activeChatFilter = "Все";
        var rowIds = new List<string>();
        var rowSigs = new List<string>();
        var suppressSelection = false;

        var messagePanel = new StackPanel { Spacing = 10 };
        var messages = new ScrollViewer
        {
            Content = messagePanel,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        };
        // Центральный индикатор загрузки: три точки по очереди подпрыгивают по оси Y.
        var loadingTransforms = new List<TranslateTransform>();
        var loadingDots = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 7,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        for (var i = 0; i < 3; i++)
        {
            var transform = new TranslateTransform();
            loadingTransforms.Add(transform);
            loadingDots.Children.Add(new Border
            {
                Width = 10, Height = 10, CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Color.Parse("#83B5FF")),
                RenderTransform = transform, VerticalAlignment = VerticalAlignment.Center
            });
        }
        var loadingOverlay = new Border
        {
            IsVisible = false, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
            Background = new SolidColorBrush(Color.FromArgb(0xB8, 17, 21, 29)),
            Child = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Spacing = 12, Children =
                {
                    loadingDots,
                    new TextBlock { Text = "Загружаю сообщения…", FontSize = 13, Opacity = .82, HorizontalAlignment = HorizontalAlignment.Center }
                }
            }
        };
        var chatMessagesHost = new Grid { ClipToBounds = true };
        chatMessagesHost.Children.Add(messages);
        chatMessagesHost.Children.Add(loadingOverlay);
        var loadingFrame = 0;
        var loadingTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(75) };
        loadingTimer.Tick += (_, _) =>
        {
            var bounce = new[] { 0d, -2.5d, -5.5d, -2.5d, 0d, 0d, 0d, 0d, 0d };
            for (var i = 0; i < loadingTransforms.Count; i++)
            {
                var phase = ((loadingFrame - i * 3) % bounce.Length + bounce.Length) % bounce.Length;
                loadingTransforms[i].Y = bounce[phase];
            }
            loadingFrame++;
        };
        void SetChatLoading(bool active)
        {
            loadingOverlay.IsVisible = active;
            if (active)
            {
                loadingFrame = 0;
                loadingTimer.Start();
            }
            else
            {
                loadingTimer.Stop();
                foreach (var transform in loadingTransforms) transform.Y = 0;
            }
        }
        var compose = new TextBox { Watermark = "Напишите сообщение...", AcceptsReturn = true, MinHeight = 62, TextWrapping = TextWrapping.Wrap };
        var attach = new Button { Content = "📎", Width = 46, Height = 44, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 8, 0), FontSize = 18, HorizontalContentAlignment = HorizontalAlignment.Center };
        ToolTip.SetTip(attach, "Прикрепить фото (повторное нажатие — убрать)");
        var chatTitle = new TextBlock { Text = "Выберите диалог", FontSize = 18, FontWeight = FontWeight.SemiBold };
        var info = new TextBlock { Text = "Чаты обновляются автоматически.", Opacity = .65, TextWrapping = TextWrapping.Wrap };
        var refresh = new Button { Content = "↻ Обновить чаты" };
        var chatSearch = new TextBox { Watermark = "Поиск по имени или сообщению…", MinHeight = 36, Margin = new Thickness(0, 0, 0, 6) };
        var unreadOnly = new CheckBox { Content = "Только непрочитанные", Margin = new Thickness(2, 0, 0, 6), VerticalAlignment = VerticalAlignment.Center };
        var filterButtons = new Dictionary<string, Button>();
        var filtersPanel = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        foreach (var filterName in new[] { "Все", "Оплачено", "Товар выдан", "Возврат", "Спор / проблема" })
        {
            var button = new Button { Content = filterName, Padding = new Thickness(8, 5), Margin = new Thickness(0, 0, 5, 5), FontSize = 11 };
            filterButtons[filterName] = button;
            filtersPanel.Children.Add(button);
        }

        // Состояние открытого чата и кэш уже открытых (повторное открытие показывается мгновенно).
        var messageCache = new Dictionary<string, ChatState>();
        ChatState? curState = null;
        CancellationTokenSource? chatCts = null;
        var chatLoadVersion = 0;
        var loadingOlder = false;
        var loadingChats = false;
        var pendingEchoes = new List<string>(); // тексты, отправленные отсюда и уже нарисованные локально
        Dictionary<string, Deal> salesByChat = new();
        // Запасные источники статуса сделки: по id сделки, по нику покупателя и найденные в сообщениях чата.
        Dictionary<string, Deal> salesById = new();
        Dictionary<string, Deal> salesByBuyer = new(StringComparer.OrdinalIgnoreCase);
        var dealByChat = new Dictionary<string, Deal>();
        var enrichedChats = new HashSet<string>();
        var enriching = false;
        var lastEnrichFailed = 0;
        var salesLoading = false;

        // Главная покупка чата: сначала сделка без статуса (Unknown), затем оплаченная.
        // Сделки с любым другим статусом — побочные, пока в чате есть Unknown.
        Deal? SelectPrimaryDeal(IEnumerable<Deal> deals) => deals
            .OrderBy(d => d.Status switch
            {
                DealStatus.Unknown => 0,
                DealStatus.Paid => 1,
                _ => 2
            })
            .ThenByDescending(d => d.CreatedAt ?? DateTimeOffset.MinValue)
            .FirstOrDefault();

        Deal? GetPrimaryDealForChat(string chatId) => SelectPrimaryDeal(
            salesById.Values.Where(d => string.Equals(d.ChatId, chatId, StringComparison.Ordinal))
                .Concat(salesByChat.TryGetValue(chatId, out var indexed) ? new[] { indexed } : Array.Empty<Deal>())
                .Concat(dealByChat.TryGetValue(chatId, out var enriched) ? new[] { enriched } : Array.Empty<Deal>()));

        // Прокрутка: «прилипание» к низу и сохранение позиции при подгрузке старых сообщений.
        var stickBottom = true;
        double? anchorFromBottom = null;
        var anchorSetAt = DateTime.MinValue;
        var lastExtentH = 0d;

        // Ответы по товарам: список ключевых слов, по клику текст вставляется в поле сообщения.
        var productList = new ListBox { MaxHeight = 340, Width = 400, Background = new SolidColorBrush(Color.Parse("#E014161C")) };
        var productFlyout = new Flyout { Content = productList, Placement = PlacementMode.BottomEdgeAlignedLeft };
        var productButton = new Button
        {
            Content = "🎯 Ответы по товарам ▾",
            Flyout = productFlyout,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(12, 6)
        };
        Deal? currentDeal = null;
        var dealActionBusy = false;
        var confirmSentButton = new Button
        {
            Content = "✅ Подтвердить выдачу",
            Margin = new Thickness(8, 0, 0, 8), Padding = new Thickness(12, 6), IsEnabled = false
        };
        var refundButton = new Button
        {
            Content = "↩ Сделать возврат",
            Margin = new Thickness(8, 0, 0, 8), Padding = new Thickness(12, 6), IsEnabled = false
        };
        var productOrder = new List<int>();
        var refreshingProducts = false;

        void InsertIntoCompose(string answer)
        {
            compose.Text = answer;
            compose.CaretIndex = answer.Length;
            compose.Focus();
        }

        void RefreshProductList()
        {
            try
            {
                refreshingProducts = true;
                var items = _host.Products.Items;
                var matched = new HashSet<int>(ProductMatcher.Match(items, currentDeal?.ItemName).Select(m => m.Index));
                // Подходящие под купленный товар идут первыми и отмечены ✓.
                productOrder = Enumerable.Range(0, items.Count).OrderBy(i => matched.Contains(i) ? 0 : 1).ThenBy(i => i).ToList();
                productList.ItemsSource = productOrder.Select(i =>
                {
                    var aliases = items[i].Keyword.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    var label = string.Join(" · ", aliases);
                    if (label.Length > 70) label = label[..67] + "…";
                    return new TextBlock { Text = (matched.Contains(i) ? "✓ " : "") + label, TextTrimming = TextTrimming.CharacterEllipsis };
                }).ToList();
                productButton.IsEnabled = items.Count > 0;
            }
            catch (Exception ex) { AppendLog($"⚠️ Список ответов по товарам: {ex.Message}"); }
            finally { refreshingProducts = false; }
            UpdateDealButtons();
        }

        void UpdateDealButtons()
        {
            var d = currentDeal;
            var final = d is not null && d.Status is DealStatus.Confirmed or DealStatus.ConfirmedAutomatically or DealStatus.RolledBack;
            confirmSentButton.IsEnabled = !dealActionBusy && d is not null && !final && d.Status != DealStatus.Sent;
            refundButton.IsEnabled = !dealActionBusy && d is not null && !final;
        }

        async Task RunDealActionAsync(DealStatus target, string title, string question, string doneText)
        {
            var deal = currentDeal;
            if (deal is null || dealActionBusy) return;
            var partner = deal.BuyerName ?? _chatCache.FirstOrDefault(c => c.Id == _selectedChatId)?.PartnerName ?? "покупатель";
            var text = $"{question}\n\nПокупатель: {partner}\nТовар: {deal.ItemName ?? "Без названия"}\nСумма: {deal.Price:0.##} ₽";
            if (!await ConfirmDialogAsync(title, text)) return;
            dealActionBusy = true; UpdateDealButtons();
            info.Text = "Отправляю запрос в PlayerOK…";
            try
            {
                var updated = await Task.Run(() => _host.UpdateDealStatusFromUiAsync(deal.Id, target, CancellationToken.None));
                salesById = new Dictionary<string, Deal>(salesById) { [updated.Id] = updated };
                if (currentDeal?.Id == deal.Id) currentDeal = updated;
                if (!string.IsNullOrEmpty(updated.ChatId))
                {
                    salesByChat = new Dictionary<string, Deal>(salesByChat);
                    var primary = GetPrimaryDealForChat(updated.ChatId!);
                    if (primary is not null)
                    {
                        salesByChat[updated.ChatId!] = primary;
                        if (_selectedChatId == updated.ChatId) currentDeal = primary;
                    }
                }
                SyncChatList();
                info.Text = doneText;
            }
            catch (Exception ex) { info.Text = "Ошибка: " + ex.Message; }
            finally { dealActionBusy = false; RefreshProductList(); }
        }

        confirmSentButton.Click += async (_, _) => await RunDealActionAsync(
            DealStatus.Sent, "Подтвердить выдачу",
            "Подтвердить, что товар выдан покупателю? После этого покупатель сможет подтвердить сделку.",
            "✅ Выдача подтверждена.");
        refundButton.Click += async (_, _) => await RunDealActionAsync(
            DealStatus.RolledBack, "Сделать возврат",
            "Оформить возврат? Деньги вернутся покупателю, а сделка будет отменена. Это действие нельзя отменить.",
            "↩ Возврат оформлен.");
        RefreshProductList();
        // Список ключевых слов мог поменяться на вкладке «Ответы по товарам».
        compose.GotFocus += (_, _) => RefreshProductList();

        productList.SelectionChanged += (_, _) =>
        {
            if (refreshingProducts) return;
            var pos = productList.SelectedIndex;
            if (pos < 0 || pos >= productOrder.Count) return;
            var product = _host.Products.Items[productOrder[pos]];
            var deal = currentDeal;
            var partner = _chatCache.FirstOrDefault(c => c.Id == _selectedChatId)?.PartnerName;
            var dealInfo = deal is null
                ? new DealInfo(null, partner, null, null, null, _selectedChatId)
                : new DealInfo(deal.Id, deal.BuyerName ?? partner, deal.SellerName, deal.ItemName, deal.Price, deal.ChatId);
            InsertIntoCompose(ReplyRenderer.Render(product.Text, dealInfo, _host.UiUsername));
            info.Text = $"Вставлен ответ «{product.Title}». Проверьте текст и нажмите Enter.";
            productList.SelectedIndex = -1; // чтобы повторный клик по тому же пункту снова срабатывал
            productFlyout.Hide();
        };

        // ---------- Список чатов ----------

        List<Deal> GetDealsForChat(ChatSummary chat)
        {
            // Для фильтров собираем ВСЕ сделки чата, а не только главную.
            // По ID предпочитаем свежую запись из salesById, затем добавляем сделки из остальных источников.
            var byId = new Dictionary<string, Deal>(StringComparer.Ordinal);
            void AddDeal(Deal? deal)
            {
                if (deal is null || string.IsNullOrWhiteSpace(deal.Id)) return;
                byId[deal.Id] = salesById.TryGetValue(deal.Id, out var fresh) ? fresh : deal;
            }

            foreach (var deal in salesById.Values.Where(d => string.Equals(d.ChatId, chat.Id, StringComparison.Ordinal)))
                AddDeal(deal);
            AddDeal(chat.Deal);
            if (dealByChat.TryGetValue(chat.Id, out var enriched)) AddDeal(enriched);
            if (salesByChat.TryGetValue(chat.Id, out var indexed)) AddDeal(indexed);
            return byId.Values.ToList();
        }


        HashSet<string> ChatDealCategories(ChatSummary chat)
        {
            // Один чат может одновременно присутствовать в нескольких фильтрах,
            // если разные покупки в нём имеют разные статусы.
            var categories = new HashSet<string>(StringComparer.Ordinal);
            foreach (var deal in GetDealsForChat(chat))
            {
                if (deal.Status == DealStatus.RolledBack)
                {
                    categories.Add("Возврат");
                    continue;
                }
                if (deal.HasProblem)
                {
                    categories.Add("Спор / проблема");
                    continue;
                }
                switch (deal.Status)
                {
                    case DealStatus.Paid:
                        categories.Add("Оплачено");
                        break;
                    case DealStatus.Sent:
                        // В фильтре показываем только сделки, для которых продавец нажал «Товар выдан».
                        categories.Add("Товар выдан");
                        break;
                    // Pending не считается оплаченным.
                }
            }
            return categories;
        }

        void UpdateFilterButtons()
        {
            foreach (var pair in filterButtons)
            {
                var selected = pair.Key == activeChatFilter;
                pair.Value.Content = pair.Key == "Все"
                    ? pair.Key
                    : $"{pair.Key} ({_chatCache.Count(c => ChatDealCategories(c).Contains(pair.Key))})";
                pair.Value.Background = new SolidColorBrush(Color.Parse(selected ? "#FF3978D4" : "#302F3745"));
                pair.Value.Foreground = selected ? Brushes.White : new SolidColorBrush(Color.Parse("#FFD5D9E2"));
            }
        }

        Control BuildChatRow(ChatSummary c)
        {
            var isOpen = c.Id == _selectedChatId && _chatsVisible;
            var unread = c.Unread > 0 && !isOpen;
            var alert = _dealAlertChats.Contains(c.Id) && !isOpen;
            var raw = string.IsNullOrWhiteSpace(c.LastText) ? "📷 Вложение" : c.LastText!;
            var preview = SafeText(EventFormatter.ReadableSystemMessage(raw)).Replace("\n", " ");
            var hot = unread || alert;

            var texts = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = (alert ? "🛒 " : "") + SafeText(c.PartnerName),
                        FontWeight = hot ? FontWeight.Bold : FontWeight.Normal,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    },
                    new TextBlock
                    {
                        Text = alert ? "Новая покупка!" : preview, FontSize = 12, Opacity = hot ? .95 : .65,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    }
                }
            };
            var rowGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), MaxWidth = 280 };
            rowGrid.Children.Add(texts);
            if (hot)
            {
                var pill = new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#FF3B30")),
                    CornerRadius = new CornerRadius(10), MinWidth = 20, Height = 20,
                    Padding = new Thickness(6, 0), Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = c.Unread > 0 ? (c.Unread > 99 ? "99+" : c.Unread.ToString()) : "!",
                        FontSize = 12, FontWeight = FontWeight.Bold, Foreground = Brushes.White,
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                    }
                };
                Grid.SetColumn(pill, 1);
                rowGrid.Children.Add(pill);
            }
            return rowGrid;
        }

        string RowSig(ChatSummary c)
        {
            // Учитываем статусы всех покупок: изменение побочной сделки тоже должно обновлять строку/фильтры.
            var dealSignature = string.Join(",", GetDealsForChat(c)
                .OrderBy(d => d.Id, StringComparer.Ordinal)
                .Select(d => $"{d.Id}:{d.Status}:{d.HasProblem}"));

            return $"{c.Id}|{c.Unread}|{c.LastText}|{c.LastAt?.ToUnixTimeMilliseconds()}|{c.PartnerName}|{c.Deal?.Id}|{c.Deal?.Status}|{c.Deal?.HasProblem}|{dealSignature}|{_dealAlertChats.Contains(c.Id)}|{c.Id == _selectedChatId && _chatsVisible}";
        }

        // Обновляет строки списка. Если порядок не менялся — заменяются только изменившиеся строки (прокрутка не сбивается).
        void SyncChatList()
        {
            suppressSelection = true;
            try
            {
                var query = chatSearch.Text?.Trim();
                visibleChats = _chatCache
                    .Where(c => activeChatFilter == "Все" || ChatDealCategories(c).Contains(activeChatFilter))
                    .Where(c => unreadOnly.IsChecked != true || c.Unread > 0)
                    .Where(c => string.IsNullOrWhiteSpace(query)
                        || c.PartnerName.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || (c.LastText?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
                    .OrderByDescending(c => c.Unread > 0)
                    .ThenByDescending(c => c.LastAt ?? DateTimeOffset.MinValue)
                    .ToList();
                UpdateFilterButtons();
                var sameOrder = rowIds.Count == visibleChats.Count && chatRows.Count == rowIds.Count && rowSigs.Count == rowIds.Count
                                && rowIds.SequenceEqual(visibleChats.Select(c => c.Id));
                // Замена/очистка элементов при выбранной строке ломает выбор внутри ListBox («Index was out of range»),
                // поэтому перед любыми изменениями коллекции выделение снимаем и возвращаем после.
                var needRebuild = !sameOrder;
                if (sameOrder)
                {
                    var changed = new List<int>();
                    for (var i = 0; i < visibleChats.Count; i++)
                        if (rowSigs[i] != RowSig(visibleChats[i])) changed.Add(i);
                    if (changed.Count > 0)
                    {
                        try
                        {
                            if (chats.SelectedIndex != -1) chats.SelectedIndex = -1;
                            foreach (var i in changed)
                            {
                                rowSigs[i] = RowSig(visibleChats[i]);
                                chatRows[i] = BuildChatRow(visibleChats[i]);
                            }
                        }
                        catch (Exception ex) { Program.LogCrash("SyncChatList(replace)", ex); needRebuild = true; }
                    }
                }
                if (needRebuild)
                {
                    chats.SelectedIndex = -1;
                    rowIds.Clear(); rowSigs.Clear();
                    var fresh = new List<Control>();
                    foreach (var c in visibleChats)
                    {
                        fresh.Add(BuildChatRow(c));
                        rowIds.Add(c.Id);
                        rowSigs.Add(RowSig(c));
                    }
                    chatRows.Clear();
                    foreach (var r in fresh) chatRows.Add(r);
                }
                var sel = _selectedChatId is null ? -1 : visibleChats.FindIndex(c => c.Id == _selectedChatId);
                if (chats.SelectedIndex != sel) chats.SelectedIndex = sel;
                refresh.Content = $"↻ Обновить чаты ({visibleChats.Count}/{_chatCache.Count})";
            }
            finally { suppressSelection = false; }
            UpdateChatsBadge();
        }

        // Добавляет/обновляет чаты в списке. preferIncoming=true — данные свежего опроса, иначе берём более новые по времени.
        bool MergeChats(IEnumerable<ChatSummary> incoming, bool preferIncoming)
        {
            var changed = false;
            var index = new Dictionary<string, int>();
            for (var i = 0; i < _chatCache.Count; i++) index[_chatCache[i].Id] = i;
            foreach (var c in incoming)
            {
                if (index.TryGetValue(c.Id, out var i))
                {
                    var existing = _chatCache[i];
                    var newer = (c.LastAt ?? DateTimeOffset.MinValue) > (existing.LastAt ?? DateTimeOffset.MinValue);
                    if ((preferIncoming || newer) && existing != c) { _chatCache[i] = c; changed = true; }
                }
                else
                {
                    _chatCache.Add(c);
                    index[c.Id] = _chatCache.Count - 1;
                    changed = true;
                }
            }
            if (changed)
                _chatCache = _chatCache.OrderByDescending(x => x.LastAt ?? DateTimeOffset.MinValue).ToList();
            return changed;
        }

        // ---------- Сообщения открытого чата ----------

        void ScrollToBottom() =>
            messages.Offset = new Vector(0, Math.Max(0, messages.Extent.Height - messages.Viewport.Height));

        void UpdateInfo(ChatState st)
        {
            var count = $"Показано сообщений: {st.Messages.Count}" +
                        (st.HasOlder ? " · прокрутите вверх — подгрузятся предыдущие 24" : st.Messages.Count > 0 ? " · это вся история" : "");
            info.Text = currentDeal is null
                ? (st.Messages.Count == 0 ? "В этом диалоге пока нет сообщений." : count)
                : $"🛒 Покупка: {currentDeal.ItemName ?? "Без названия"} · {currentDeal.Price:0.##} ₽ · {count}";
        }

        bool ConsumeEcho(ChatMessage m)
        {
            var i = pendingEchoes.IndexOf((m.Text ?? "").Trim());
            if (i < 0) return false;
            pendingEchoes.RemoveAt(i);
            return true;
        }

        void AppendMessages(IEnumerable<ChatMessage> fresh)
        {
            foreach (var m in fresh)
            {
                // Своё сообщение, которое мы только что показали локально, второй раз не рисуем.
                if (!string.IsNullOrEmpty(_host.UiUserId) && m.AuthorId == _host.UiUserId && ConsumeEcho(m)) continue;
                AddMessageBubble(messagePanel, m);
            }
        }

        // Старые сообщения добавляются НАД уже показанными, нижние не удаляются и не перерисовываются.
        void PrependMessages(List<ChatMessage> older)
        {
            var savedDay = _lastDay;
            _lastDay = null;
            var tmp = new StackPanel();
            foreach (var m in older) AddMessageBubble(tmp, m);
            var lastDay = _lastDay;
            _lastDay = savedDay;

            var kids = tmp.Children.ToList();
            tmp.Children.Clear();
            // Если день на стыке совпал — убираем дублирующую плашку даты.
            if (lastDay is { } d && messagePanel.Children.FirstOrDefault() is TextBlock first && first.Text == d.ToString("d MMMM", Ru))
                messagePanel.Children.RemoveAt(0);
            messagePanel.Children.InsertRange(0, kids);
        }

        DispatcherTimer? messagesFadeTimer = null;
        void FadeMessagesIn()
        {
            messagesFadeTimer?.Stop();
            if (!_gui.Transition.Enabled) { messagePanel.Opacity = 1; return; }
            messagePanel.Opacity = 0;
            var clock = Stopwatch.StartNew();
            var duration = Math.Clamp(_gui.Transition.DurationMs, 100, 1800);
            var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
            messagesFadeTimer = timer;
            timer.Tick += (_, _) =>
            {
                var t = Math.Clamp(clock.Elapsed.TotalMilliseconds / duration, 0, 1);
                messagePanel.Opacity = t * t * (3 - 2 * t);
                if (t < 1) return;
                timer.Stop();
                if (ReferenceEquals(messagesFadeTimer, timer)) messagesFadeTimer = null;
                messagePanel.Opacity = 1;
            };
            timer.Start();
        }

        void BindOpenChat(ChatState st, string chatId)
        {
            CapturePurchaseDatesFromMessages(st.Messages, chatId);
            RenderMessages(messagePanel, st.Messages);
            FadeMessagesIn();
            stickBottom = true;
            anchorFromBottom = null;
            currentDeal = GetPrimaryDealForChat(chatId)
                          ?? (salesByChat.TryGetValue(chatId, out var sold) ? sold : null)
                          ?? (_chatCache.FirstOrDefault(c => c.Id == chatId)?.Deal)
                          ?? DealFromMessages(st.Messages, chatId);
            RefreshProductList();
            UpdateInfo(st);
            Dispatcher.UIThread.Post(() => ScrollToBottom(), DispatcherPriority.Background);
        }

        // Подтягивает самые свежие сообщения открытого чата и дорисовывает только новые.
        async Task RefreshNewestAsync(string chatId, ChatState st, int version, string summaryKey, CancellationToken ct)
        {
            if (st.Refreshing) { st.RefreshAgain = true; return; }
            st.Refreshing = true;
            try
            {
                do
                {
                    st.RefreshAgain = false;
                    ct.ThrowIfCancellationRequested();
                    var page = await _host.GetMessagesPageForUiAsync(chatId, null, ct);
                    ct.ThrowIfCancellationRequested();
                    CapturePurchaseDatesFromMessages(page.Messages, chatId);

                    // Ключ меняется только после успешного ответа: сетевой сбой не пропустит обновление.
                    st.SummaryKey = summaryKey;
                    var known = new HashSet<string>(st.Messages.Select(m => m.Id), StringComparer.Ordinal);
                    var newest = st.Messages.Count > 0
                        ? st.Messages.Max(m => m.CreatedAt ?? DateTimeOffset.MinValue)
                        : DateTimeOffset.MinValue;
                    var fresh = page.Messages
                        .Where(m => !known.Contains(m.Id)
                            && (m.CreatedAt is null || newest == DateTimeOffset.MinValue || m.CreatedAt >= newest))
                        .OrderBy(m => m.CreatedAt ?? DateTimeOffset.MinValue)
                        .ThenBy(m => m.Id, StringComparer.Ordinal)
                        .ToList();
                    if (fresh.Count == 0) continue;

                    foreach (var message in fresh)
                        if (st.Messages.All(existing => existing.Id != message.Id)) st.Messages.Add(message);
                    st.Messages.Sort((a, b) =>
                    {
                        var byTime = Nullable.Compare(a.CreatedAt, b.CreatedAt);
                        return byTime != 0 ? byTime : StringComparer.Ordinal.Compare(a.Id, b.Id);
                    });

                    var visibleNow = chatId == _selectedChatId && version == chatLoadVersion;
                    if (!visibleNow) continue;
                    AppendMessages(fresh);
                    var deal = GetPrimaryDealForChat(chatId) ?? DealFromMessages(st.Messages, chatId);
                    if (deal is not null && deal.Id != currentDeal?.Id) { currentDeal = deal; RefreshProductList(); }
                    UpdateInfo(st);
                    if (_chatsVisible && fresh.Any(m => m.AuthorId != _host.UiUserId))
                        _ = _host.MarkChatReadForUiAsync(chatId);
                } while (st.RefreshAgain && !ct.IsCancellationRequested);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (chatId == _selectedChatId) info.Text = "Не удалось обновить чат: " + ex.Message; }
            finally { st.Refreshing = false; }
        }

        async Task OpenChatAsync(ChatSummary chat)
        {
            SetChatLoading(false);
            chatCts?.Cancel();
            var cts = new CancellationTokenSource();
            chatCts = cts;
            var ct = cts.Token;
            var version = ++chatLoadVersion;

            _selectedChatId = chat.Id;
            _dealAlertChats.Remove(chat.Id);
            pendingEchoes.Clear();
            loadingOlder = false;
            chatTitle.Text = chat.PartnerName;

            // Локально снимаем «непрочитано» и отмечаем чат прочитанным на сайте (в фоне).
            var idx = _chatCache.FindIndex(c => c.Id == chat.Id);
            if (idx >= 0 && _chatCache[idx].Unread > 0) _chatCache[idx] = _chatCache[idx] with { Unread = 0 };
            // Нельзя перестраивать список прямо внутри SelectionChanged: у чата с непрочитанными после сброса счётчика
            // меняется порядок («непрочитанные сверху»), список очищается и заполняется заново, пока ListBox ещё обрабатывает
            // выбор — это и роняло приложение. Откладываем обновление до завершения обработки события.
            Dispatcher.UIThread.Post(() =>
            {
                try { SyncChatList(); }
                catch (Exception ex) { Program.LogCrash("SyncChatList(open)", ex); }
            }, DispatcherPriority.Background);
            _ = _host.MarkChatReadForUiAsync(chat.Id);

            // Уже открывавшийся чат показываем сразу из кэша, а свежие сообщения подтягиваем фоном.
            if (messageCache.TryGetValue(chat.Id, out var cached) && cached.Loaded)
            {
                curState = cached;
                try { BindOpenChat(cached, chat.Id); }
                catch (Exception ex) { Program.LogCrash("BindOpenChat(cache)", ex); info.Text = "Не удалось показать диалог: " + ex.Message; }
                _ = RefreshNewestAsync(chat.Id, cached, version, SummaryKeyOf(chat), ct);
                return;
            }

            var st = new ChatState();
            curState = st;
            messagePanel.Children.Clear();
            _lastDay = null;
            currentDeal = null;
            RefreshProductList();
            info.Text = "Загрузка последних 24 сообщений…";
            SetChatLoading(true);
            try
            {
                var page = await _host.GetMessagesPageForUiAsync(chat.Id, null, ct);
                if (version != chatLoadVersion) return;
                st.Messages.AddRange(page.Messages);
                st.Cursor = page.BeforeCursor;
                if (!string.IsNullOrWhiteSpace(st.Cursor)) st.UsedCursors.Add(st.Cursor);
                st.HasOlder = page.HasOlder;
                st.Loaded = true;
                st.SummaryKey = SummaryKeyOf(chat);
                if (messageCache.Count > 30) messageCache.Remove(messageCache.Keys.First());
                messageCache[chat.Id] = st;
                BindOpenChat(st, chat.Id);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (version == chatLoadVersion) info.Text = "Не удалось открыть диалог: " + ex.Message; }
            finally { if (version == chatLoadVersion) SetChatLoading(false); }
        }

        // Дошли до верха — подгружаем предыдущие 24 сообщения; нижние остаются на месте.
        async Task LoadOlderAsync()
        {
            var st = curState;
            var chatId = _selectedChatId;
            if (st is null || chatId is null || !st.Loaded || !st.HasOlder || loadingOlder) return;
            loadingOlder = true;
            var version = chatLoadVersion;
            var ct = chatCts?.Token ?? CancellationToken.None;
            info.Text = $"Загружаю предыдущие сообщения… ({st.Messages.Count} уже показано)";
            try
            {
                var page = await _host.GetMessagesPageForUiAsync(chatId, st.Cursor, ct);
                if (version != chatLoadVersion || _selectedChatId != chatId) return;
                CapturePurchaseDatesFromMessages(page.Messages, chatId);
                var existing = new HashSet<string>(st.Messages.Select(m => m.Id), StringComparer.Ordinal);
                var older = page.Messages.Where(m => existing.Add(m.Id)).ToList();
                var nextCursor = page.BeforeCursor;
                var cursorAdvanced = !string.IsNullOrWhiteSpace(nextCursor) && st.UsedCursors.Add(nextCursor);
                st.Cursor = nextCursor;
                st.HasOlder = page.HasOlder && older.Count > 0 && cursorAdvanced;
                if (older.Count > 0)
                {
                    st.Messages.InsertRange(0, older);
                    // Запоминаем расстояние до низа: после добавления сообщений сверху вернём прокрутку на то же место.
                    anchorFromBottom = Math.Max(0, messages.Extent.Height - messages.Viewport.Height - messages.Offset.Y);
                    anchorSetAt = DateTime.UtcNow;
                    stickBottom = false;
                    PrependMessages(older);
                }
                UpdateInfo(st);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (version == chatLoadVersion) info.Text = "Не удалось загрузить старые сообщения: " + ex.Message; }
            finally { if (version == chatLoadVersion) loadingOlder = false; }
        }

        messages.ScrollChanged += (_, _) =>
        {
            var extentH = messages.Extent.Height;
            var viewH = messages.Viewport.Height;
            var offY = messages.Offset.Y;
            var extentChanged = Math.Abs(extentH - lastExtentH) > 0.5;
            lastExtentH = extentH;

            if (extentChanged)
            {
                // Содержимое выросло: либо возвращаем позицию после подгрузки сверху, либо держимся у нижнего края.
                if (anchorFromBottom is double a && (DateTime.UtcNow - anchorSetAt).TotalSeconds < 3)
                {
                    anchorFromBottom = null;
                    messages.Offset = new Vector(0, Math.Max(0, extentH - viewH - a));
                    return;
                }
                if (stickBottom)
                {
                    if (extentH - viewH - offY > 1) messages.Offset = new Vector(0, Math.Max(0, extentH - viewH));
                    return;
                }
            }
            else
            {
                // Пользователь сам прокрутил: у низа — снова «прилипаем», выше — нет.
                stickBottom = extentH - viewH - offY <= 40;
            }

            if (offY <= 35) _ = LoadOlderAsync();
        };

        // ---------- Загрузка списка, автообновление ----------

        // Догружает статусы сделок для чатов, у которых сделка не определилась ни по списку чатов, ни по продажам:
        // берём последние сообщения чата, находим в них сделку и запрашиваем её точный статус. Открывать чаты вручную не нужно.
        async Task EnrichChatsAsync()
        {
            if (enriching) return;
            enriching = true;
            try
            {
                var todo = _chatCache.Where(c => !enrichedChats.Contains(c.Id) && ChatDealCategories(c).Count == 0).ToList();
                if (todo.Count == 0) return;
                var gate = new SemaphoreSlim(3);
                var done = 0;
                var failed = 0;
                var tasks = todo.Select(async chat =>
                {
                    await gate.WaitAsync();
                    try
                    {
                        var page = await _host.GetMessagesPageForUiAsync(chat.Id, null, CancellationToken.None);
                        var dealId = page.Messages.OrderByDescending(m => m.CreatedAt ?? DateTimeOffset.MinValue)
                            .Select(m => m.DealId).FirstOrDefault(id => !string.IsNullOrEmpty(id));
                        Deal? found = null;
                        if (dealId is not null)
                            found = salesById.TryGetValue(dealId, out var known) ? known : await _host.GetDealForUiAsync(dealId, CancellationToken.None);
                        // Только после успешного ответа, чтобы сбой сети не пометил чат «проверенным».
                        Dispatcher.UIThread.Post(() =>
                        {
                            CapturePurchaseDatesFromMessages(page.Messages, chat.Id);
                            enrichedChats.Add(chat.Id);
                            if (found is not null) dealByChat[chat.Id] = found with { ChatId = chat.Id };
                        });
                    }
                    catch { Interlocked.Increment(ref failed); /* повторим при следующем обновлении */ }
                    finally
                    {
                        gate.Release();
                        if (Interlocked.Increment(ref done) % 10 == 0) Dispatcher.UIThread.Post(SyncChatList);
                    }
                }).ToList();
                await Task.WhenAll(tasks);
                Dispatcher.UIThread.Post(() =>
                {
                    SyncChatList();
                    if (failed > 0 && failed != lastEnrichFailed)
                        AppendLog($"⚠️ Не удалось определить статус сделки для чатов: {failed} из {todo.Count}. Будет повторено.");
                    lastEnrichFailed = failed;
                });
            }
            catch (Exception logEx) { global::PlayerokBridge.Program.LogCrash("MainWindow.cs:1617", logEx); }
            finally { enriching = false; }
        }

        string? lastSalesError = null;
        async Task RefreshSalesAsync(bool full = true)
        {
            if (salesLoading) return;
            salesLoading = true;
            try
            {
                // Полная загрузка истории — при запуске и по кнопке; в остальное время достаточно свежих страниц.
                var sales = full
                    ? await Task.Run(() => _host.GetSalesForUiAsync(CancellationToken.None))
                    : await Task.Run(() => _host.GetRecentSalesForUiAsync(2, CancellationToken.None));
                MergeSalesForUi(sales);
                var map = full ? new Dictionary<string, Deal>() : new Dictionary<string, Deal>(salesByChat);
                var byId = full ? new Dictionary<string, Deal>() : new Dictionary<string, Deal>(salesById);
                var byBuyer = new Dictionary<string, Deal>(full ? new Dictionary<string, Deal>() : salesByBuyer, StringComparer.OrdinalIgnoreCase);
                lastSalesError = null;
                foreach (var sale in sales)
                {
                    byId[sale.Id] = sale;
                    if (!string.IsNullOrWhiteSpace(sale.BuyerName)) byBuyer[sale.BuyerName!] = sale;
                }
                foreach (var group in byId.Values.Where(d => !string.IsNullOrEmpty(d.ChatId)).GroupBy(d => d.ChatId!, StringComparer.Ordinal))
                {
                    var groupPrimary = SelectPrimaryDeal(group);
                    if (groupPrimary is not null) map[group.Key] = groupPrimary;
                }
                salesByChat = map; salesById = byId; salesByBuyer = byBuyer;
                SyncChatList();
                if (_selectedChatId is { } sid && (currentDeal is null || GetPrimaryDealForChat(sid) is { } primary && primary.Id != currentDeal.Id)
                    && (GetPrimaryDealForChat(sid) ?? (map.TryGetValue(sid, out var found) ? found : null)) is { } foundDeal)
                {
                    currentDeal = foundDeal;
                    RefreshProductList();
                    if (curState is { Loaded: true } s) UpdateInfo(s);
                }
            }
            catch (Exception ex)
            {
                // Без списка продаж фильтры опираются только на данные чатов, поэтому ошибку показываем в журнале.
                if (ex.Message != lastSalesError) AppendLog("⚠️ Не удалось загрузить продажи для фильтров чатов: " + ex.Message);
                lastSalesError = ex.Message;
            }
            finally { salesLoading = false; }
            _ = EnrichChatsAsync();
        }

        async Task LoadChats()
        {
            if (loadingChats) return;
            loadingChats = true;
            refresh.IsEnabled = false;
            try
            {
                var all = await _host.GetChatsForUiAsync(CancellationToken.None);
                MergeChats(all, preferIncoming: false);
                SyncChatList();
                await RefreshSalesAsync(); // фильтры сразу получают статусы всех сделок, а не только открытых чатов
            }
            catch (Exception ex)
            {
                info.Text = "Не удалось загрузить чаты: " + ex.Message;
                AppendLog("⚠️ Не удалось загрузить чаты: " + ex.Message);
            }
            finally { refresh.IsEnabled = true; loadingChats = false; }
        }

        // Свежая первая страница чатов от монитора (каждые ~3 секунды).
        void OnChatsPolled(IReadOnlyList<ChatSummary> polled)
        {
            var markRead = false;
            var incoming = new List<ChatSummary>();
            foreach (var p in polled)
            {
                var q = p;
                if (q.Id == _selectedChatId && _chatsVisible && q.Unread > 0) { q = q with { Unread = 0 }; markRead = true; }
                incoming.Add(q);
            }
            if (MergeChats(incoming, preferIncoming: true) || rowIds.Count != _chatCache.Count) SyncChatList();

            if (_selectedChatId is { } sid)
            {
                if (markRead) _ = _host.MarkChatReadForUiAsync(sid);
                // В открытом чате появилось новое сообщение — дорисовываем его без перезагрузки истории.
                var sc = polled.FirstOrDefault(c => c.Id == sid);
                if (sc is not null && curState is { Loaded: true } st)
                {
                    var key = SummaryKeyOf(sc);
                    if (key != st.SummaryKey)
                    {
                        var ct = chatCts?.Token ?? CancellationToken.None;
                        _ = RefreshNewestAsync(sid, st, chatLoadVersion, key, ct);
                    }
                }
            }
        }

        void OnChatEvent(PlayerokEvent e)
        {
            if (e is DealConfirmedEvent or DealRolledBackEvent or DealHasProblemEvent or DealProblemResolvedEvent)
                _ = RefreshSalesAsync(false);

            if (e is NewDealEvent nd)
            {
                // Не ждём обновления полного списка продаж: фиксируем сделку и дату её покупки сразу.
                var purchase = _salesHistory.CapturePurchase(nd.Deal, DateTimeOffset.Now);
                MergeSalesForUi(new[] { purchase });
                var cid = nd.ChatId ?? nd.Deal.ChatId;
                if (!string.IsNullOrEmpty(cid) && !(cid == _selectedChatId && _chatsVisible))
                {
                    _dealAlertChats.Add(cid);
                    SyncChatList();
                }
                if (!string.IsNullOrEmpty(cid)) { enrichedChats.Remove(cid); dealByChat.Remove(cid); }
                _sound.Play(SoundKind.Purchase);
                TrayAlert($"Новая покупка: {nd.Deal.ItemName ?? "без названия"} · {nd.Deal.Price:0.##} ₽");
                ShowToast("Новая сделка PlayerOK", $"{nd.Deal.ItemName ?? "Покупка"} · {nd.Deal.Price:0.##} ₽");
                _ = RefreshSalesAsync(false);
                return;
            }

            if (e is NewMessageEvent snd && snd.Message.AuthorId != _host.UiUserId
                && !(snd.Message.Text?.TrimStart().StartsWith("{{", StringComparison.Ordinal) ?? false))
                _sound.Play(SoundKind.Message); // тот же звук, что и при отправке своего сообщения

            if (e is NewMessageEvent nm && nm.Message.AuthorId != _host.UiUserId
                && !(nm.ChatId == _selectedChatId && _chatsVisible))
            {
                var partner = _chatCache.FirstOrDefault(c => c.Id == nm.ChatId)?.PartnerName ?? "Новый чат";
                var preview = string.IsNullOrWhiteSpace(nm.Message.Text)
                    ? (nm.Message.HasImages ? "Отправлено фото" : "Новое сообщение")
                    : nm.Message.Text!;
                ShowToast($"Сообщение: {partner}", preview);
            }
        }

        _host.ChatsUpdated += polled => Dispatcher.UIThread.Post(() => { try { OnChatsPolled(polled); } catch (Exception ex) { Program.LogCrash("OnChatsPolled", ex); } });
        _host.ChatEvent += e => Dispatcher.UIThread.Post(() => { try { OnChatEvent(e); } catch (Exception ex) { Program.LogCrash("OnChatEvent", ex); } });
        _host.AppConnected += () => Dispatcher.UIThread.Post(() => { _ = LoadChats(); });

        // Вернулись на вкладку «Чаты»: открытый чат считается прочитанным.
        _onChatsShown = () =>
        {
            if (_selectedChatId is { } sid)
            {
                _dealAlertChats.Remove(sid);
                var idx = _chatCache.FindIndex(c => c.Id == sid);
                if (idx >= 0 && _chatCache[idx].Unread > 0)
                {
                    _chatCache[idx] = _chatCache[idx] with { Unread = 0 };
                    _ = _host.MarkChatReadForUiAsync(sid);
                }
            }
            SyncChatList();
        };

        chats.SelectionChanged += async (_, _) =>
        {
            if (suppressSelection) return;
            var i = chats.SelectedIndex;
            if (i < 0 || i >= visibleChats.Count) return;
            var chat = visibleChats[i];
            if (chat.Id == _selectedChatId && curState is { Loaded: true }) return;
            try { await OpenChatAsync(chat); }
            catch (Exception ex) { Program.LogCrash("OpenChatAsync", ex); info.Text = "Не удалось открыть диалог: " + ex.Message; }
        };

        foreach (var pair in filterButtons)
            pair.Value.Click += (_, _) => { activeChatFilter = pair.Key; SyncChatList(); _ = RefreshSalesAsync(false); };

        // Статусы сделок меняются (оплачено → выдано → завершено), поэтому периодически перечитываем продажи.
        var salesTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
        salesTimer.Tick += (_, _) => { if (_host.HasAppConnection) _ = RefreshSalesAsync(false); };
        salesTimer.Start();
        refresh.Click += async (_, _) => await LoadChats();
        if (_host.HasAppConnection) _ = LoadChats(); // окно создаётся до подключения; на случай повторной сборки вкладки

        // Enter — отправить, Shift+Enter — новая строка.
        var sending = false;
        byte[]? pendingImage = null;
        var pendingName = "";
        attach.Click += async (_, _) =>
        {
            if (pendingImage is not null)
            {
                pendingImage = null; attach.Content = "📎"; info.Text = "Фото убрано.";
                return;
            }
            try
            {
                var top = TopLevel.GetTopLevel(this);
                if (top is null) return;
                var files = await top.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
                {
                    Title = "Выберите фото", AllowMultiple = false,
                    FileTypeFilter = new[] { Avalonia.Platform.Storage.FilePickerFileTypes.ImageAll }
                });
                if (files.Count == 0) return;
                await using var stream = await files[0].OpenReadAsync();
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms);
                pendingImage = ms.ToArray(); pendingName = files[0].Name;
                attach.Content = "📎✓";
                info.Text = $"Фото «{pendingName}» прикреплено. Нажмите Enter, чтобы отправить (можно с подписью).";
                compose.Focus();
            }
            catch (Exception ex) { info.Text = "Не удалось прикрепить фото: " + ex.Message; }
        };
        compose.AddHandler(Avalonia.Input.InputElement.KeyDownEvent, async (object? _, Avalonia.Input.KeyEventArgs e) =>
        {
            if (e.Key != Avalonia.Input.Key.Enter || e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift)) return;
            e.Handled = true;
            if (sending || string.IsNullOrWhiteSpace(_selectedChatId) || (string.IsNullOrWhiteSpace(compose.Text) && pendingImage is null)) return;
            var chatId = _selectedChatId!;
            var textToSend = compose.Text?.Trim() ?? "";
            var imageToSend = pendingImage; var imageName = pendingName;
            pendingImage = null; attach.Content = "📎";
            sending = true;
            // Показываем сообщение сразу, а отправку выполняем в фоне: без повторной загрузки всей истории.
            compose.Text = "";
            var local = new ChatMessage("local-" + Guid.NewGuid().ToString("N"), _host.UiUserId, null, null,
                textToSend.Length > 0 ? textToSend : (imageToSend is null ? "" : "📷 Фото отправляется…"), DateTimeOffset.Now);
            pendingEchoes.Add(textToSend);
            AddMessageBubble(messagePanel, local);
            stickBottom = true;
            _sound.Play(SoundKind.Message);
            info.Text = "Отправка…";
            try
            {
                if (imageToSend is not null)
                    await Task.Run(() => _host.SendImageFromUiAsync(chatId, imageToSend, imageName, textToSend, CancellationToken.None));
                else
                    await Task.Run(() => _host.SendChatMessageFromUiAsync(chatId, textToSend, CancellationToken.None));
                info.Text = imageToSend is null ? "Сообщение отправлено." : "Фото отправлено.";
            }
            catch (Exception ex)
            {
                pendingEchoes.Remove(textToSend);
                info.Text = "Ошибка отправки: " + ex.Message;
                if (string.IsNullOrEmpty(compose.Text)) compose.Text = textToSend; // вернуть текст, чтобы не потерять
                if (imageToSend is not null && pendingImage is null) { pendingImage = imageToSend; pendingName = imageName; attach.Content = "📎✓"; }
            }
            finally { sending = false; }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        var left = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,*")
        };
        refresh.Margin = new Thickness(0, 0, 0, 6);
        Grid.SetRow(refresh, 0);
        left.Children.Add(refresh);
        Grid.SetRow(chatSearch, 1);
        left.Children.Add(chatSearch);
        Grid.SetRow(unreadOnly, 2);
        left.Children.Add(unreadOnly);
        Grid.SetRow(filtersPanel, 3);
        left.Children.Add(filtersPanel);
        Grid.SetRow(chats, 4);
        left.Children.Add(chats);
        chatSearch.TextChanged += (_, _) => SyncChatList();
        unreadOnly.IsCheckedChanged += (_, _) => SyncChatList();

        // Grid ограничивает высоту ScrollViewer. StackPanel здесь использовать нельзя:
        // он измеряет дочерний ScrollViewer с бесконечной высотой, из-за чего история обрезается.
        var rightGrid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto")
        };
        var actionsRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { productButton, confirmSentButton, refundButton }
        };
        Grid.SetRow(actionsRow, 0); rightGrid.Children.Add(actionsRow);
        Grid.SetRow(chatTitle, 1); rightGrid.Children.Add(chatTitle);
        Grid.SetRow(info, 2); rightGrid.Children.Add(info);
        Grid.SetRow(chatMessagesHost, 3); rightGrid.Children.Add(chatMessagesHost);
        compose.MinHeight = 44;
        var composeGrid = new Grid { Margin = new Thickness(0, 6, 0, 0), ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        composeGrid.Children.Add(attach);
        Grid.SetColumn(compose, 1); composeGrid.Children.Add(compose);
        Grid.SetRow(composeGrid, 4); rightGrid.Children.Add(composeGrid);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("300,*"), Margin = new Thickness(10) };
        grid.Children.Add(left);
        var rightBorder = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#A6191C23")),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16),
            Margin = new Thickness(10, 0, 0, 0),
            Child = rightGrid
        };
        Grid.SetColumn(rightBorder, 1);
        grid.Children.Add(rightBorder);
        return grid;
    }

    // ---------- AFK-режим (внутри вкладки «Ответы по товарам») ----------

    private Control BuildAfkCard()
    {
        var afk = _host.Afk;
        var enabled = new CheckBox
        {
            Content = "🌙 Включить AFK-режим (второе сообщение после ответа по товару)",
            IsChecked = afk.Enabled, FontWeight = FontWeight.SemiBold
        };
        var text = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 110, Text = afk.Text };
        var delay = new Slider { Minimum = 0, Maximum = 30, Width = 320, Value = Math.Clamp(afk.DelaySeconds, 0, 30) };
        var delayLabel = new TextBlock { Opacity = .8, Margin = new Thickness(0, 6, 0, 0) };
        var withoutProduct = new CheckBox
        {
            Content = "Отправлять AFK, даже если ответ по товару не подобран",
            IsChecked = afk.SendWithoutProduct
        };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = .85 };
        var preview = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = .9 };
        var loading = true;

        void UpdateDelayLabel() =>
            delayLabel.Text = delay.Value < 0.05 ? "Пауза перед AFK: без паузы" : $"Пауза после ответа по товару: {delay.Value:0.#} с";
        UpdateDelayLabel();
        loading = false;

        // Включение/выключение сохраняется сразу — удобно быстро уйти в AFK и вернуться.
        void Persist(string okMessage)
        {
            try
            {
                afk.Enabled = enabled.IsChecked == true;
                afk.SendWithoutProduct = withoutProduct.IsChecked == true;
                afk.DelaySeconds = Math.Round(delay.Value, 1);
                if (!string.IsNullOrWhiteSpace(text.Text)) afk.Text = text.Text!;
                afk.Save();
                status.Text = okMessage;
            }
            catch (Exception ex) { status.Text = "❌ " + ex.Message; }
        }

        enabled.IsCheckedChanged += (_, _) =>
        {
            if (loading) return;
            Persist(enabled.IsChecked == true
                ? "🌙 AFK включён: после каждого ответа по товару покупатель получит и AFK-сообщение."
                : "☀️ AFK выключен.");
        };
        delay.PropertyChanged += (_, e) =>
        {
            if (e.Property == Avalonia.Controls.Primitives.RangeBase.ValueProperty) UpdateDelayLabel();
        };

        var save = new Button { Content = "💾 Сохранить AFK" };
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(text.Text)) { status.Text = "❌ Текст AFK-сообщения не должен быть пустым."; return; }
            Persist("✅ AFK-настройки сохранены (Python-бот подхватит их без перезапуска).");
        };
        var show = new Button { Content = "👁 Предпросмотр" };
        show.Click += (_, _) =>
        {
            var sample = new DealInfo("A1B2C3", "Покупатель", _host.UiUsername ?? "Продавец", "Brawl Stars 30 гемов", 149m, null);
            preview.Text = ReplyRenderer.Render(text.Text ?? "", sample, "Продавец");
        };
        var reset = new Button { Content = "↺ Текст по умолчанию" };
        reset.Click += (_, _) => text.Text = AfkSettings.DefaultText;

        var card = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#A61A1D26")),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 18, 0, 0),
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = "AFK-режим", FontSize = 18, FontWeight = FontWeight.SemiBold },
                    new TextBlock
                    {
                        Text = "Порядок: 1) автоответ по товару → 2) пауза → 3) AFK-сообщение. Отправляется один раз на сделку.",
                        Opacity = .7, TextWrapping = TextWrapping.Wrap
                    },
                    enabled,
                    Caption("Текст AFK-сообщения. Подстановки в тексте: {buyer} — ник покупателя, {seller} — ваш ник, " +
                            "{item} — название товара, {price} — цена, {deal_id} — номер сделки. Пример: Здравствуйте, {buyer}!"),
                    text,
                    delayLabel, delay,
                    withoutProduct,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 6, 0, 0), Children = { save, show, reset } },
                    status, preview
                }
            }
        };
        return card;
    }

    // ---------- Мои товары, продажи и продвижение объявлений ----------

    private static (string Text, string Color, int Group) ListingStatus(string name) => name switch
    {
        "APPROVED" or "ACTIVE" => ("Активно", "#2E9E5B", 1),
        "PENDING_APPROVAL" or "PENDING_MODERATION" => ("На проверке", "#C98A1B", 2),
        "DRAFT" => ("Черновик", "#5B6577", 3),
        "EXPIRED" => ("Истекло", "#8A5A44", 4),
        "DECLINED" => ("Отклонено", "#B8434B", 4),
        "BLOCKED" => ("Заблокировано", "#B8434B", 4),
        "SOLD" => ("Продано", "#3F6FD8", 4),
        _ => (name, "#5B6577", 4),
    };

    private static readonly (string A, string B)[] CardPalette =
    {
        ("#3B5BDB", "#7048E8"), ("#0CA678", "#1C7ED6"), ("#E8590C", "#C2255C"), ("#7048E8", "#AE3EC9"),
        ("#1098AD", "#3B5BDB"), ("#D9480F", "#F08C00"), ("#2F9E44", "#0CA678"), ("#C2255C", "#7048E8"),
    };

    private async Task<ListingSummary?> ShowCreateListingWizardAsync()
    {
        var gameSearch = new TextBox { Watermark = "Например: PUBG Mobile, Brawl Stars, Fortnite" };
        var gameSelect = new ComboBox { MinWidth = 300 };
        var categorySelect = new ComboBox { MinWidth = 300 };
        var obtainingSelect = new ComboBox { MinWidth = 300 };
        var nameInput = new TextBox { Watermark = "Название объявления" };
        var descriptionInput = new TextBox { Watermark = "Описание товара", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 100 };
        var priceInput = new TextBox { Watermark = "Цена в рублях, например 250" };
        var optionsPanel = new StackPanel { Spacing = 7 };
        var fieldsPanel = new StackPanel { Spacing = 7 };
        var photosInfo = new TextBlock { Text = "Фотографии не добавлены (необязательно).", Opacity = .65 };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = .85 };
        var searchGameButton = new Button { Content = "Найти игру" };
        var loadCategoriesButton = new Button { Content = "Загрузить категории" };
        var loadSchemaButton = new Button { Content = "Загрузить характеристики и поля" };
        var photosButton = new Button { Content = "🖼 Добавить фотографии" };
        var createButton = new Button { Content = "Создать черновик", MinWidth = 150 };
        var cancelButton = new Button { Content = "Отмена" };

        List<GameChoice> games = new();
        List<GameCategoryChoice> categories = new();
        List<GameObtainingChoice> obtainingTypes = new();
        List<GameDataField> dataFields = new();
        List<(List<GameCategoryOption> Choices, ComboBox Selector)> optionGroups = new();
        List<(GameDataField Field, TextBox Input)> fieldControls = new();
        string? categoriesForGameId = null;
        string? schemaForCategoryId = null;
        string? fieldsForObtainingId = null;
        List<CreateItemAttachment> attachments = new();
        GameCategoryChoice? activeCategory = null;
        bool busy = false;
        bool suppressObtainingSelection = false;

        async Task LoadSelectedObtainingFieldsAsync()
        {
            if (activeCategory is null || obtainingSelect.SelectedIndex < 0 || obtainingSelect.SelectedIndex >= obtainingTypes.Count)
                return;

            var categoryId = activeCategory.Id;
            var obtainingId = obtainingTypes[obtainingSelect.SelectedIndex].Id;
            fieldsForObtainingId = null;
            dataFields.Clear();
            fieldControls.Clear();
            fieldsPanel.Children.Clear();
            status.Text = "Загружаю поля выбранного способа передачи…";
            try
            {
                var loaded = await _host.GetItemDataFieldsForUiAsync(categoryId, obtainingId, CancellationToken.None);
                if (activeCategory?.Id != categoryId || obtainingSelect.SelectedIndex < 0 ||
                    obtainingSelect.SelectedIndex >= obtainingTypes.Count || obtainingTypes[obtainingSelect.SelectedIndex].Id != obtainingId)
                    return; // Пользователь успел выбрать другой способ — не показываем устаревшую схему.

                dataFields = loaded.ToList();
                fieldsForObtainingId = obtainingId;
                foreach (var field in dataFields)
                {
                    var input = new TextBox { Watermark = field.Required ? "Обязательное поле" : "Можно оставить пустым" };
                    fieldsPanel.Children.Add(new TextBlock
                    {
                        Text = field.Required ? field.Label + " *" : field.Label,
                        FontWeight = FontWeight.SemiBold,
                    });
                    fieldsPanel.Children.Add(input);
                    fieldControls.Add((field, input));
                }
                status.Text = $"Поля загружены: {fieldControls.Count}. Обязательные поля отмечены *.";
            }
            catch (Exception ex)
            {
                status.Text = "Не удалось загрузить поля способа передачи: " + ex.Message;
            }
        }

        obtainingSelect.SelectionChanged += async (_, _) =>
        {
            if (!suppressObtainingSelection)
                await LoadSelectedObtainingFieldsAsync();
        };

        var window = new Window
        {
            Title = "Новое объявление PlayerOK",
            Width = 760,
            Height = 820,
            MinWidth = 620,
            MinHeight = 650,
            CanResize = true,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.Parse("#17191F")),
            Foreground = new SolidColorBrush(Color.Parse("#F2F4F8")),
        };

        searchGameButton.Click += async (_, _) =>
        {
            var query = gameSearch.Text?.Trim() ?? "";
            if (query.Length < 2)
            {
                status.Text = "Введите хотя бы два символа названия игры или приложения.";
                return;
            }
            searchGameButton.IsEnabled = false;
            status.Text = "Ищу игру в каталоге PlayerOK…";
            try
            {
                games = (await _host.SearchGamesForUiAsync(query, CancellationToken.None)).ToList();
                gameSelect.ItemsSource = games.Select(x => x.Name).ToList();
                gameSelect.SelectedIndex = games.Count > 0 ? 0 : -1;
                categorySelect.ItemsSource = Array.Empty<string>();
                obtainingSelect.ItemsSource = Array.Empty<string>();
                categories.Clear(); obtainingTypes.Clear(); dataFields.Clear(); activeCategory = null;
                categoriesForGameId = null; schemaForCategoryId = null; fieldsForObtainingId = null;
                optionsPanel.Children.Clear(); fieldsPanel.Children.Clear();
                status.Text = games.Count == 0
                    ? "Совпадений не найдено. Попробуйте другое название."
                    : $"Найдено игр и приложений: {games.Count}. Выберите нужную запись и загрузите категории.";
            }
            catch (Exception ex) { status.Text = "Не удалось найти игру: " + ex.Message; }
            finally { searchGameButton.IsEnabled = true; }
        };

        loadCategoriesButton.Click += async (_, _) =>
        {
            if (gameSelect.SelectedIndex < 0 || gameSelect.SelectedIndex >= games.Count)
            {
                status.Text = "Сначала найдите и выберите игру.";
                return;
            }
            loadCategoriesButton.IsEnabled = false;
            status.Text = "Загружаю категории…";
            try
            {
                var gameId = games[gameSelect.SelectedIndex].Id;
                categories = (await _host.GetGameCategoriesForUiAsync(gameId, CancellationToken.None)).ToList();
                categoriesForGameId = gameId;
                categorySelect.ItemsSource = categories.Select(x => x.Name).ToList();
                categorySelect.SelectedIndex = categories.Count > 0 ? 0 : -1;
                obtainingSelect.ItemsSource = Array.Empty<string>();
                obtainingTypes.Clear(); activeCategory = null; dataFields.Clear();
                schemaForCategoryId = null; fieldsForObtainingId = null;
                optionsPanel.Children.Clear(); fieldsPanel.Children.Clear();
                status.Text = categories.Count == 0
                    ? "PlayerOK не вернул категории для этой игры."
                    : $"Категорий: {categories.Count}. Выберите категорию и загрузите её схему.";
            }
            catch (Exception ex) { status.Text = "Не удалось загрузить категории: " + ex.Message; }
            finally { loadCategoriesButton.IsEnabled = true; }
        };

        loadSchemaButton.Click += async (_, _) =>
        {
            if (categorySelect.SelectedIndex < 0 || categorySelect.SelectedIndex >= categories.Count)
            {
                status.Text = "Сначала выберите категорию.";
                return;
            }
            loadSchemaButton.IsEnabled = false;
            status.Text = "Загружаю способы передачи и поля выбранной категории…";
            try
            {
                var preview = categories[categorySelect.SelectedIndex];
                activeCategory = await _host.GetGameCategoryDetailsForUiAsync(preview.Id, CancellationToken.None);
                if (activeCategory is null) throw new InvalidOperationException("PlayerOK не вернул данные категории.");
                schemaForCategoryId = activeCategory.Id;
                fieldsForObtainingId = null;
                obtainingTypes = (await _host.GetObtainingTypesForUiAsync(activeCategory.Id, CancellationToken.None)).ToList();
                suppressObtainingSelection = true;
                obtainingSelect.ItemsSource = obtainingTypes.Select(x => x.Name).ToList();
                obtainingSelect.SelectedIndex = obtainingTypes.Count > 0 ? 0 : -1;
                suppressObtainingSelection = false;
                if (obtainingTypes.Count == 0)
                    throw new InvalidOperationException("Для этой категории нет доступных способов передачи.");

                optionsPanel.Children.Clear();
                optionGroups.Clear();
                foreach (var group in activeCategory.Options.GroupBy(x => x.Group, StringComparer.OrdinalIgnoreCase))
                {
                    var choices = group.ToList();
                    var selector = new ComboBox
                    {
                        MinWidth = 300,
                        ItemsSource = choices.Select(x => x.Label).ToList(),
                        SelectedIndex = -1,
                    };
                    optionsPanel.Children.Add(new TextBlock { Text = group.Key, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 5, 0, 0) });
                    optionsPanel.Children.Add(selector);
                    // Группа может быть пропущена, если характеристика неприменима к конкретному лоту.
                    optionGroups.Add((choices, selector));
                }

                await LoadSelectedObtainingFieldsAsync();
                status.Text = $"Схема загружена: {optionGroups.Count} групп характеристик, {fieldControls.Count} полей продавца. Обязательные поля отмечены *.";
            }
            catch (Exception ex)
            {
                activeCategory = null;
                schemaForCategoryId = null; fieldsForObtainingId = null;
                optionsPanel.Children.Clear(); fieldsPanel.Children.Clear();
                status.Text = "Не удалось загрузить схему: " + ex.Message;
            }
            finally { loadSchemaButton.IsEnabled = true; }
        };

        photosButton.Click += async (_, _) =>
        {
            try
            {
                var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Выберите фотографии объявления",
                    AllowMultiple = true,
                    FileTypeFilter = new[] { FilePickerFileTypes.ImageAll },
                });
                foreach (var file in files.Take(Math.Max(0, 10 - attachments.Count)))
                {
                    await using var stream = await file.OpenReadAsync();
                    using var memory = new MemoryStream();
                    await stream.CopyToAsync(memory);
                    if (memory.Length > 0) attachments.Add(new CreateItemAttachment(file.Name, memory.ToArray()));
                }
                photosInfo.Text = attachments.Count == 0
                    ? "Фотографии не добавлены (необязательно)."
                    : $"Добавлено фотографий: {attachments.Count}/10.";
            }
            catch (Exception ex) { status.Text = "Не удалось прочитать фотографии: " + ex.Message; }
        };

        createButton.Click += async (_, _) =>
        {
            if (busy) return;
            if (gameSelect.SelectedIndex < 0 || gameSelect.SelectedIndex >= games.Count ||
                categorySelect.SelectedIndex < 0 || categorySelect.SelectedIndex >= categories.Count ||
                obtainingSelect.SelectedIndex < 0 || obtainingSelect.SelectedIndex >= obtainingTypes.Count ||
                activeCategory is null || categoriesForGameId != games[gameSelect.SelectedIndex].Id ||
                schemaForCategoryId != categories[categorySelect.SelectedIndex].Id ||
                fieldsForObtainingId != obtainingTypes[obtainingSelect.SelectedIndex].Id)
            {
                status.Text = "Проверьте выбор игры, категории и способа передачи. После изменения выбора повторно загрузите схему.";
                return;
            }
            if (string.IsNullOrWhiteSpace(nameInput.Text) || string.IsNullOrWhiteSpace(descriptionInput.Text))
            {
                status.Text = "Заполните название и описание объявления.";
                return;
            }
            if (!int.TryParse((priceInput.Text ?? "").Replace(" ", "").Replace("₽", ""), out var price) || price <= 0)
            {
                status.Text = "Цена должна быть положительным целым числом в рублях.";
                return;
            }

            var fieldValues = new List<(string FieldId, string Value)>();
            foreach (var (field, input) in fieldControls)
            {
                var value = input.Text?.Trim() ?? "";
                if (field.Required && value.Length == 0)
                {
                    status.Text = $"Заполните обязательное поле «{field.Label}».";
                    input.Focus();
                    return;
                }
                if (value.Length > 0) fieldValues.Add((field.Id, value));
            }

            var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (choices, selector) in optionGroups)
            {
                if (selector.SelectedIndex < 0 || selector.SelectedIndex >= choices.Count) continue;
                var option = choices[selector.SelectedIndex];
                attributes[option.Field] = option.Value;
            }

            busy = true;
            createButton.IsEnabled = false;
            status.Text = "Создаю черновик на PlayerOK…";
            try
            {
                var draft = await _host.CreateItemDraftForUiAsync(
                    activeCategory.Id,
                    obtainingTypes[obtainingSelect.SelectedIndex].Id,
                    nameInput.Text!.Trim(), price, descriptionInput.Text!.Trim(),
                    attributes, fieldValues, attachments, CancellationToken.None);
                status.Text = "✅ Черновик создан. Теперь можно выбрать тариф публикации.";
                window.Close((ListingSummary?)draft);
            }
            catch (Exception ex) { status.Text = "❌ Не удалось создать черновик: " + ex.Message; }
            finally { busy = false; createButton.IsEnabled = true; }
        };

        cancelButton.Click += (_, _) => window.Close((ListingSummary?)null);
        window.Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = new StackPanel
            {
                Margin = new Thickness(20), Spacing = 9,
                Children =
                {
                    new TextBlock { Text = "Создание товара", FontSize = 23, FontWeight = FontWeight.Bold },
                    new TextBlock { Text = "Схема полей загружается из PlayerOK для выбранной игры. Поэтому категории PUBG Mobile, Brawl Stars, Standoff 2 и других игр не захардкожены в приложении.", Opacity = .7, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = "1. Игра или приложение", FontSize = 16, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) },
                    gameSearch,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { searchGameButton } },
                    gameSelect,
                    new TextBlock { Text = "2. Категория и способ передачи", FontSize = 16, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) },
                    loadCategoriesButton,
                    categorySelect,
                    loadSchemaButton,
                    obtainingSelect,
                    new TextBlock { Text = "3. Характеристики", FontSize = 16, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) },
                    new TextBlock { Text = "В каждой группе выберите подходящий вариант. Группу можно оставить пустой, если она не относится к товару.", Opacity = .65, TextWrapping = TextWrapping.Wrap },
                    optionsPanel,
                    fieldsPanel,
                    new TextBlock { Text = "4. Описание объявления", FontSize = 16, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) },
                    nameInput,
                    descriptionInput,
                    priceInput,
                    photosButton,
                    photosInfo,
                    status,
                    new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancelButton, createButton } },
                }
            }
        };
        return await window.ShowDialog<ListingSummary?>(this);
    }

    private async Task<bool> ConfirmDialogAsync(string title, string message)
    {
        var window = new Window
        {
            Title = title, Width = 440, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false
        };
        var yes = new Button { Content = "Да, подтвердить", Padding = new Thickness(14, 6) };
        var no = new Button { Content = "Отмена", Padding = new Thickness(14, 6) };
        yes.Click += (_, _) => window.Close(true);
        no.Click += (_, _) => window.Close(false);
        window.Content = new StackPanel
        {
            Margin = new Thickness(20), Spacing = 14,
            Children =
            {
                new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeight.Bold },
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { no, yes } }
            }
        };
        return await window.ShowDialog<bool>(this);
    }

    private async Task<ItemPriorityStatus?> ChoosePriorityStatusAsync(
        IReadOnlyList<ItemPriorityStatus> options, ListingSummary item, bool publishDraft)
    {
        var selector = new ComboBox
        {
            ItemsSource = options.Select(x => x.Price == 0
                ? $"{x.Name} · бесплатно · {x.Period} дн."
                : $"{x.Name} · {x.Price} ₽ · {x.Period} дн.").ToList(),
            SelectedIndex = 0,
            MinWidth = 340,
        };
        var cost = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
        void UpdateCost()
        {
            var index = selector.SelectedIndex;
            if (index < 0 || index >= options.Count) { cost.Text = "Выберите вариант."; return; }
            var option = options[index];
            cost.Text = option.Price == 0
                ? "Стоимость: бесплатно."
                : $"Стоимость: {option.Price} ₽. Эта сумма будет списана с баланса PlayerOK.";
        }
        selector.SelectionChanged += (_, _) => UpdateCost();
        UpdateCost();

        var window = new Window
        {
            Title = publishDraft ? "Публикация объявления" : "Продвижение объявления",
            Width = 470,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.Parse("#17191F")),
            Foreground = new SolidColorBrush(Color.Parse("#F2F4F8")),
        };
        var accept = new Button { Content = publishDraft ? "Опубликовать" : "Оплатить и поднять" };
        var cancel = new Button { Content = "Отмена" };
        accept.Click += (_, _) =>
        {
            if (selector.SelectedIndex >= 0 && selector.SelectedIndex < options.Count)
                window.Close((ItemPriorityStatus?)options[selector.SelectedIndex]);
        };
        cancel.Click += (_, _) => window.Close((ItemPriorityStatus?)null);
        window.Content = new StackPanel
        {
            Margin = new Thickness(20), Spacing = 12,
            Children =
            {
                new TextBlock { Text = item.Name, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = publishDraft ? "Выберите тариф публикации." : "Выберите тариф поднятия объявления." , Opacity = .7 },
                selector, cost,
                new TextBlock { Text = "Перед продолжением проверьте стоимость. Платное действие запускается только после нажатия кнопки ниже.", FontSize = 11, Opacity = .65, TextWrapping = TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, accept } },
            }
        };
        return await window.ShowDialog<ItemPriorityStatus?>(this);
    }


    // ---------- Профиль (бывшая «Статистика») ----------

    private readonly TextBlock _profBalance = new() { Text = "—", FontSize = 28, FontWeight = FontWeight.Bold };
    private readonly TextBlock _profBalanceMeta = new() { Text = "Ожидание подключения", FontSize = 11, Opacity = .7, TextWrapping = TextWrapping.Wrap };
    private Border? _sideBalanceCard;   // карточка баланса справа сверху (скрывается на вкладке «Профиль»)
    private Action? _onProfileShown;    // обновление профиля при открытии вкладки
    private int _sideBalanceToken;

    /// <summary>Баланс живёт либо справа сверху (обычные вкладки), либо в профиле.</summary>
    private void SetSideBalanceVisible(bool visible)
    {
        var card = _sideBalanceCard;
        if (card is null) return;
        var token = ++_sideBalanceToken;
        if (visible)
        {
            card.IsVisible = true;
            card.Opacity = 1;
        }
        else
        {
            card.Opacity = 0;
            _ = Task.Delay(260).ContinueWith(_ => Dispatcher.UIThread.Post(() =>
            {
                if (token == _sideBalanceToken) card.IsVisible = false;
            }));
        }
    }

    private static readonly string[] MusicThemeKeys =
        { "auto", "lava", "flow", "sunset", "fire", "snow", "bubbles", "aurora", "stars", "solid", "neon", "rain", "dream" };

    private static readonly string[] MusicThemeNames =
    {
        "Автоматически под фон", "Лава-лампа / Ambient", "Переливание / Arpeggio",
        "Закат / Lounge", "Огонь / Dark drive", "Снег / Music box", "Пузыри / Chill",
        "Аврора / Ethereal", "Звёзды / Space", "Минимализм", "Neon synthwave", "Rainy lo-fi", "Dream ambient"
    };

    private Control BuildStatsTab()
    {
        static TextBlock Big() => new() { Text = "—", FontSize = 22, FontWeight = FontWeight.Bold };
        var cardBg = new SolidColorBrush(Color.Parse("#A61D2430"));
        var cardBorder = new SolidColorBrush(Color.Parse("#334A5568"));

        // ----- шапка профиля -----
        var avatarInitial = new TextBlock
        {
            Text = "?", FontSize = 38, FontWeight = FontWeight.Bold,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        var avatarImg = new Image { Stretch = Stretch.UniformToFill };
        var avatar = new Border
        {
            Width = 92, Height = 92, CornerRadius = new CornerRadius(46), ClipToBounds = true,
            Background = new SolidColorBrush(Color.Parse("#554D83FF")), Child = avatarInitial,
        };
        var nameText = new TextBlock { Text = "Профиль", FontSize = 24, FontWeight = FontWeight.Bold };
        var sinceText = new TextBlock { Opacity = .65, FontSize = 12 };
        var header = new Border
        {
            Background = cardBg, BorderBrush = cardBorder, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14), Padding = new Thickness(16),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 16,
                Children =
                {
                    avatar,
                    new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 3, Children = { nameText, sinceText } }
                }
            }
        };

        // ----- период, метрики -----
        var period = new ComboBox { Width = 170, ItemsSource = new[] { "За всё время", "За неделю", "За месяц" }, SelectedIndex = 0 };
        var dealFilter = new ComboBox
        {
            Width = 210, SelectedIndex = 0,
            ItemsSource = new[] { "Все сделки", "Успешные", "Неуспешные (возврат)", "В процессе" }
        };
        var totalV = Big(); var refundV = Big(); var successV = Big(); var progressV = Big(); var incomeV = Big();
        var incomeHint = new TextBlock { Opacity = .65, FontSize = 11, TextWrapping = TextWrapping.Wrap };
        var hint = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = .7, FontSize = 12 };
        var dealsPanel = new StackPanel { Spacing = 6 };

        DateTimeOffset? Since() => period.SelectedIndex switch
        {
            1 => DateTimeOffset.Now.AddDays(-7),
            2 => DateTimeOffset.Now.AddDays(-30),
            _ => null
        };

        // 1 — успешная, 2 — неуспешная (возврат), 3 — в процессе
        static int Kind(Deal d) => d.Status switch
        {
            DealStatus.Confirmed or DealStatus.ConfirmedAutomatically => 1,
            DealStatus.RolledBack => 2,
            DealStatus.Sent => 3,
            // Paid/Pending ещё не означают, что продавец выдал товар.
            _ => 0,
        };

        static (string Text, string Color) DealBadge(Deal d)
        {
            var (t, c) = d.Status switch
            {
                DealStatus.Confirmed => ("Успешно", "#34C759"),
                DealStatus.ConfirmedAutomatically => ("Успешно (авто)", "#34C759"),
                DealStatus.RolledBack => ("Возврат", "#FF3B30"),
                DealStatus.Sent => ("Выдано, ждёт подтверждения", "#FF9F0A"),
                DealStatus.Paid => ("Оплачено", "#FF9F0A"),
                DealStatus.Pending => ("Ожидает оплаты", "#FF9F0A"),
                _ => ("В процессе", "#FF9F0A"),
            };
            if (d.HasProblem && d.Status != DealStatus.RolledBack) t = "⚠ Проблема · " + t;
            return (t, c);
        }

        void Refresh()
        {
            try
            {
                var since = Since();
                var inPeriod = _mySales.Where(d => since is null || (d.CreatedAt.HasValue && d.CreatedAt.Value >= since.Value))
                    .OrderByDescending(d => d.CreatedAt ?? DateTimeOffset.MinValue).ToList();
                var ok = inPeriod.Where(d => Kind(d) == 1).ToList();
                var bad = inPeriod.Where(d => Kind(d) == 2).ToList();
                var wip = inPeriod.Where(d => Kind(d) == 3).ToList();
                totalV.Text = inPeriod.Count.ToString("N0", Ru);
                refundV.Text = bad.Count.ToString("N0", Ru);
                successV.Text = ok.Count.ToString("N0", Ru);
                progressV.Text = wip.Count.ToString("N0", Ru);
                incomeV.Text = $"{ok.Sum(d => d.Price):0.##} ₽";
                incomeHint.Text = wip.Count > 0 ? $"Ещё {wip.Sum(d => d.Price):0.##} ₽ в процессе" : "Сумма успешных сделок";

                var shown = dealFilter.SelectedIndex switch
                {
                    1 => ok, 2 => bad, 3 => wip, _ => inPeriod
                };
                dealsPanel.Children.Clear();
                if (shown.Count == 0)
                    dealsPanel.Children.Add(new TextBlock { Text = _mySales.Count == 0 ? "Сделок пока нет или данные ещё не загружены." : "В этом разделе сделок нет.", Opacity = .65, Margin = new Thickness(2, 6) });
                foreach (var d in shown.Take(200))
                {
                    var (badge, color) = DealBadge(d);
                    var left = new StackPanel
                    {
                        Spacing = 2, VerticalAlignment = VerticalAlignment.Center,
                        Children =
                        {
                            new TextBlock { Text = string.IsNullOrWhiteSpace(d.ItemName) ? "Без названия" : d.ItemName, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                            new TextBlock
                            {
                                Text = string.Join(" · ", new[] { d.BuyerName, d.CreatedAt?.ToLocalTime().ToString("dd.MM.yyyy HH:mm", Ru) }.Where(x => !string.IsNullOrWhiteSpace(x))),
                                FontSize = 11, Opacity = .65
                            },
                        }
                    };
                    var chip = new Border
                    {
                        Background = new SolidColorBrush(Color.Parse(color)) { Opacity = .22 },
                        CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 3), Margin = new Thickness(10, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        Child = new TextBlock { Text = badge, FontSize = 11, Foreground = new SolidColorBrush(Color.Parse(color)), FontWeight = FontWeight.SemiBold }
                    };
                    var price = new TextBlock { Text = $"{d.Price:0.##} ₽", FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center };
                    var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
                    row.Children.Add(left);
                    Grid.SetColumn(chip, 1); row.Children.Add(chip);
                    Grid.SetColumn(price, 2); row.Children.Add(price);
                    dealsPanel.Children.Add(new Border
                    {
                        Background = cardBg, BorderBrush = cardBorder, BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8), Child = row
                    });
                }
                if (shown.Count > 200)
                    dealsPanel.Children.Add(new TextBlock { Text = $"Показаны первые 200 из {shown.Count}. Уточните период.", Opacity = .6, FontSize = 11 });
                hint.Text = "";
            }
            catch (Exception ex)
            {
                hint.Text = "Не удалось обновить статистику: " + ex.Message;
            }
        }
        period.SelectionChanged += (_, _) => Refresh();
        dealFilter.SelectionChanged += (_, _) => Refresh();

        // ----- профиль: ник, аватар, дата регистрации -----
        string? avatarLoaded = null;
        async Task LoadProfileAsync()
        {
            try
            {
                var p = await _host.GetProfileForUiAsync(CancellationToken.None);
                nameText.Text = string.IsNullOrWhiteSpace(p.Username) ? "Профиль" : p.Username;
                avatarInitial.Text = string.IsNullOrWhiteSpace(p.Username) ? "?" : p.Username.Substring(0, 1).ToUpperInvariant();
                sinceText.Text = p.RegisteredAt is { } r ? "На PlayerOK с " + r.ToLocalTime().ToString("d MMMM yyyy", Ru) : "";
                var url = p.AvatarUrl;
                if (!string.IsNullOrWhiteSpace(url) && url != avatarLoaded)
                {
                    if (url.StartsWith('/')) url = "https://playerok.com" + url;
                    var bytes = await Task.Run(() => _host.GetImageAsync(url, CancellationToken.None));
                    if (bytes is not null)
                    {
                        avatarImg.Source = Bitmap.DecodeToWidth(new MemoryStream(bytes), 256, BitmapInterpolationMode.MediumQuality);
                        avatar.Child = avatarImg;
                        avatarLoaded = p.AvatarUrl;
                    }
                }
            }
            catch { /* профиль вспомогательный: без сети остаются заглушки */ }
        }

        var refresh = new Button { Content = "↻ Обновить данные" };
        async Task ReloadAsync()
        {
            refresh.IsEnabled = false;
            try
            {
                if (_refreshMyProducts != null) await _refreshMyProducts();
            }
            catch (Exception ex) { hint.Text = "Ошибка обновления: " + ex.Message; }
            Refresh();
            _refreshAnalytics?.Invoke();
            await LoadProfileAsync();
            await RefreshBalanceAsync();
            refresh.IsEnabled = true;
        }
        refresh.Click += async (_, _) => await ReloadAsync();
        _onProfileShown = () => _ = ReloadAsync();

        // ----- правая колонка: баланс + саундтрек -----
        var profRefresh = new Button { Content = "↻", Width = 34, Height = 34, Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Center };
        profRefresh.Click += async (_, _) => await RefreshBalanceAsync();
        var balanceGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        balanceGrid.Children.Add(new StackPanel
        {
            Spacing = 3,
            Children = { new TextBlock { Text = "БАЛАНС PLAYEROK", FontSize = 10, FontWeight = FontWeight.Bold, Opacity = .65 }, _profBalance, _profBalanceMeta }
        });
        Grid.SetColumn(profRefresh, 1);
        balanceGrid.Children.Add(profRefresh);
        var balanceCard = new Border
        {
            Background = cardBg, BorderBrush = cardBorder, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14), Padding = new Thickness(14), Child = balanceGrid
        };

        var st = _sound.Settings;
        var musicOn = new CheckBox { Content = "🎵 Музыка на фоне", IsChecked = st.Music, FontWeight = FontWeight.SemiBold };
        var musicTheme = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = MusicThemeNames,
            SelectedIndex = Math.Max(0, Array.IndexOf(MusicThemeKeys, st.MusicTheme))
        };
        var musicNow = new TextBlock { Opacity = .75, FontSize = 11, TextWrapping = TextWrapping.Wrap };
        var syncing = false;
        void UpdateMusicNow()
        {
            syncing = true;
            try
            {
                musicOn.IsChecked = st.Music;
                var idx = Math.Max(0, Array.IndexOf(MusicThemeKeys, st.MusicTheme));
                if (musicTheme.SelectedIndex != idx) musicTheme.SelectedIndex = idx;
                var key = MusicKey();
                var title = GuiSettings.Presets.FirstOrDefault(x => x.Key == key)?.Title ?? key;
                var about = SoundService.MusicTitles.TryGetValue(key, out var d) ? d : "";
                musicNow.Text = st.Music ? $"Сейчас: {title} — {about}." : "Музыка выключена.";
            }
            finally { syncing = false; }
        }
        UpdateMusicNow();
        musicTheme.SelectionChanged += (_, _) =>
        {
            if (syncing || musicTheme.SelectedIndex < 0 || musicTheme.SelectedIndex >= MusicThemeKeys.Length) return;
            st.MusicTheme = MusicThemeKeys[musicTheme.SelectedIndex];
            st.Save();
            UpdateMusicTheme(force: true);
            UpdateMusicNow();
        };
        musicOn.IsCheckedChanged += (_, _) =>
        {
            if (syncing) return;
            st.Music = musicOn.IsChecked == true;
            st.Save();
            UpdateMusicTheme(force: true);
            UpdateMusicNow();
        };
        _refreshMusicLabel += UpdateMusicNow;

        var musicCard = new Border
        {
            Background = cardBg, BorderBrush = cardBorder, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14), Padding = new Thickness(14),
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = "САУНДТРЕК НА ФОН", FontSize = 10, FontWeight = FontWeight.Bold, Opacity = .65 },
                    musicOn, musicTheme, musicNow,
                    new TextBlock { Text = "Громкость и остальное — во вкладке «🎨 GUI».", FontSize = 11, Opacity = .55, TextWrapping = TextWrapping.Wrap },
                }
            }
        };
        var rightCol = new StackPanel { Spacing = 12, Margin = new Thickness(14, 0, 0, 0), Children = { balanceCard, musicCard } };

        // ----- левая колонка -----
        var metrics = new WrapPanel
        {
            Children =
            {
                MetricCard("Всего сделок", totalV),
                MetricCard("С возвратом", refundV),
                MetricCard("Успешных", successV),
                MetricCard("В процессе", progressV),
                MetricCard("Доход", incomeV),
            }
        };
        var left = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                header,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 10, 0, 0),
                    Children = { new TextBlock { Text = "Статистика", FontSize = 18, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center }, period, refresh }
                },
                metrics, incomeHint,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 12, 0, 4),
                    Children = { new TextBlock { Text = "Сделки", FontSize = 18, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center }, dealFilter }
                },
                dealsPanel, hint,
            }
        };

        var layout = new Grid { Margin = new Thickness(14), ColumnDefinitions = new ColumnDefinitions("*,290") };
        layout.Children.Add(left);
        Grid.SetColumn(rightCol, 1);
        layout.Children.Add(rightCol);

        Refresh();
        // Сверху — переключатель «Профиль | Аналитика»; аналитика считается за всё время.
        return BuildProfileShell(new ScrollViewer { Content = layout });
    }

    private static Control MetricCard(string title, TextBlock value) => new Border
    {
        Width = 150, Margin = new Thickness(0, 8, 8, 0), Padding = new Thickness(14),
        CornerRadius = new CornerRadius(12),
        Background = new SolidColorBrush(Color.Parse("#A61D2430")),
        Child = new StackPanel { Spacing = 5, Children = { new TextBlock { Text = title, Opacity = .7 }, value } }
    };

    // ---------- Быстрые ответы ----------

    private Control BuildQuickTab()
    {
        var list = new ListBox { MinWidth = 280 };
        var text = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 140 };

        void Refresh(int select)
        {
            list.ItemsSource = _host.Quick.Items.Select((t, i) => $"{i + 1}. {t.Replace("\n", " ")}").ToList();
            list.SelectedIndex = select < _host.Quick.Items.Count ? select : -1;
        }

        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedIndex >= 0 && list.SelectedIndex < _host.Quick.Items.Count)
                text.Text = _host.Quick.Items[list.SelectedIndex];
        };

        var save = new Button { Content = "💾 Сохранить изменения" };
        save.Click += (_, _) =>
        {
            var i = list.SelectedIndex;
            if (i < 0 || string.IsNullOrWhiteSpace(text.Text)) return;
            _host.Quick.Items[i] = text.Text!;
            _host.Quick.Save(); Refresh(i);
        };
        var add = new Button { Content = "➕ Добавить как новый" };
        add.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(text.Text)) return;
            _host.Quick.Items.Add(text.Text!);
            _host.Quick.Save(); Refresh(_host.Quick.Items.Count - 1);
        };
        var del = new Button { Content = "🗑 Удалить" };
        del.Click += (_, _) =>
        {
            var i = list.SelectedIndex;
            if (i < 0) return;
            _host.Quick.Items.RemoveAt(i);
            _host.Quick.Save(); text.Text = ""; Refresh(-1);
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(save); buttons.Children.Add(add); buttons.Children.Add(del);

        var right = Page(Caption("Эти ответы появляются кнопками под уведомлениями о сообщениях в Telegram."), text, buttons);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("300,*"), Margin = new Thickness(8) };
        var scroll = new ScrollViewer { Content = right };
        Grid.SetColumn(scroll, 1);
        grid.Children.Add(list);
        grid.Children.Add(scroll);
        Refresh(-1);
        return grid;
    }

    // ---------- Уведомления ----------

    private Control BuildNotifyTab()
    {
        var tabs = new TabControl { Margin = new Thickness(4) };

        // Telegram: настройки отправки уведомлений ботом.
        var telegramPanel = Page(Caption("Выберите, о каких событиях бот будет писать в Telegram. Автоответы покупателям не зависят от этих настроек."));
        var boxes = new Dictionary<string, CheckBox>();
        foreach (var (key, label) in NotifySettings.Categories)
        {
            var cb = new CheckBox { Content = label, IsChecked = _host.Notify.IsOn(key), Margin = new Thickness(2, 5) };
            cb.IsCheckedChanged += (_, _) =>
            {
                if ((cb.IsChecked == true) != _host.Notify.IsOn(key)) _host.Notify.Toggle(key);
            };
            boxes[key] = cb;
            telegramPanel.Children.Add(cb);
        }

        telegramPanel.Children.Add(Caption("Быстрые наборы"));
        var presets = new WrapPanel();
        foreach (var (key, (label, _)) in NotifySettings.Presets)
        {
            var b = new Button { Content = label, Margin = new Thickness(0, 0, 8, 8) };
            b.Click += (_, _) =>
            {
                _host.Notify.ApplyPreset(key);
                foreach (var (k, box) in boxes) box.IsChecked = _host.Notify.IsOn(k);
            };
            presets.Children.Add(b);
        }
        telegramPanel.Children.Add(presets);

        // In-app / Windows: локальные уведомления, звук и напоминания.
        var appPanel = Page(Caption("Уведомления внутри приложения и Windows. Эти параметры не влияют на сообщения Telegram."));
        var windowsNotifications = new CheckBox
        {
            Content = "Показывать системные уведомления Windows о новых сообщениях и сделках",
            IsChecked = WindowsNotificationOptions.Enabled,
            Margin = new Thickness(2, 5)
        };
        var testWindowsNotification = new Button { Content = "🔔 Проверить уведомление", Margin = new Thickness(0, 4, 0, 8) };
        var notificationInfo = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = .8 };
        windowsNotifications.IsCheckedChanged += (_, _) =>
        {
            try
            {
                WindowsNotificationOptions.Enabled = windowsNotifications.IsChecked == true;
                notificationInfo.Text = WindowsNotificationOptions.Enabled
                    ? "Системные уведомления Windows включены."
                    : "Системные уведомления Windows выключены.";
            }
            catch (Exception ex) { notificationInfo.Text = "Не удалось сохранить настройку: " + ex.Message; }
        };
        testWindowsNotification.Click += (_, _) =>
        {
            if (!OperatingSystem.IsWindows())
            {
                notificationInfo.Text = "Системные уведомления доступны только в Windows.";
                return;
            }
            var ok = _windowsNotifications.Show("PlayerOK Bridge — проверка", "Тестовое уведомление работает.", silent: true);
            _sound.Play(SoundKind.Notification, force: true);
            notificationInfo.Text = ok
                ? "✅ Тестовое уведомление отправлено, звук проигран."
                : "❌ Не удалось отправить уведомление. Проверьте разрешения Windows.";
        };
        appPanel.Children.Add(windowsNotifications);
        appPanel.Children.Add(testWindowsNotification);
        appPanel.Children.Add(notificationInfo);
        appPanel.Children.Add(BuildStaleDealSection());
        appPanel.Children.Add(BuildSoundSection());

        tabs.Items.Add(new TabItem
        {
            Header = "✈ Telegram",
            Content = new ScrollViewer { Content = telegramPanel, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto }
        });
        tabs.Items.Add(new TabItem
        {
            Header = "🔔 В приложении",
            Content = new ScrollViewer { Content = appPanel, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto }
        });
        return tabs;
    }

    /// <summary>Напоминание о «зависшей» сделке: оплачена, но товар не выдан (вкладка «Уведомления»).</summary>
    private Control BuildStaleDealSection()
    {
        var cfg = _host.Stale;
        var thresholds = new[] { 5, 10, 15, 20, 30, 45, 60, 120 };
        var repeats = new[] { 0, 15, 30, 60, 120 };
        var enabled = new CheckBox
        {
            Content = "⏰ Напоминать, если оплаченный заказ долго не выдан",
            IsChecked = cfg.Enabled, FontWeight = FontWeight.SemiBold,
        };
        var after = new ComboBox
        {
            MinWidth = 200,
            ItemsSource = thresholds.Select(m => m < 60 ? $"через {m} мин" : $"через {m / 60} ч").ToList(),
            SelectedIndex = Math.Max(0, Array.IndexOf(thresholds, cfg.Minutes)),
        };
        var repeat = new ComboBox
        {
            MinWidth = 200,
            ItemsSource = repeats.Select(m => m == 0 ? "не повторять" : m < 60 ? $"каждые {m} мин" : $"каждые {m / 60} ч").ToList(),
            SelectedIndex = Math.Max(0, Array.IndexOf(repeats, cfg.RepeatMinutes)),
        };
        var note = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = .8 };

        void Apply()
        {
            cfg.Enabled = enabled.IsChecked == true;
            if (after.SelectedIndex >= 0) cfg.Minutes = thresholds[after.SelectedIndex];
            if (repeat.SelectedIndex >= 0) cfg.RepeatMinutes = repeats[repeat.SelectedIndex];
            cfg.Save();
            note.Text = cfg.Enabled
                ? $"Включено: напомню через {cfg.Minutes} мин после оплаты" + (cfg.RepeatMinutes > 0 ? $", затем каждые {cfg.RepeatMinutes} мин." : " (один раз).")
                : "Напоминания выключены.";
        }
        enabled.IsCheckedChanged += (_, _) => Apply();
        after.SelectionChanged += (_, _) => Apply();
        repeat.SelectionChanged += (_, _) => Apply();
        note.Text = cfg.Enabled ? "Напоминание включено. Работает, пока приложение подключено к PlayerOK (можно в трее)." : "Напоминания выключены.";

        var section = new StackPanel { Spacing = 6 };
        section.Children.Add(Caption("Зависшие сделки"));
        section.Children.Add(enabled);
        section.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10,
            Children = { new TextBlock { Text = "Напомнить", VerticalAlignment = VerticalAlignment.Center }, after, repeat }
        });
        section.Children.Add(note);
        return section;
    }

    /// <summary>Звуки событий и фоновая музыка (вкладка «Уведомления»).</summary>
    private Control BuildSoundSection()
    {
        var st = _sound.Settings;
        var section = new StackPanel { Spacing = 8 };
        section.Children.Add(Caption("Звуки событий"));
        var info = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = .8 };
        if (!SoundService.Supported)
            info.Text = "Звуки и пользовательская музыка доступны только в Windows.";

        async Task<string?> PickAudioAsync(string title, string prefix)
        {
            var top = TopLevel.GetTopLevel(this);
            if (top is null) return null;
            var files = await top.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new Avalonia.Platform.Storage.FilePickerFileType("Аудиофайлы (MP3, WAV, WMA, MIDI)")
                    {
                        Patterns = new[] { "*.mp3", "*.wav", "*.wma", "*.mid", "*.midi" }
                    }
                }
            });
            if (files.Count == 0) return null;

            var extension = Path.GetExtension(files[0].Name).ToLowerInvariant();
            if (extension is not (".mp3" or ".wav" or ".wma" or ".mid" or ".midi"))
                throw new InvalidOperationException("Поддерживаются MP3, WAV, WMA и MIDI.");

            var folder = Path.Combine(JsonStore.DataDir, "audio");
            Directory.CreateDirectory(folder);
            var destination = Path.Combine(folder, $"{prefix}_{Guid.NewGuid():N}{extension}");
            var temporary = destination + ".tmp";
            try
            {
                await using (var source = await files[0].OpenReadAsync())
                {
                    await using var target = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None);
                    var buffer = new byte[80 * 1024];
                    long total = 0;
                    while (true)
                    {
                        var read = await source.ReadAsync(buffer.AsMemory());
                        if (read == 0) break;
                        total += read;
                        if (total > 100L * 1024 * 1024)
                            throw new InvalidOperationException("Размер аудиофайла не должен превышать 100 МБ.");
                        await target.WriteAsync(buffer.AsMemory(0, read));
                    }
                    await target.FlushAsync();
                    if (total == 0)
                        throw new InvalidOperationException("Выбранный аудиофайл пуст.");
                }
                File.Move(temporary, destination, overwrite: true);
                return destination;
            }
            catch
            {
                try { File.Delete(temporary); } catch { }
                throw;
            }
        }

        var enabled = new CheckBox
        {
            Content = "🔊 Звуки событий (покупки, сообщения, уведомления)",
            IsChecked = st.Enabled, FontWeight = FontWeight.SemiBold
        };
        var volumeLabel = new TextBlock { Opacity = .8 };
        var volume = new Slider { Minimum = 0, Maximum = 100, Width = 320, Value = Math.Clamp(st.Volume, 0, 100) };
        void UpdateVolumeLabel() => volumeLabel.Text = $"Громкость звуков: {(int)Math.Round(volume.Value)}%";
        UpdateVolumeLabel();

        string? GetSoundPath(SoundKind kind) => kind switch
        {
            SoundKind.Purchase => st.PurchaseSoundPath,
            SoundKind.Message => st.MessageSoundPath,
            _ => st.NotificationSoundPath,
        };
        void SetSoundPath(SoundKind kind, string? path)
        {
            switch (kind)
            {
                case SoundKind.Purchase: st.PurchaseSoundPath = path; break;
                case SoundKind.Message: st.MessageSoundPath = path; break;
                default: st.NotificationSoundPath = path; break;
            }
            st.Save();
        }

        Control Row(string text, bool isOn, Action<bool> set, SoundKind kind, string testText)
        {
            var cb = new CheckBox { Content = text, IsChecked = isOn, MinWidth = 290, VerticalAlignment = VerticalAlignment.Center };
            cb.IsCheckedChanged += (_, _) => { set(cb.IsChecked == true); st.Save(); };
            var test = new Button
            {
                Content = testText, Padding = new Thickness(10, 5), MinWidth = 210,
                HorizontalContentAlignment = HorizontalAlignment.Center
            };
            test.Click += (_, _) =>
            {
                var played = _sound.Play(kind, force: true);
                info.Text = played ? "▶ Тестовый звук запущен." : SoundService.Supported ? "Проверьте громкость звука." : "Звук доступен только в Windows.";
            };
            var choose = new Button
            {
                Content = "🎵 Файл…", Padding = new Thickness(10, 5),
                Margin = new Thickness(22, 0, 0, 0)
            };
            var clear = new Button { Content = "Сброс", Padding = new Thickness(9, 5), IsVisible = !string.IsNullOrWhiteSpace(GetSoundPath(kind)) };
            var customLabel = new TextBlock { FontSize = 11, Opacity = .6, TextWrapping = TextWrapping.Wrap };
            void PaintCustom()
            {
                var path = GetSoundPath(kind);
                clear.IsVisible = !string.IsNullOrWhiteSpace(path);
                customLabel.Text = !string.IsNullOrWhiteSpace(path) && File.Exists(path)
                    ? "Свой звук: " + Path.GetFileName(path)
                    : "Используется встроенный звук";
            }
            PaintCustom();
            choose.Click += async (_, _) =>
            {
                try
                {
                    var path = await PickAudioAsync("Выберите звук события", "event_sound");
                    if (path is null) return;
                    SetSoundPath(kind, path);
                    PaintCustom();
                    info.Text = "✅ Пользовательский звук сохранён.";
                }
                catch (Exception ex) { info.Text = "Не удалось загрузить звук: " + ex.Message; }
            };
            clear.Click += (_, _) =>
            {
                SetSoundPath(kind, null);
                PaintCustom();
                info.Text = "Встроенный звук восстановлен.";
            };
            return new StackPanel
            {
                Spacing = 3,
                Children =
                {
                    new WrapPanel { Orientation = Orientation.Horizontal, Children = { cb, test, choose, clear } },
                    customLabel
                }
            };
        }

        enabled.IsCheckedChanged += (_, _) => { st.Enabled = enabled.IsChecked == true; st.Save(); };
        volume.PropertyChanged += (_, e) =>
        {
            if (e.Property != Avalonia.Controls.Primitives.RangeBase.ValueProperty) return;
            st.Volume = (int)Math.Round(volume.Value);
            UpdateVolumeLabel();
        };
        volume.PointerCaptureLost += (_, _) => st.Save();

        section.Children.Add(enabled);
        section.Children.Add(Row("🛒 Новая покупка", st.Purchase, v => st.Purchase = v, SoundKind.Purchase, "▶ Протестировать уведомление"));
        section.Children.Add(Row("💬 Новое сообщение", st.Message, v => st.Message = v, SoundKind.Message, "▶ Протестировать уведомление"));
        section.Children.Add(Row("🔔 Системное уведомление", st.Notification, v => st.Notification = v, SoundKind.Notification, "▶ Протестировать уведомление"));
        section.Children.Add(volumeLabel);
        section.Children.Add(volume);

        section.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromArgb(0x28, 255, 255, 255)), Margin = new Thickness(0, 7) });
        section.Children.Add(Caption("Фоновая музыка"));
        var music = new CheckBox { Content = "🎵 Включить музыку", IsChecked = st.Music, FontWeight = FontWeight.SemiBold };
        var musicLabel = new TextBlock { Opacity = .8 };
        var musicVolume = new Slider { Minimum = 0, Maximum = 100, Width = 320, Value = Math.Clamp(st.MusicVolume, 0, 100) };
        var musicNow = new TextBlock { Opacity = .8, TextWrapping = TextWrapping.Wrap };
        var customMusicInfo = new TextBlock { Opacity = .65, FontSize = 11, TextWrapping = TextWrapping.Wrap };
        var musicTheme = new ComboBox
        {
            MinWidth = 320,
            ItemsSource = new[]
            {
                "Автоматически под фон", "Лава-лампа / Ambient", "Переливание / Arpeggio",
                "Закат / Lounge", "Огонь / Dark drive", "Снег / Music box", "Пузыри / Chill",
                "Аврора / Ethereal", "Звёзды / Space", "Минимализм", "Neon synthwave", "Rainy lo-fi", "Dream ambient"
            },
            SelectedIndex = st.MusicTheme switch { "lava" => 1, "flow" => 2, "sunset" => 3, "fire" => 4, "snow" => 5, "bubbles" => 6, "aurora" => 7, "stars" => 8, "solid" => 9, "neon" => 10, "rain" => 11, "dream" => 12, _ => 0 }
        };
        void UpdateMusicLabels()
        {
            musicLabel.Text = $"Громкость музыки: {(int)Math.Round(musicVolume.Value)}%";
            var custom = !string.IsNullOrWhiteSpace(st.CustomMusicPath) && File.Exists(st.CustomMusicPath);
            customMusicInfo.Text = custom
                ? "Ваша музыка: " + Path.GetFileName(st.CustomMusicPath)
                : "Своя музыка не выбрана. Можно загрузить MP3, WAV, WMA или MIDI (до 100 МБ).";
            var key = MusicKey();
            var title = GuiSettings.Presets.FirstOrDefault(x => x.Key == key)?.Title ?? key;
            var about = SoundService.MusicTitles.TryGetValue(key, out var d) ? d : "";
            musicNow.Text = !st.Music ? "Музыка выключена."
                : custom ? "Сейчас играет ваш трек."
                : $"Сейчас: {title} — {about}.";
        }
        UpdateMusicLabels();

        musicTheme.SelectionChanged += (_, _) =>
        {
            if (musicTheme.SelectedIndex < 0 || musicTheme.SelectedIndex >= MusicThemeKeys.Length) return;
            st.MusicTheme = MusicThemeKeys[musicTheme.SelectedIndex];
            st.Save();
            UpdateMusicTheme(force: true);
            UpdateMusicLabels();
        };
        music.IsCheckedChanged += (_, _) =>
        {
            st.Music = music.IsChecked == true;
            st.Save();
            UpdateMusicLabels();
            UpdateMusicTheme(force: true);
        };
        var musicApply = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        musicApply.Tick += (_, _) => { musicApply.Stop(); st.Save(); _sound.ApplyMusic(); };
        musicVolume.PropertyChanged += (_, e) =>
        {
            if (e.Property != Avalonia.Controls.Primitives.RangeBase.ValueProperty) return;
            st.MusicVolume = (int)Math.Round(musicVolume.Value);
            UpdateMusicLabels();
            musicApply.Stop(); musicApply.Start();
        };
        _refreshMusicLabel += UpdateMusicLabels;

        var chooseMusic = new Button { Content = "📂 Загрузить свою музыку" };
        chooseMusic.Click += async (_, _) =>
        {
            try
            {
                var path = await PickAudioAsync("Выберите фоновую музыку", "background_music");
                if (path is null) return;
                st.CustomMusicPath = path;
                st.Save();
                UpdateMusicLabels();
                _sound.ApplyMusic();
                info.Text = "✅ Своя музыка загружена и выбрана.";
            }
            catch (Exception ex) { info.Text = "Не удалось загрузить музыку: " + ex.Message; }
        };
        var clearMusic = new Button { Content = "Использовать музыку темы" };
        clearMusic.Click += (_, _) =>
        {
            st.CustomMusicPath = null;
            st.Save();
            UpdateMusicLabels();
            _sound.ApplyMusic();
            info.Text = "Восстановлена музыка выбранной темы GUI.";
        };

        section.Children.Add(music);
        section.Children.Add(Caption("Музыкальная тема (автоматически под фон или вручную)"));
        section.Children.Add(musicTheme);
        section.Children.Add(musicNow);
        section.Children.Add(musicLabel);
        section.Children.Add(musicVolume);
        section.Children.Add(new WrapPanel { Orientation = Orientation.Horizontal, Children = { chooseMusic, clearMusic } });
        section.Children.Add(customMusicInfo);
        section.Children.Add(info);
        section.Children.Add(Caption("Загруженный трек сохраняется в папке данных приложения и будет доступен после перезапуска."));
        return section;
    }

    // ---------- Просьба об отзыве (внутри вкладки автоответов) ----------

    private Control BuildReviewCard()
    {
        var enabled = new CheckBox
        {
            Content = "⭐ Просить отзыв после подтверждения сделки",
            IsChecked = _host.Review.Enabled,
            FontWeight = FontWeight.SemiBold,
        };
        var text = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 105, Text = _host.Review.Text };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = .8 };
        var save = new Button { Content = "💾 Сохранить просьбу об отзыве" };
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(text.Text))
            {
                status.Text = "❌ Текст просьбы не должен быть пустым.";
                return;
            }
            _host.Review.Enabled = enabled.IsChecked == true;
            _host.Review.Text = text.Text!;
            try
            {
                _host.Review.Save();
                status.Text = "✅ Настройки просьбы об отзыве сохранены.";
            }
            catch (Exception ex) { status.Text = "❌ " + ex.Message; }
        };

        return new Border
        {
            Background = new SolidColorBrush(Color.Parse("#A61A1D26")),
            CornerRadius = new CornerRadius(14), Padding = new Thickness(14),
            Margin = new Thickness(0, 18, 0, 0),
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = "Просьба об отзыве", FontSize = 18, FontWeight = FontWeight.SemiBold },
                    Caption("Сообщение отправляется после подтверждения сделки. Подстановки: {buyer} {seller} {item} {price} {deal_id}."),
                    enabled, text, save, status,
                }
            }
        };
    }

    private DateTime? _lastDay;
    private static readonly System.Globalization.CultureInfo Ru = new("ru-RU");

    private void RenderMessages(StackPanel panel, IReadOnlyList<ChatMessage> rows)
    {
        panel.Children.Clear();
        _lastDay = null;
        foreach (var m in rows) AddMessageBubble(panel, m);
    }

    /// <summary>Убирает из текста символы, на которых падает отрисовка (управляющие, одиночные суррогаты, U+FFFE/FFFF).</summary>
    internal static string SafeText(string? s)
    {
        if (string.IsNullOrEmpty(s)) return s ?? "";
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var ch = s[i];
            if (char.IsHighSurrogate(ch) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { sb.Append(ch).Append(s[++i]); continue; }
            if (char.IsSurrogate(ch)) continue;
            if (ch == '\n' || ch == '\t') { sb.Append(ch); continue; }
            if (ch == '\r') continue;
            if (char.IsControl(ch) || ch == '\uFFFE' || ch == '\uFFFF') continue;
            sb.Append(ch);
        }
        return sb.ToString();
    }

    private void AddMessageBubble(StackPanel panel, ChatMessage m)
    {
        var before = panel.Children.Count;
        try { AddMessageBubbleCore(panel, m); }
        catch (Exception ex)
        {
            Program.LogCrash("AddMessageBubble " + m.Id, ex);
            while (panel.Children.Count > before) panel.Children.RemoveAt(panel.Children.Count - 1);
            panel.Children.Add(new TextBlock { Text = "⚠️ Сообщение не удалось отобразить", Opacity = .6, FontSize = 12 });
        }
    }

    private void AddMessageBubbleCore(StackPanel panel, ChatMessage m)
    {
        var system = EventFormatter.IsSystemMarker(m.Text);
        // Служебные сообщения PlayerOK ({{...}}) показываются карточками событий; их можно выключить во вкладке GUI.
        if (system && !_gui.ShowDealCards) return;

        var when = m.CreatedAt?.ToLocalTime();
        if (when is { } w && _lastDay != w.Date)
        {
            _lastDay = w.Date;
            panel.Children.Add(new TextBlock
            {
                Text = w.ToString("d MMMM", Ru), HorizontalAlignment = HorizontalAlignment.Center,
                Opacity = .65, FontSize = 12, Margin = new Thickness(0, 8, 0, 0)
            });
        }
        if (system) { panel.Children.Add(BuildEventCard(m, when)); return; }

        var hasText = !string.IsNullOrWhiteSpace(m.Text);
        var hasImages = m.ImageUrls is { Count: > 0 };
        // Убираем случайные пустые строки в начале текста.
        var body = hasText ? SafeText(EventFormatter.ReadableSystemMessage(m.Text!.TrimStart('\r', '\n', ' ', '\t').TrimEnd())) : "[Вложение]";
        var own = !string.IsNullOrEmpty(_host.UiUserId) && m.AuthorId == _host.UiUserId;
        var header = SafeText($"{(own ? "Вы" : m.AuthorName ?? (m.AuthorRole == "SUPPORT" ? "Поддержка" : "Покупатель"))}  ·  {when?.ToString("dd.MM HH:mm") ?? ""}");

        var content = new StackPanel { Spacing = 5 };
        content.Children.Add(new TextBlock { Text = header, FontSize = 11, Opacity = .72, TextWrapping = TextWrapping.Wrap });
        if (hasText || !hasImages)
            content.Children.Add(new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, FontSize = 14 });
        if (hasImages)
            foreach (var url in m.ImageUrls!) content.Children.Add(BuildImage(url, 300, 300, false));
        if (m.PurchasedItemName is { Length: > 0 } itemName)
            content.Children.Add(new TextBlock { Text = SafeText("🛒 Куплен товар: " + itemName), FontSize = 12, FontWeight = FontWeight.SemiBold, Opacity = .9, TextWrapping = TextWrapping.Wrap });

        var bubble = new Border
        {
            Background = new SolidColorBrush(Color.Parse(own ? _gui.OwnBubble : _gui.BuyerBubble)),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 8),
            MaxWidth = 620,
            Child = content
        };
        var line = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        bubble.HorizontalAlignment = own ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        Grid.SetColumn(bubble, own ? 1 : 0);
        line.Children.Add(bubble);
        panel.Children.Add(line);
    }

    /// <summary>Картинка из сообщения/сделки: грузится в фоне, по клику открывается в браузере.</summary>
    private Control BuildImage(string url, double width, double height, bool crop)
    {
        var img = new Image { Stretch = crop ? Stretch.UniformToFill : Stretch.Uniform };
        var holder = new Border
        {
            CornerRadius = new CornerRadius(12), ClipToBounds = true,
            MaxWidth = width, MaxHeight = height,
            HorizontalAlignment = HorizontalAlignment.Left,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            Child = new TextBlock { Text = "🖼 Загрузка…", Opacity = .7, Margin = new Thickness(8) }
        };
        if (crop) { holder.Width = width; holder.Height = height; }
        holder.PointerReleased += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        };
        _ = LoadImageAsync(holder, img, url);
        return holder;
    }

    // Кэш уже загруженных картинок: чат перерисовывается часто, и повторно качать/декодировать фото не нужно.
    private readonly Dictionary<string, Bitmap> _imageCache = new();
    private readonly Queue<string> _imageOrder = new();
    private const int ImageCacheLimit = 80;

    private async Task LoadImageAsync(Border holder, Image img, string url)
    {
        if (_imageCache.TryGetValue(url, out var cached))
        {
            img.Source = cached;
            holder.Child = img;
            return;
        }
        byte[]? bytes = null;
        try { bytes = await Task.Run(() => _host.GetImageAsync(url, CancellationToken.None)); }
        catch { /* покажем заглушку ниже */ }
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (bytes is null) throw new InvalidOperationException();
                // Декодируем уменьшенную копию — полноразмерные фото сильно раздувают память.
                var bmp = Bitmap.DecodeToWidth(new MemoryStream(bytes), 1000, BitmapInterpolationMode.MediumQuality);
                if (_imageCache.TryAdd(url, bmp))
                {
                    _imageOrder.Enqueue(url);
                    while (_imageOrder.Count > ImageCacheLimit) _imageCache.Remove(_imageOrder.Dequeue());
                }
                img.Source = bmp;
                holder.Child = img;
            }
            catch
            {
                holder.Child = new TextBlock { Text = "🖼 Фото (нажмите, чтобы открыть)", Opacity = .7, Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap };
            }
        });
    }

    private static TextBlock Stars(int rating, double size) => new()
    {
        Text = new string('★', Math.Clamp(rating, 0, 5)) + new string('☆', 5 - Math.Clamp(rating, 0, 5)),
        Foreground = new SolidColorBrush(Color.Parse("#4D83FF")), FontSize = size
    };

    /// <summary>Карточка события сделки (оплата, подтверждение, отзыв) как на сайте PlayerOK.</summary>
    private Control BuildEventCard(ChatMessage m, DateTimeOffset? when)
    {
        var code = Regex.Match(m.Text ?? "", @"\{\{?\s*([A-Za-z_]+)\s*\}?\}").Groups[1].Value.ToUpperInvariant();
        var who = SafeText(m.DealUserName ?? m.AuthorName ?? "Покупатель");
        string title; string? subtitle = null; var showReview = false; var showItem = false;
        switch (code)
        {
            case "ITEM_PAID" or "NEW_DEAL" or "DEAL_PAID":
                title = $"{who} оплатил покупку";
                subtitle = "Вы получите оплату после того, как передадите товар";
                showItem = true; break;
            case "DEAL_CONFIRMED" or "DEAL_CONFIRMED_BY_BUYER":
                title = $"{who} подтвердил получение";
                subtitle = "Оплата зачислена на ваш баланс и доступна к выплате";
                showReview = m.Rating is not null; showItem = !showReview; break;
            case "NEW_REVIEW":
                title = $"{who} оставил отзыв"; showReview = m.Rating is not null; break;
            case "DEAL_HAS_PROBLEM":
                title = $"{who} сообщил о проблеме"; showItem = true; break;
            default:
                title = SafeText(EventFormatter.ReadableSystemMessage(m.Text ?? "")); break;
        }

        var panel = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4) };
        if (when is { } t)
            panel.Children.Add(new TextBlock { Text = t.ToString("H:mm"), Opacity = .6, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center });
        panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center });
        if (subtitle is not null)
            panel.Children.Add(new TextBlock { Text = subtitle, Opacity = .7, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center });

        var pink = new SolidColorBrush(Color.Parse("#FF4D6D"));
        var cardBg = new SolidColorBrush(Color.Parse("#B02A2D37"));

        if (showReview && m.Rating is { } rating)
        {
            var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            var avatar = new Border
            {
                Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Margin = new Thickness(0, 0, 10, 0),
                Background = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)),
                Child = new TextBlock { Text = "👤", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Black }
            };
            var name = new TextBlock { Text = m.ReviewerName ?? who, Foreground = pink, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center };
            var date = new TextBlock { Text = (m.ReviewAt?.ToLocalTime() ?? when)?.ToString("d MMMM в HH:mm", Ru) ?? "", Opacity = .65, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(name, 1); Grid.SetColumn(date, 2);
            head.Children.Add(avatar); head.Children.Add(name); head.Children.Add(date);
            var stars = Stars(rating, 30); stars.HorizontalAlignment = HorizontalAlignment.Center; stars.Margin = new Thickness(0, 10, 0, 4);
            var inner = new StackPanel { Spacing = 4, Children = { head, stars } };
            if (!string.IsNullOrWhiteSpace(m.ReviewText))
                inner.Children.Add(new TextBlock { Text = SafeText(m.ReviewText), TextWrapping = TextWrapping.Wrap, Opacity = .9 });
            panel.Children.Add(new Border { Background = cardBg, CornerRadius = new CornerRadius(14), Padding = new Thickness(16), Width = 420, Margin = new Thickness(0, 6, 0, 0), Child = inner });
        }
        else if (showItem && (m.PurchasedItemName is { Length: > 0 } || m.ItemImageUrl is not null))
        {
            var info = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
            if (m.ItemPrice is { } price)
                info.Children.Add(new TextBlock { Text = $"{price:0.##} ₽", FontWeight = FontWeight.Bold, FontSize = 16 });
            if (!string.IsNullOrEmpty(m.PurchasedItemName))
                info.Children.Add(new TextBlock { Text = SafeText(m.PurchasedItemName), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 280 });
            if (!string.IsNullOrEmpty(m.SellerName))
                info.Children.Add(new TextBlock { Text = "👤 " + m.SellerName, Foreground = pink, FontWeight = FontWeight.Bold });
            if (m.Rating is { } r) info.Children.Add(Stars(r, 14));
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            if (m.ItemImageUrl is { Length: > 0 } imgUrl) row.Children.Add(BuildImage(imgUrl, 120, 84, true));
            row.Children.Add(info);
            panel.Children.Add(new Border { Background = cardBg, CornerRadius = new CornerRadius(14), Padding = new Thickness(10), Margin = new Thickness(0, 6, 0, 0), Child = row });
        }
        return panel;
    }

    /// <summary>Снимок элемента в bitmap (для анимации перехода). Размер — в пикселях экрана, без размытия на HiDPI.</summary>
    private static RenderTargetBitmap? Snapshot(Control c)
    {
        double w = c.Bounds.Width, h = c.Bounds.Height;
        if (w < 2 || h < 2) return null;
        // Ограничиваем разрешение снимка: на 2x/3x HiDPI full-size bitmap сильно
        // нагружает UI при двух последовательных захватах, хотя переход занимает доли секунды.
        var renderScale = TopLevel.GetTopLevel(c)?.RenderScaling ?? 1.0;
        var scale = Math.Clamp(renderScale, 0.75, 1.25);
        var bmp = new RenderTargetBitmap(
            new PixelSize((int)Math.Ceiling(w * scale), (int)Math.Ceiling(h * scale)),
            new Vector(96 * scale, 96 * scale));
        bmp.Render(c);
        return bmp;
    }

    /// <summary>Цвета полосы перехода: свои или из палитры фона.</summary>
    private (Color, Color) TransitionColors(TransitionSettings t)
    {
        static Color P(string? text, string fallback) => Color.TryParse(text, out var x) ? x : Color.Parse(fallback);
        if (t.UseBackgroundColors && _gui.Colors.Count > 0)
            return (P(_gui.Colors[0], "#FF5E62"), P(_gui.Colors[^1], "#7B2FBE"));
        return (P(t.Color1, "#FF5E62"), P(t.Color2, "#7B2FBE"));
    }

    /// <summary>Оплаченные заказы, которые слишком долго не выданы: уведомление Windows, звук и мигание значка в трее.</summary>
    private void OnStaleDeals(IReadOnlyList<StaleDealInfo> list)
    {
        if (list.Count == 0) return;
        string One(StaleDealInfo x) =>
            $"{x.Deal.ItemName ?? "Заказ"} · {x.Deal.Price:0.##} ₽ · {x.Deal.BuyerName} · {AppHost.FormatAge(x.Age)}";
        var title = list.Count == 1 ? "⏰ Заказ не выдан" : $"⏰ Не выдано заказов: {list.Count}";
        var body = list.Count == 1
            ? One(list[0])
            : string.Join("\n", list.Take(3).Select(One)) + (list.Count > 3 ? $"\n…и ещё {list.Count - 3}" : "");
        ShowToast(title, body);
        _sound.Play(SoundKind.Notification);
        TrayAlert(title + ": " + One(list[0]));
    }

    /// <summary>Системное уведомление Windows. Если звуки приложения включены, системный сигнал отключаем, чтобы не было двойного звука.</summary>
    private void ShowToast(string title, string message)
    {
        if (!WindowsNotificationOptions.Enabled) return;
        _windowsNotifications.Show(title, message, silent: _sound.Settings.Enabled);
    }

    /// <summary>Ключ музыкальной темы под текущий фон.</summary>
    private string MusicKey()
    {
        if (!string.IsNullOrWhiteSpace(_sound.Settings.MusicTheme) && _sound.Settings.MusicTheme != "auto")
            return SoundService.MusicTitles.ContainsKey(_sound.Settings.MusicTheme) ? _sound.Settings.MusicTheme : "lava";
        var p = GuiSettings.Presets.FirstOrDefault(x => x.Key == _gui.Preset);
        if (p is not null && p.Mode == _gui.Mode) return p.Key;
        return _gui.Mode switch
        {
            BgMode.Flow => "flow",
            BgMode.Lava => "lava",
            BgMode.Snow => "snow",
            BgMode.Bubbles => "bubbles",
            BgMode.Aurora => "aurora",
            BgMode.Stars => "stars",
            _ => "solid",
        };
    }

    private void UpdateMusicTheme(bool force = false)
    {
        if (!_musicReady) return;
        var key = MusicKey();
        if (!force && key == _lastMusicKey) return;
        _lastMusicKey = key;
        _sound.SetTheme(key);
        _refreshMusicLabel?.Invoke();
    }

    private void ApplyGui()
    {
        UpdateMusicTheme();
        var colors = _gui.Colors.Select(c => Color.TryParse(c, out var x) ? (Color?)x : null)
            .Where(c => c is not null).Select(c => c!.Value).ToArray();
        _bg.Configure(_gui.Mode, colors, _gui.Speed, _gui.Density, _gui.Animate);
        var glass = new SolidColorBrush(Color.FromArgb((byte)(Math.Clamp(_gui.PanelOpacity, 0, 1) * 255), 0x10, 0x12, 0x18));
        var radius = new CornerRadius(Math.Clamp(_gui.CornerRadius, 0, 40));
        foreach (var g in _glass) { g.Background = glass; g.CornerRadius = radius; }
        var small = new CornerRadius(Math.Max(4, Math.Clamp(_gui.CornerRadius, 0, 40) * 0.8));
        foreach (var b in _navButtons) b.CornerRadius = small;
        var inner = new CornerRadius(Math.Max(6, Math.Clamp(_gui.CornerRadius, 0, 40) * 0.65));
        // Скругление всех элементов внутри страниц (чаты, автоответы, поля ввода и т.д.).
        foreach (var st in _radiusStyles) Styles.Remove(st);
        _radiusStyles.Clear();
        void Round<T>() where T : Avalonia.Controls.Primitives.TemplatedControl
        {
            var style = new Style(x => x.OfType<T>());
            style.Setters.Add(new Setter(Avalonia.Controls.Primitives.TemplatedControl.CornerRadiusProperty, inner));
            _radiusStyles.Add(style);
            Styles.Add(style);
        }
        Round<TextBox>(); Round<ComboBox>(); Round<ListBox>(); Round<ListBoxItem>(); Round<Button>();
    }

    // ---------- Настройки ----------

    private Control BuildSettingsTab()
    {
        var s = _host.Settings;
        var tg = new TextBox { Text = s.TgBotToken, PasswordChar = '•' };
        var admin = new TextBox { Text = s.AdminId == 0 ? "" : s.AdminId.ToString() };
        var token = new TextBox { Text = s.PlayerokToken, PasswordChar = '•' };
        var ddg = new TextBox { Text = s.PlayerokDdg3, PasswordChar = '•' };
        var ua = new TextBox { Text = s.UserAgent };
        var proxy = new TextBox { Text = s.TgProxy, Watermark = "http://user:pass@host:port (необязательно)" };
        var tz = new TextBox { Text = s.TzOffsetHours.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        var info = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var save = new Button { Content = "💾 Сохранить настройки" };

        // ----- Внешний вид и язык: применяется сразу, без кнопки «Сохранить» -----
        var appearanceInfo = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = .7, FontSize = 12 };
        var langBox = new ComboBox { MinWidth = 280, ItemsSource = new[] { "Русский", "English" }, SelectedIndex = Loc.IsEn ? 1 : 0 };
        var fontNames = UiFont.Available();
        var fontBox = new ComboBox
        {
            MinWidth = 280, MaxDropDownHeight = 360,
            // Названия пунктов не закрепляем за собственным шрифтом: иначе
            // надпись выбранного ранее шрифта выглядит неизменной при переключении.
            ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<string>(
                (name, _) => new TextBlock { Text = name }, true),
        };
        var fillingFonts = false;
        void FillFonts()
        {
            fillingFonts = true;
            var items = new List<string> { Loc.T("Системный (по умолчанию)", "System (default)") };
            items.AddRange(fontNames);
            fontBox.ItemsSource = items;
            var idx = string.IsNullOrWhiteSpace(_host.Settings.UiFont)
                ? 0
                : items.FindIndex(1, x => string.Equals(x, _host.Settings.UiFont, StringComparison.OrdinalIgnoreCase));
            fontBox.SelectedIndex = idx < 0 ? 0 : idx;
            fillingFonts = false;
        }
        FillFonts();
        langBox.SelectionChanged += (_, _) =>
        {
            if (fillingFonts) return;
            var code = langBox.SelectedIndex == 1 ? Loc.EnCode : Loc.Ru;
            if (code == Loc.Language) return;
            _host.Settings.Language = code;
            try { _host.Settings.Save(); }
            catch (Exception ex) { appearanceInfo.Text = Loc.T("❌ Не удалось сохранить: ", "❌ Failed to save: ") + ex.Message; }
            Loc.Set(code);
            FillFonts(); // подпись «Системный (по умолчанию)» зависит от языка
        };
        fontBox.SelectionChanged += (_, _) =>
        {
            if (fillingFonts || fontBox.SelectedIndex < 0) return;
            var name = fontBox.SelectedIndex == 0 ? "" : fontBox.SelectedItem as string ?? "";
            if (string.Equals(name, _host.Settings.UiFont, StringComparison.Ordinal)) return;
            _host.Settings.UiFont = name;
            try { _host.Settings.Save(); }
            catch (Exception ex) { appearanceInfo.Text = Loc.T("❌ Не удалось сохранить: ", "❌ Failed to save: ") + ex.Message; }
            UiFont.Apply(name);
            appearanceInfo.Text = string.IsNullOrWhiteSpace(name)
                ? "Текущий шрифт: системный. Настройка сохранена автоматически."
                : $"Текущий шрифт: {name}. Настройка сохранена автоматически.";
        };
        appearanceInfo.Text = string.IsNullOrWhiteSpace(_host.Settings.UiFont)
            ? "Текущий шрифт: системный. Выбор применяется сразу."
            : $"Текущий шрифт: {_host.Settings.UiFont}. Выбор применяется сразу.";

        var logout = new Button { Content = "Выйти из аккаунта Playerok", Margin = new Thickness(0, 6, 0, 0) };
        logout.Click += (_, _) =>
        {
            try
            {
                _host.DisconnectApp();
                var cfg = _host.Settings;
                cfg.PlayerokToken = ""; cfg.PlayerokDdg3 = "";
                cfg.Save();
                token.Text = ""; ddg.Text = "";
                info.Text = "Токен удалён. Перезапустите приложение, чтобы войти заново.";
            }
            catch (Exception ex) { info.Text = ex.Message; }
        };
        save.Click += (_, _) =>
        {
            if (!long.TryParse(admin.Text?.Trim(), out var adminId)) { info.Text = "❌ ADMIN_ID должен быть числом."; return; }
            if (!double.TryParse(tz.Text?.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var tzHours)) { info.Text = "❌ Часовой пояс — число, например 3."; return; }

            var cfg = _host.Settings;
            cfg.TgBotToken = tg.Text?.Trim() ?? ""; cfg.AdminId = adminId;
            cfg.PlayerokToken = token.Text?.Trim() ?? ""; cfg.PlayerokDdg3 = ddg.Text?.Trim() ?? "";
            cfg.UserAgent = string.IsNullOrWhiteSpace(ua.Text) ? cfg.UserAgent : ua.Text!.Trim();
            cfg.TgProxy = proxy.Text?.Trim() ?? ""; cfg.TzOffsetHours = tzHours;
            cfg.Save();
            info.Text = $"✅ Сохранено в {JsonStore.PathOf(AppSettings.FileName)}. Перезапустите работу, чтобы применить.";
        };

        return new ScrollViewer
        {
            Content = Page(
                new TextBlock { Text = "Внешний вид и язык", FontSize = 17, FontWeight = FontWeight.SemiBold },
                Caption("Язык интерфейса"), langBox,
                Caption("Шрифт интерфейса"), fontBox,
                appearanceInfo,
                new TextBlock { Text = "Подключение и аккаунт", FontSize = 17, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 18, 0, 0) },
                Caption("Токен Telegram-бота (от @BotFather)"), tg,
                Caption("Ваш Telegram ID (число)"), admin,
                Caption("Токен PlayerOK (cookie token)"), token,
                Caption("Cookie __ddg3 / __ddg5 (если PlayerOK просит проверку)"), ddg,
                Caption("User-Agent браузера, из которого взяты cookie"), ua,
                Caption("Прокси для Telegram (необязательно)"), proxy,
                Caption("Часовой пояс для статистики (3 = Москва)"), tz,
                save, logout, info),
        };
    }

    // ---------- GUI ----------

    /// <summary>Вкладка «Переходы»: все параметры анимации смены страниц. Возвращает панель и метод перезагрузки значений.</summary>
    private (Control Panel, Action Reload) BuildTransitionPanel()
    {
        var loading = true;
        TransitionSettings T() => _gui.Transition;

        var noAnimation = new CheckBox { Content = "Без анимации — мгновенное переключение вкладок" };
        var preview = new Button { Content = "▶ Предпросмотр перехода", Padding = new Thickness(14, 8) };
        var kindBox = new ComboBox { ItemsSource = TransitionSettings.KindTitles, MinWidth = 280 };
        var dirBox = new ComboBox { ItemsSource = TransitionSettings.DirectionTitles, MinWidth = 280 };
        var easeBox = new ComboBox { ItemsSource = TransitionSettings.EaseTitles, MinWidth = 280 };
        var useBg = new CheckBox { Content = "Брать цвета полосы из цветов фона" };
        var parallax = new CheckBox { Content = "Параллакс (старая страница уезжает медленнее, новая подъезжает с запасом)" };
        var glow = new CheckBox { Content = "Свечение вокруг полосы" };
        var pauseBg = new CheckBox { Content = "⏸ Останавливать анимацию фона на время перехода (выключено — фон движется и во время перехода)" };

        TextBox ColorBox() => new() { Width = 160, Watermark = "#RRGGBB" };
        Border Swatch() => new()
        {
            Width = 34, Height = 34, CornerRadius = new CornerRadius(10),
            BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), BorderThickness = new Thickness(1)
        };
        var color1 = ColorBox(); var color2 = ColorBox();
        var sw1 = Swatch(); var sw2 = Swatch();

        Slider Sl(double min, double max) => new() { Minimum = min, Maximum = max, Width = 320 };
        var duration = Sl(100, 2500);
        var band = Sl(0, 40);
        var bandOpacity = Sl(0, 1);
        var angle = Sl(0, 60);
        var parts = Sl(2, 40);
        var strength = Sl(0, 100);
        var durationLabel = Caption(""); var bandLabel = Caption(""); var bandOpacityLabel = Caption("");
        var angleLabel = Caption(""); var partsLabel = Caption(""); var strengthLabel = Caption("");

        void UpdateLabels()
        {
            var t = T();
            durationLabel.Text = $"Скорость: длительность перехода {t.DurationMs} мс (меньше — быстрее)";
            bandLabel.Text = $"Толщина полосы / кольца: {t.BandPercent:0}% (0 — без полосы)";
            bandOpacityLabel.Text = $"Непрозрачность полосы: {(int)(t.BandOpacity * 100)}%";
            angleLabel.Text = $"Наклон кромки (диагональный пролёт, волна): {t.Angle:0}°";
            partsLabel.Text = $"Количество частей (жалюзи, мозаика, полосы): {t.Parts}";
            strengthLabel.Text = $"Сила эффекта (волна, приближение, разброс частей): {t.Strength:0}%";
        }

        void SetSwatch(Border sw, string? text) =>
            sw.Background = !string.IsNullOrWhiteSpace(text) && Color.TryParse(text.Trim(), out var c) ? new SolidColorBrush(c) : Brushes.Transparent;

        void Reload()
        {
            loading = true;
            var t = T();
            noAnimation.IsChecked = !t.Enabled;
            preview.IsEnabled = t.Enabled;
            kindBox.SelectedIndex = (int)t.Kind;
            dirBox.SelectedIndex = (int)t.Direction;
            easeBox.SelectedIndex = (int)t.Ease;
            useBg.IsChecked = t.UseBackgroundColors;
            parallax.IsChecked = t.Parallax;
            glow.IsChecked = t.Glow;
            pauseBg.IsChecked = t.PauseBackground;
            color1.Text = t.Color1; color2.Text = t.Color2;
            SetSwatch(sw1, t.Color1); SetSwatch(sw2, t.Color2);
            duration.Value = t.DurationMs; band.Value = t.BandPercent; bandOpacity.Value = t.BandOpacity;
            angle.Value = t.Angle; parts.Value = t.Parts; strength.Value = t.Strength;
            UpdateLabels();
            loading = false;
        }

        noAnimation.IsCheckedChanged += (_, _) =>
        {
            if (loading) return;
            T().Enabled = noAnimation.IsChecked != true;
            preview.IsEnabled = T().Enabled;
            if (!T().Enabled) _overlay.Cancel();
        };
        useBg.IsCheckedChanged += (_, _) => { if (!loading) T().UseBackgroundColors = useBg.IsChecked == true; };
        parallax.IsCheckedChanged += (_, _) => { if (!loading) T().Parallax = parallax.IsChecked == true; };
        glow.IsCheckedChanged += (_, _) => { if (!loading) T().Glow = glow.IsChecked == true; };
        pauseBg.IsCheckedChanged += (_, _) => { if (!loading) T().PauseBackground = pauseBg.IsChecked == true; };
        kindBox.SelectionChanged += (_, _) => { if (!loading && kindBox.SelectedIndex >= 0) T().Kind = (TransitionKind)kindBox.SelectedIndex; };
        dirBox.SelectionChanged += (_, _) => { if (!loading && dirBox.SelectedIndex >= 0) T().Direction = (TransitionDirection)dirBox.SelectedIndex; };
        easeBox.SelectionChanged += (_, _) => { if (!loading && easeBox.SelectedIndex >= 0) T().Ease = (TransitionEase)easeBox.SelectedIndex; };
        color1.TextChanged += (_, _) =>
        {
            SetSwatch(sw1, color1.Text);
            if (!loading && Color.TryParse(color1.Text?.Trim() ?? "", out _)) T().Color1 = color1.Text!.Trim();
        };
        color2.TextChanged += (_, _) =>
        {
            SetSwatch(sw2, color2.Text);
            if (!loading && Color.TryParse(color2.Text?.Trim() ?? "", out _)) T().Color2 = color2.Text!.Trim();
        };
        void Hook(Slider sl, Action<double> set)
        {
            sl.PropertyChanged += (_, e) =>
            {
                if (e.Property != Avalonia.Controls.Primitives.RangeBase.ValueProperty || loading) return;
                set(sl.Value); UpdateLabels();
            };
        }
        Hook(duration, v => T().DurationMs = (int)Math.Round(v / 10) * 10);
        Hook(band, v => T().BandPercent = Math.Round(v));
        Hook(bandOpacity, v => T().BandOpacity = Math.Round(v, 2));
        Hook(angle, v => T().Angle = Math.Round(v));
        Hook(parts, v => T().Parts = (int)Math.Round(v));
        Hook(strength, v => T().Strength = Math.Round(v));

        preview.Click += (_, _) => _previewTransition?.Invoke();

        Control ColorRow(Border sw, TextBox tb) =>
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { sw, tb } };

        Reload();
        var panel = Page(
            new TextBlock { Text = "Анимация переходов между страницами", FontSize = 18, FontWeight = FontWeight.SemiBold },
            new TextBlock
            {
                Text = "Анимация использует отдельные снимки содержимого вкладок, не захватывает предыдущий слой перехода и автоматически отключается при ошибке снимка. " +
                       "Для максимальной скорости выберите режим без анимации. Эффект, скорость, цвет и направление меняются здесь.",
                Opacity = .7, TextWrapping = TextWrapping.Wrap
            },
            noAnimation,
            pauseBg,
            Caption("Эффект (используется, когда анимация включена)"), kindBox,
            Caption("Направление (откуда приходит новая страница)"), dirBox,
            Caption("Плавность движения"), easeBox,
            durationLabel, duration,
            Caption("Цвет 1 — передняя кромка полосы (HEX)"), ColorRow(sw1, color1),
            Caption("Цвет 2 — задняя кромка полосы / свечение (HEX)"), ColorRow(sw2, color2),
            useBg,
            bandLabel, band, bandOpacityLabel, bandOpacity,
            angleLabel, angle, partsLabel, parts, strengthLabel, strength,
            parallax, glow,
            new StackPanel { Margin = new Thickness(0, 10, 0, 0), Children = { preview } },
            new TextBlock { Text = "Предпросмотр переключает страницу и возвращает её обратно.", Opacity = .6, FontSize = 12 });
        return (panel, Reload);
    }

    private Control BuildGuiTab()
    {
        var loading = true;
        var info = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var presetBox = new ComboBox { ItemsSource = GuiSettings.Presets.Select(p => p.Title).ToList(), MinWidth = 280 };
        var modeBox = new ComboBox { ItemsSource = GuiSettings.ModeTitles, MinWidth = 280 };
        var colorBoxes = new List<TextBox>();
        var swatches = new List<Border>();
        var colorRows = new StackPanel { Spacing = 6 };
        for (var i = 0; i < 5; i++)
        {
            var tb = new TextBox { Width = 160, Watermark = i < 2 ? $"#RRGGBB (цвет {i + 1})" : $"#RRGGBB (необязательно)" };
            var sw = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(10), BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), BorderThickness = new Thickness(1) };
            colorBoxes.Add(tb); swatches.Add(sw);
            colorRows.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { sw, tb } });
        }
        var speed = new Slider { Minimum = 0.1, Maximum = 4, Width = 320 };
        var density = new Slider { Minimum = 3, Maximum = 150, Width = 320 };
        var panelOpacity = new Slider { Minimum = 0, Maximum = 0.95, Width = 320 };
        var radius = new Slider { Minimum = 0, Maximum = 32, Width = 320 };
        var speedLabel = Caption(""); var densityLabel = Caption(""); var opacityLabel = Caption(""); var radiusLabel = Caption("");
        // В Avalonia один визуальный контрол нельзя помещать сразу в два контейнера.
        // Поэтому для быстрого режима и вкладки фона используются отдельные CheckBox,
        // которые управляют одной общей настройкой _gui.Animate.
        var animateQuick = new CheckBox { Content = "Анимация фона включена" };
        var animateBackground = new CheckBox { Content = "Анимация фона включена" };
        var dealCards = new CheckBox { Content = "Показывать события сделок карточками в чатах (оплата, подтверждение, отзыв)" };
        var buyer = new TextBox { Width = 160, Watermark = "#RRGGBB" };
        var own = new TextBox { Width = 160, Watermark = "#RRGGBB" };
        var quickSelection = new TextBlock { FontSize = 12, Opacity = .7, TextWrapping = TextWrapping.Wrap };
        var (transitionPanel, reloadTransitions) = BuildTransitionPanel();

        void LoadToUi()
        {
            loading = true;
            presetBox.SelectedIndex = Math.Max(-1, Array.FindIndex(GuiSettings.Presets, p => p.Key == _gui.Preset));
            modeBox.SelectedIndex = (int)_gui.Mode;
            for (var i = 0; i < 5; i++)
            {
                colorBoxes[i].Text = i < _gui.Colors.Count ? _gui.Colors[i] : "";
                swatches[i].Background = i < _gui.Colors.Count && Color.TryParse(_gui.Colors[i], out var c) ? new SolidColorBrush(c) : Brushes.Transparent;
            }
            speed.Value = _gui.Speed; density.Value = _gui.Density; panelOpacity.Value = _gui.PanelOpacity; radius.Value = _gui.CornerRadius;
            animateQuick.IsChecked = _gui.Animate;
            animateBackground.IsChecked = _gui.Animate;
            dealCards.IsChecked = _gui.ShowDealCards; buyer.Text = _gui.BuyerBubble; own.Text = _gui.OwnBubble;
            UpdateLabels();
            reloadTransitions();
            loading = false;
        }
        void UpdateLabels()
        {
            speedLabel.Text = $"Скорость анимации: {_gui.Speed:0.0}×";
            densityLabel.Text = $"Количество элементов (пузырей, снежинок, звёзд): {_gui.Density}";
            opacityLabel.Text = $"Непрозрачность панелей: {(int)(_gui.PanelOpacity * 100)}%";
            radiusLabel.Text = $"Скругление углов: {(int)_gui.CornerRadius}";
            var selectedPreset = GuiSettings.Presets.FirstOrDefault(p => p.Key == _gui.Preset);
            quickSelection.Text = selectedPreset is null
                ? "Выбран собственный стиль. Точные значения доступны во вкладках «Фон» и «Оформление»."
                : "Текущий стиль: " + selectedPreset.Title;
        }

        var quickPresets = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 6) };
        foreach (var preset in GuiSettings.Presets)
        {
            var swatchesRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            foreach (var color in preset.Colors.Take(4))
                if (Color.TryParse(color, out var chipColor))
                    swatchesRow.Children.Add(new Border
                    {
                        Width = 22, Height = 7, CornerRadius = new CornerRadius(4),
                        Background = new SolidColorBrush(chipColor)
                    });
            var presetButton = new Button
            {
                Width = 174, MinHeight = 72, Margin = new Thickness(0, 0, 8, 8),
                Padding = new Thickness(10, 8), HorizontalContentAlignment = HorizontalAlignment.Left,
                Content = new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock { Text = preset.Title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                        swatchesRow,
                        new TextBlock { Text = $"{preset.Density} элементов · {preset.Speed:0.#}×", FontSize = 10, Opacity = .6 }
                    }
                }
            };
            ToolTip.SetTip(presetButton, "Применить стиль «" + preset.Title + "» сразу");
            presetButton.Click += (_, _) =>
            {
                _gui.ApplyPreset(preset);
                LoadToUi();
                ApplyGui();
                info.Text = "Применён стиль «" + preset.Title + "». Нажмите «Сохранить оформление», чтобы сохранить его.";
            };
            quickPresets.Children.Add(presetButton);
        }

        presetBox.SelectionChanged += (_, _) =>
        {
            if (loading || presetBox.SelectedIndex < 0) return;
            _gui.ApplyPreset(GuiSettings.Presets[presetBox.SelectedIndex]);
            LoadToUi(); ApplyGui();
        };
        modeBox.SelectionChanged += (_, _) =>
        {
            if (loading || modeBox.SelectedIndex < 0) return;
            _gui.Mode = (BgMode)modeBox.SelectedIndex; ApplyGui();
        };
        for (var i = 0; i < 5; i++)
        {
            var idx = i;
            colorBoxes[i].TextChanged += (_, _) =>
            {
                if (loading) return;
                var t = colorBoxes[idx].Text?.Trim();
                swatches[idx].Background = !string.IsNullOrEmpty(t) && Color.TryParse(t, out var c) ? new SolidColorBrush(c) : Brushes.Transparent;
                var list = colorBoxes.Select(b => b.Text?.Trim() ?? "").Where(x => x.Length > 0 && Color.TryParse(x, out _)).ToList();
                if (list.Count == 0) return;
                _gui.Colors = list; _gui.Preset = "custom"; ApplyGui();
            };
        }
        void Hook(Slider sl, Action<double> set)
        {
            sl.PropertyChanged += (_, e) =>
            {
                if (e.Property != Avalonia.Controls.Primitives.RangeBase.ValueProperty || loading) return;
                set(sl.Value); UpdateLabels(); ApplyGui();
            };
        }
        Hook(speed, v => _gui.Speed = Math.Round(v, 1));
        Hook(density, v => _gui.Density = (int)v);
        Hook(panelOpacity, v => _gui.PanelOpacity = Math.Round(v, 2));
        Hook(radius, v => _gui.CornerRadius = Math.Round(v));
        void SetAnimationEnabled(bool enabled)
        {
            if (loading) return;
            loading = true;
            _gui.Animate = enabled;
            animateQuick.IsChecked = enabled;
            animateBackground.IsChecked = enabled;
            loading = false;
            ApplyGui();
        }
        animateQuick.IsCheckedChanged += (_, _) => SetAnimationEnabled(animateQuick.IsChecked == true);
        animateBackground.IsCheckedChanged += (_, _) => SetAnimationEnabled(animateBackground.IsChecked == true);
        dealCards.IsCheckedChanged += (_, _) => { if (!loading) _gui.ShowDealCards = dealCards.IsChecked == true; };
        buyer.TextChanged += (_, _) => { if (!loading && Color.TryParse(buyer.Text?.Trim() ?? "", out _)) _gui.BuyerBubble = buyer.Text!.Trim(); };
        own.TextChanged += (_, _) => { if (!loading && Color.TryParse(own.Text?.Trim() ?? "", out _)) _gui.OwnBubble = own.Text!.Trim(); };

        var save = new Button { Content = "💾 Сохранить оформление" };
        save.Click += (_, _) =>
        {
            try { _gui.Save(); info.Text = "✅ Оформление сохранено. Цвета сообщений обновятся при следующем открытии чата."; }
            catch (Exception ex) { info.Text = "❌ " + ex.Message; }
        };
        var reset = new Button { Content = "↺ Сбросить всё" };
        reset.Click += (_, _) =>
        {
            _gui.ApplyPreset(GuiSettings.Presets[0]);
            _gui.Animate = true; _gui.PanelOpacity = 0.45; _gui.CornerRadius = 18;
            _gui.BuyerBubble = "#252A34"; _gui.OwnBubble = "#2457A6";
            _gui.Transition = new TransitionSettings();
            LoadToUi(); ApplyGui();
            info.Text = "Оформление и переходы сброшены (не забудьте сохранить).";
        };

        LoadToUi();

        // Быстрый режим — применить готовый стиль без ручной настройки каждого параметра.
        var quickTab = new ScrollViewer
        {
            Content = Page(
                new TextBlock { Text = "Быстрое оформление", FontSize = 18, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = "Выберите готовый вид одним нажатием. Цвета, плотность и скорость будут подобраны автоматически.", Opacity = .68, TextWrapping = TextWrapping.Wrap },
                quickSelection,
                quickPresets,
                new Border { Height = 1, Background = new SolidColorBrush(Color.FromArgb(0x28, 255, 255, 255)), Margin = new Thickness(0, 6) },
                new TextBlock { Text = "Быстрые параметры", FontSize = 15, FontWeight = FontWeight.SemiBold },
                animateQuick,
                dealCards,
                new TextBlock { Text = "Для точной настройки откройте вкладки «Фон», «Переходы» и «Оформление». Изменения применяются сразу, а кнопка внизу сохраняет их между запусками.", Opacity = .62, TextWrapping = TextWrapping.Wrap }
            )
        };

        // Дополнительные настройки сгруппированы по назначению.
        var backgroundTab = new ScrollViewer
        {
            Content = Page(
                Caption("Шаблон (применяет цвета, стиль и плотность)"), presetBox,
                Caption("Стиль анимации фона"), modeBox,
                Caption("Цвета, которые переливаются (HEX; вставьте код вручную, пустые поля игнорируются)"), colorRows,
                speedLabel, speed, densityLabel, density,
                animateBackground)
        };
        var transitionsTab = new ScrollViewer { Content = transitionPanel };
        var lookTab = new ScrollViewer
        {
            Content = Page(
                opacityLabel, panelOpacity, radiusLabel, radius,
                Caption("Цвет сообщений покупателя (слева)"), buyer,
                Caption("Цвет ваших сообщений (справа)"), own)
        };

        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = "✨ Быстро", Content = quickTab });
        tabs.Items.Add(new TabItem { Header = "🖼 Фон", Content = backgroundTab });
        tabs.Items.Add(new TabItem { Header = "🎞 Анимация", Content = transitionsTab });
        tabs.Items.Add(new TabItem { Header = "🧩 Интерфейс", Content = lookTab });

        var footer = new StackPanel
        {
            Spacing = 8, Margin = new Thickness(0, 10, 0, 0),
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { save, reset } },
                info
            }
        };
        var title = new TextBlock { Text = "Оформление", FontSize = 20, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 8) };
        Grid.SetRow(tabs, 1); Grid.SetRow(footer, 2);
        return new Grid
        {
            Margin = new Thickness(14),
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            Children = { title, tabs, footer }
        };
    }
}
