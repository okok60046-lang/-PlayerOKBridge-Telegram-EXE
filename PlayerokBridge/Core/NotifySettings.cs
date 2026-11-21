namespace PlayerokBridge.Core;

/// <summary>Категории уведомлений и готовые наборы (порт NOTIFY_* из bot.py).</summary>
public sealed class NotifySettings
{
    public const string FileName = "notify_settings.json";

    public static readonly IReadOnlyDictionary<string, string> Categories = new Dictionary<string, string>
    {
        ["msg_buyer"] = "💬 Сообщения покупателей",
        ["msg_support"] = "🔰 Сообщения поддержки",
        ["msg_system"] = "ℹ️ Системные сообщения чата",
        ["deal_new"] = "🛒 Новые покупки",
        ["deal_confirmed"] = "✅ Подтверждения сделок",
        ["deal_refund"] = "↩️ Возвраты",
        ["deal_problem"] = "⚠️ Проблемы в сделках",
        ["review"] = "📝 Отзывы",
        ["unfreeze"] = "🔓 Разморозка средств",
        ["autoreply"] = "🤖 Отчёты об автоответах",
    };

    public static readonly IReadOnlyDictionary<string, (string Label, HashSet<string> Enabled)> Presets =
        new Dictionary<string, (string, HashSet<string>)>
        {
            ["all"] = ("🔔 Всё", new HashSet<string>(Categories.Keys)),
            ["support"] = ("🔰 Только поддержка", new HashSet<string> { "msg_support" }),
            ["buys"] = ("🛒 Только покупки", new HashSet<string> { "deal_new" }),
            ["nosupport"] = ("🙈 Без поддержки",
                new HashSet<string>(Categories.Keys.Except(new[] { "msg_support", "msg_system" }))),
            ["none"] = ("🔕 Выключить всё", new HashSet<string>()),
        };

    private readonly Dictionary<string, bool> _values = new();
    private readonly object _lock = new();

    public NotifySettings()
    {
        foreach (var key in Categories.Keys) _values[key] = key != "msg_system"; // системные дублируют события сделок
    }

    public static NotifySettings Load()
    {
        var settings = new NotifySettings();
        try
        {
            var data = JsonStore.Load<Dictionary<string, bool>>(FileName);
            if (data is not null)
                foreach (var (key, value) in data)
                    if (settings._values.ContainsKey(key)) settings._values[key] = value;
        }
        catch { /* битый файл — остаются значения по умолчанию */ }
        return settings;
    }

    /// <summary>Неизвестные категории считаются выключенными.</summary>
    public bool IsOn(string category)
    {
        lock (_lock) return _values.TryGetValue(category, out var v) && v;
    }

    public void Toggle(string category)
    {
        lock (_lock)
        {
            if (!_values.ContainsKey(category)) return;
            _values[category] = !_values[category];
            Persist();
        }
    }

    public void ApplyPreset(string key)
    {
        if (!Presets.TryGetValue(key, out var preset)) return;
        lock (_lock)
        {
            foreach (var category in Categories.Keys) _values[category] = preset.Enabled.Contains(category);
            Persist();
        }
    }

    private void Persist() => JsonStore.Save(FileName, new Dictionary<string, bool>(_values));
}
