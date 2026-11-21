namespace PlayerokBridge.Core;

public enum LogLevel { Info, Warning, Error }

[Flags]
public enum LogTopic
{
    None = 0,
    Connection = 1,   // подключение, токены, повторы
    Polling = 2,      // опрос чатов, монитор, продажи для фильтров
    Purchases = 4,    // покупки и напоминания о невыданных заказах
    Telegram = 8,
    Bot = 16,         // Python-бот
    All = Connection | Polling | Purchases | Telegram | Bot,
}

public sealed record LogEntry(string Text, LogLevel Level, LogTopic Topics);

/// <summary>Журнал событий в памяти: уровень и тема определяются по тексту строки. Используется только из UI-потока.</summary>
public sealed class LogBuffer
{
    private const int MaxEntries = 3000;
    private readonly List<LogEntry> _entries = new();

    public event Action<LogEntry>? Added;
    public event Action? Cleared;

    public IReadOnlyList<LogEntry> Snapshot() => _entries.ToList();

    public void Add(string line)
    {
        var entry = Classify(line);
        _entries.Add(entry);
        if (_entries.Count > MaxEntries) _entries.RemoveRange(0, _entries.Count - MaxEntries + 500);
        Added?.Invoke(entry);
    }

    public void Clear()
    {
        _entries.Clear();
        Cleared?.Invoke();
    }

    public static LogEntry Classify(string text)
    {
        var t = text ?? "";
        var isBot = t.Contains("[BOT]", StringComparison.Ordinal);

        var level = LogLevel.Info;
        if (t.Contains("❌") || t.Contains("Traceback", StringComparison.Ordinal)
            || (isBot && (t.Contains("ERROR", StringComparison.Ordinal) || t.Contains("Exception", StringComparison.Ordinal))))
            level = LogLevel.Error;
        else if (t.Contains("⚠️") || t.Contains("⏰") || t.Contains("Не удалось", StringComparison.OrdinalIgnoreCase)
                 || t.Contains("Ошибка", StringComparison.OrdinalIgnoreCase) || t.Contains("недействителен", StringComparison.OrdinalIgnoreCase)
                 || (isBot && t.Contains("WARNING", StringComparison.Ordinal)))
            level = LogLevel.Warning;

        var topics = LogTopic.None;
        if (isBot || t.Contains("Python", StringComparison.OrdinalIgnoreCase)) topics |= LogTopic.Bot;
        if (t.Contains("Telegram", StringComparison.OrdinalIgnoreCase)) topics |= LogTopic.Telegram;
        if (t.Contains("🛒") || t.Contains("⏰") || t.Contains("покупк", StringComparison.OrdinalIgnoreCase)
            || t.Contains("не выдан", StringComparison.OrdinalIgnoreCase)) topics |= LogTopic.Purchases;
        if (t.Contains("Опрос", StringComparison.OrdinalIgnoreCase) || t.Contains("Монитор", StringComparison.OrdinalIgnoreCase)
            || t.Contains("Чаты обновляются", StringComparison.OrdinalIgnoreCase)
            || t.Contains("чат", StringComparison.OrdinalIgnoreCase) || t.Contains("продаж", StringComparison.OrdinalIgnoreCase))
            topics |= LogTopic.Polling;
        if (t.Contains("подключ", StringComparison.OrdinalIgnoreCase) || t.Contains("токен", StringComparison.OrdinalIgnoreCase)
            || t.Contains("Повтор", StringComparison.OrdinalIgnoreCase) || t.Contains("PlayerOK как", StringComparison.OrdinalIgnoreCase)
            || t.Contains("Остановлено", StringComparison.OrdinalIgnoreCase))
            topics |= LogTopic.Connection;

        return new LogEntry(t, level, topics);
    }
}
