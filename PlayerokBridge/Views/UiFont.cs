using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace PlayerokBridge.Views;

/// <summary>Смена шрифта интерфейса «на лету». Пустое имя — системный шрифт по умолчанию.</summary>
public static class UiFont
{
    // Fluent-тема задаёт шрифт элементов через этот ресурс, поэтому одного Window.FontFamily мало.
    private const string ThemeKey = "ContentControlThemeFontFamily";

    public static FontFamily? Current { get; private set; }

    /// <summary>Список шрифтов, установленных в системе (без повторов, по алфавиту).</summary>
    public static List<string> Available()
    {
        try
        {
            return FontManager.Current.SystemFonts
                .Select(f => f.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch { return new List<string>(); }
    }

    public static void Apply(string? name)
    {
        FontFamily? family = null;
        if (!string.IsNullOrWhiteSpace(name))
        {
            try { family = new FontFamily(name); } catch { family = null; }
        }
        Current = family;

        var app = Application.Current;
        if (app is null) return;
        try
        {
            // Fluent использует этот ресурс в шаблонах контролов. Обновляем его
            // при каждом выборе, включая возврат к системному шрифту.
            if (family is null)
                app.Resources.Remove(ThemeKey);
            else
                app.Resources[ThemeKey] = family;
        }
        catch { /* ошибка применения шрифта не должна ронять приложение */ }

        if (app.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            foreach (var w in desktop.Windows.ToList()) ApplyTo(w);
        }
    }

    public static void ApplyTo(Window window)
    {
        try
        {
            // FontFamily наследуется дочерними контролами от окна. Важно очищать
            // именно наследуемое свойство TextElement, чтобы старый выбор не оставался
            // локальным значением после переключения на системный шрифт.
            if (Current is null)
                window.ClearValue(TextElement.FontFamilyProperty);
            else
                window.SetValue(TextElement.FontFamilyProperty, Current);
        }
        catch { }
    }
}
