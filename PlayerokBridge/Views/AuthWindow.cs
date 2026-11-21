using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using PlayerokBridge.Core;
using PlayerokBridge.Playerok;

namespace PlayerokBridge.Views;

/// <summary>
/// Окно первого входа. Шаг 1 — выбор языка (ru / en), шаг 2 — ввод токена Playerok.
/// Токен проверяется реальным запросом к Playerok; после успеха всё сохраняется в app_settings.json.
/// </summary>
public sealed class AuthWindow : Window
{
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#2457A6"));
    private static readonly IBrush Idle = new SolidColorBrush(Color.Parse("#252A34"));
    private static readonly IBrush Bad = new SolidColorBrush(Color.Parse("#FF6B6B"));
    private static readonly IBrush Good = new SolidColorBrush(Color.Parse("#5BD68A"));

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly Border _body = new();
    private string _lang;
    private CancellationTokenSource? _cts;

    /// <summary>Вход завершён, настройки сохранены — можно открывать главное окно.</summary>
    public event Action? Completed;

    public AuthWindow()
    {
        Title = "PlayerOK Bridge";
        Width = 500; Height = 600;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.Parse("#111318"));
        Foreground = new SolidColorBrush(Color.Parse("#F2F4F8"));
        try
        {
            var ico = Path.Combine(AppContext.BaseDirectory, "playerokbridge.ico");
            if (File.Exists(ico)) Icon = new WindowIcon(ico);
        }
        catch { }

        _lang = !string.IsNullOrEmpty(_settings.Language)
            ? Loc.Normalize(_settings.Language)
            : (System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru" ? Loc.Ru : Loc.EnCode);
        Loc.Init(_lang);

        _body.Padding = new Thickness(36, 32);
        Content = _body;

        // Язык уже выбран раньше — сразу просим токен; иначе начинаем с выбора языка.
        if (string.IsNullOrEmpty(_settings.Language)) ShowLanguageStep();
        else ShowTokenStep();

        Closed += (_, _) => { try { _cts?.Cancel(); } catch { } };
    }

    // ---------- Шаг 1: язык ----------

    private void ShowLanguageStep()
    {
        var ru = LangButton("RU", "Русский");
        var en = LangButton("EN", "English");
        void Paint()
        {
            ru.Background = _lang == Loc.Ru ? Accent : Idle;
            en.Background = _lang == Loc.EnCode ? Accent : Idle;
        }
        ru.Click += (_, _) => { _lang = Loc.Ru; Paint(); };
        en.Click += (_, _) => { _lang = Loc.EnCode; Paint(); };
        Paint();

        var next = new Button
        {
            Content = "Продолжить  /  Continue  →",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(14, 12), FontSize = 15, Background = Accent, Foreground = Brushes.White,
            Margin = new Thickness(0, 26, 0, 0),
        };
        next.Click += (_, _) =>
        {
            Loc.Init(_lang);
            _settings.Language = _lang;
            try { _settings.Save(); } catch (Exception ex) { Program.LogCrash("AuthWindow.SaveLanguage", ex); }
            // Токен уже сохранён (обновление со старой версии) — второй шаг не нужен.
            if (!string.IsNullOrWhiteSpace(_settings.PlayerokToken)) Completed?.Invoke();
            else ShowTokenStep();
        };

        _body.Child = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center, Spacing = 12,
            Children =
            {
                new TextBlock { Text = "PLAYEROK BRIDGE", FontSize = 13, FontWeight = FontWeight.Bold, Opacity = .6, HorizontalAlignment = HorizontalAlignment.Center },
                new TextBlock { Text = "Выберите язык", FontSize = 26, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center },
                new TextBlock { Text = "Choose your language", FontSize = 18, Opacity = .75, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 16) },
                ru, en,
                new TextBlock
                {
                    Text = "Язык можно изменить позже в «Настройках».  /  You can change it later in Settings.",
                    FontSize = 11, Opacity = .55, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
                    Margin = new Thickness(0, 8, 0, 0),
                },
                next,
            },
        };
    }

    private static Button LangButton(string code, string title)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 14, HorizontalAlignment = HorizontalAlignment.Center,
            Children =
            {
                new TextBlock { Text = code, FontSize = 14, FontWeight = FontWeight.Bold, Opacity = .7, VerticalAlignment = VerticalAlignment.Center, Width = 28 },
                new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center },
            },
        };
        return new Button
        {
            Content = row, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center, Padding = new Thickness(14, 16),
        };
    }

    // ---------- Шаг 2: токен Playerok ----------

    private void ShowTokenStep()
    {
        var token = new TextBox
        {
            PasswordChar = '•', Watermark = Loc.T("Токен Playerok", "Playerok token"),
            Text = _settings.PlayerokToken, FontSize = 14,
        };
        var ddg = new TextBox
        {
            PasswordChar = '•', Text = _settings.PlayerokDdg3,
            Watermark = Loc.T("Cookie __ddg3 (необязательно)", "Cookie __ddg3 (optional)"),
        };
        var reveal = new CheckBox { Content = Loc.T("Показать токен", "Show token") };
        reveal.IsCheckedChanged += (_, _) =>
        {
            var c = reveal.IsChecked == true ? '\0' : '•';
            token.PasswordChar = c; ddg.PasswordChar = c;
        };

        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, MinHeight = 36, FontSize = 13 };
        var signIn = new Button
        {
            Content = Loc.T("Войти", "Sign in"), HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center, Padding = new Thickness(14, 12),
            FontSize = 15, Background = Accent, Foreground = Brushes.White,
        };
        var force = new Button
        {
            Content = Loc.T("Сохранить без проверки", "Save without checking"),
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center,
            IsVisible = false,
        };
        var back = new Button { Content = Loc.T("← Язык", "← Language"), HorizontalAlignment = HorizontalAlignment.Left };
        back.Click += (_, _) => { _settings.Language = ""; ShowLanguageStep(); };

        void SetBusy(bool busy)
        {
            signIn.IsEnabled = !busy; force.IsEnabled = !busy; token.IsEnabled = !busy; ddg.IsEnabled = !busy; back.IsEnabled = !busy;
        }

        async Task Run(bool skipCheck)
        {
            var tok = (token.Text ?? "").Trim();
            if (tok.StartsWith("token=", StringComparison.OrdinalIgnoreCase)) tok = tok[6..];
            tok = tok.Trim().Trim(';', '"', '\'', ' ');
            if (tok.Length == 0)
            {
                status.Foreground = Bad;
                status.Text = Loc.T("❌ Вставьте токен Playerok.", "❌ Paste your Playerok token.");
                return;
            }

            _settings.PlayerokToken = tok;
            _settings.PlayerokDdg3 = (ddg.Text ?? "").Trim();
            force.IsVisible = false;

            if (!skipCheck)
            {
                SetBusy(true);
                status.Foreground = Brushes.White;
                status.Text = Loc.T("Проверяю токен…", "Checking the token…");
                try
                {
                    _cts?.Cancel();
                    _cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                    using var client = new PlayerokClient(_settings);
                    await client.ConnectAsync(_cts.Token);
                    status.Foreground = Good;
                    status.Text = Loc.T($"✅ Вход выполнен: {client.Username}", $"✅ Signed in as {client.Username}");
                }
                catch (Exception ex)
                {
                    SetBusy(false);
                    status.Foreground = Bad;
                    var reason = ex.GetType().Name == "UnauthorizedException"
                        ? Loc.T("токен недействителен или устарел.", "the token is invalid or expired.")
                        : ex is OperationCanceledException
                            ? Loc.T("превышено время ожидания.", "the request timed out.")
                            : ex.Message;
                    status.Text = Loc.T("❌ Не удалось войти: ", "❌ Sign-in failed: ") + reason;
                    force.IsVisible = true;
                    return;
                }
            }

            _settings.Language = _lang;
            try { _settings.Save(); }
            catch (Exception ex)
            {
                SetBusy(false);
                status.Foreground = Bad;
                status.Text = Loc.T("❌ Не удалось сохранить настройки: ", "❌ Failed to save settings: ") + ex.Message;
                return;
            }
            await Task.Delay(skipCheck ? 0 : 450);
            Completed?.Invoke();
        }

        signIn.Click += async (_, _) => await Run(false);
        force.Click += async (_, _) => await Run(true);
        token.KeyDown += async (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) await Run(false); };

        _body.Child = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                back,
                new TextBlock { Text = "PLAYEROK BRIDGE", FontSize = 13, FontWeight = FontWeight.Bold, Opacity = .6, Margin = new Thickness(0, 14, 0, 0) },
                new TextBlock { Text = Loc.T("Вход в Playerok", "Sign in to Playerok"), FontSize = 26, FontWeight = FontWeight.Bold },
                new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap, Opacity = .75, FontSize = 13,
                    Text = Loc.T(
                        "Вставьте токен Playerok — значение cookie «token» с сайта playerok.com. " +
                        "Его можно найти в браузере: F12 → Application → Cookies → playerok.com → token.",
                        "Paste your Playerok token — the value of the “token” cookie from playerok.com. " +
                        "You can find it in the browser: F12 → Application → Cookies → playerok.com → token."),
                },
                new TextBlock { Text = Loc.T("Токен Playerok", "Playerok token"), FontSize = 12, Opacity = .65, Margin = new Thickness(0, 10, 0, 0) },
                token,
                reveal,
                new TextBlock { Text = Loc.T("Cookie __ddg3 (если Playerok просит проверку)", "Cookie __ddg3 (if Playerok asks for verification)"), FontSize = 12, Opacity = .65 },
                ddg,
                status,
                signIn,
                force,
            },
        };
        Dispatcher.UIThread.Post(() => token.Focus(), DispatcherPriority.Background);
    }
}
