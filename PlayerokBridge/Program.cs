using System.Reflection;
using System.Text;
using Avalonia;
using Avalonia.Threading;
using PlayerokBridge.Core;

namespace PlayerokBridge;

internal static class Program
{
    private static readonly object CrashLock = new();
    private const long MaxLogBytes = 2 * 1024 * 1024; // при превышении старый лог переименовывается в crash.old.log

    [STAThread]
    public static void Main(string[] args)
    {
        try { JsonStore.Init(); }
        catch (Exception ex) { LogCrash("JsonStore.Init", ex); }

        // Любое необработанное исключение пишем в crash.log, чтобы причину вылета можно было увидеть.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            LogCrash(e.IsTerminating ? "AppDomain (приложение завершается)" : "AppDomain", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()));
        TaskScheduler.UnobservedTaskException += (_, e) => { LogCrash("Task (необработанное исключение задачи)", e.Exception); e.SetObserved(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => LogLine("Процесс завершён.");

        LogLine($"Запуск. {Describe()}");
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            LogLine("Приложение закрыто штатно.");
        }
        catch (Exception ex)
        {
            LogCrash("Main (фатальная ошибка)", ex);
            throw;
        }
    }

    /// <summary>Ошибка в обработчике UI не должна убивать приложение: пишем в лог и продолжаем работу.</summary>
    internal static void InstallUiHandler()
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            LogCrash("UI", e.Exception);
            e.Handled = true;
        };
    }

    /// <summary>Путь к crash.log (если папка данных недоступна — рядом с EXE или во временной папке).</summary>
    internal static string CrashLogPath
    {
        get
        {
            var candidates = new List<string>();
            if (!string.IsNullOrEmpty(JsonStore.DataDir)) candidates.Add(JsonStore.DataDir);
            candidates.Add(AppContext.BaseDirectory);
            candidates.Add(Path.GetTempPath());
            foreach (var dir in candidates)
            {
                try
                {
                    Directory.CreateDirectory(dir);
                    return Path.Combine(dir, "crash.log");
                }
                catch { }
            }
            return "crash.log";
        }
    }

    internal static void LogCrash(string source, Exception? ex)
    {
        var sb = new StringBuilder();
        sb.Append('[').Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("] ").AppendLine(source);
        sb.AppendLine($"Поток: {Environment.CurrentManagedThreadId}, UI-поток: {Dispatcher.UIThread.CheckAccess()}");
        if (ex is null) sb.AppendLine("(исключение не передано)");
        else AppendException(sb, ex, 0);
        sb.AppendLine("Стек вызова в момент записи:");
        sb.AppendLine(Environment.StackTrace);
        sb.AppendLine(new string('-', 80));
        Write(sb.ToString());
    }

    /// <summary>Короткая служебная запись (запуск/выход), помогает понять, был ли выход штатным.</summary>
    internal static void LogLine(string text) =>
        Write($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}{Environment.NewLine}");

    private static void AppendException(StringBuilder sb, Exception ex, int depth)
    {
        var pad = new string(' ', depth * 2);
        sb.AppendLine($"{pad}{ex.GetType().FullName}: {ex.Message}");
        if (!string.IsNullOrEmpty(ex.StackTrace)) sb.AppendLine(ex.StackTrace);
        if (ex is AggregateException agg)
            foreach (var inner in agg.InnerExceptions) { sb.AppendLine($"{pad}--- вложенное (Aggregate) ---"); AppendException(sb, inner, depth + 1); }
        else if (ex.InnerException is { } ie)
        { sb.AppendLine($"{pad}--- вложенное исключение ---"); AppendException(sb, ie, depth + 1); }
    }

    private static void Write(string text)
    {
        try
        {
            lock (CrashLock)
            {
                var path = CrashLogPath;
                try
                {
                    var fi = new FileInfo(path);
                    if (fi.Exists && fi.Length > MaxLogBytes)
                        File.Move(path, Path.Combine(fi.DirectoryName ?? "", "crash.old.log"), true);
                }
                catch { }
                File.AppendAllText(path, text, Encoding.UTF8);
            }
        }
        catch { }
    }

    private static string Describe()
    {
        var ver = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?";
        return $"версия {ver}, {Environment.OSVersion}, .NET {Environment.Version}, {(Environment.Is64BitProcess ? "x64" : "x86")}, лог: {CrashLogPath}";
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect();
}
