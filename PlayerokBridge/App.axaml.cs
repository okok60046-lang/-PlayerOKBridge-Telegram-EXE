using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using PlayerokBridge.Core;
using PlayerokBridge.Views;

namespace PlayerokBridge;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        Program.InstallUiHandler();

        var settings = AppSettings.Load();
        Loc.Init(settings.Language);
        UiFont.Apply(settings.UiFont);
        Loc.StartWatcher();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Окно авторизации нужно при первом запуске: язык не выбран или нет токена Playerok.
            var needAuth = string.IsNullOrEmpty(settings.Language) || string.IsNullOrWhiteSpace(settings.PlayerokToken);
            if (needAuth)
            {
                var auth = new AuthWindow();
                UiFont.ApplyTo(auth);
                desktop.MainWindow = auth;
                auth.Completed += () =>
                {
                    // MainWindow заменяем до закрытия окна входа, иначе приложение завершится.
                    var main = new MainWindow();
                    UiFont.ApplyTo(main);
                    desktop.MainWindow = main;
                    main.Show();
                    Loc.ApplyAll();
                    auth.Close();
                };
            }
            else
            {
                var main = new MainWindow();
                UiFont.ApplyTo(main);
                desktop.MainWindow = main;
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
