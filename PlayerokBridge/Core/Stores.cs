using System.Text.Json.Serialization;

namespace PlayerokBridge.Core;

/// <summary>Секреты и параметры подключения. Хранятся в папке пользователя, а не рядом с кодом.</summary>
public sealed class AppSettings
{
    public const string FileName = "app_settings.json";

    [JsonPropertyName("tg_bot_token")] public string TgBotToken { get; set; } = "";
    [JsonPropertyName("admin_id")] public long AdminId { get; set; }
    [JsonPropertyName("playerok_token")] public string PlayerokToken { get; set; } = "";
    [JsonPropertyName("playerok_ddg3")] public string PlayerokDdg3 { get; set; } = "";
    [JsonPropertyName("user_agent")] public string UserAgent { get; set; } =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/136.0.0.0 Safari/537.36";
    [JsonPropertyName("tg_proxy")] public string TgProxy { get; set; } = "";
    [JsonPropertyName("tz_offset_hours")] public double TzOffsetHours { get; set; } = 3;
    [JsonPropertyName("balance_poll_seconds")] public int BalancePollSeconds { get; set; } = 60;
    /// <summary>Автоответ по ключевым словам прямо в приложении (работает, когда Python-бот не запущен).</summary>
    [JsonPropertyName("app_autoreply")] public bool AppAutoReply { get; set; } = true;

    /// <summary>Язык интерфейса: "ru" или "en". Пусто — язык ещё не выбран (покажется окно авторизации).</summary>
    [JsonPropertyName("language")] public string Language { get; set; } = "";
    /// <summary>Имя шрифта интерфейса. Пусто — системный шрифт по умолчанию.</summary>
    [JsonPropertyName("ui_font")] public string UiFont { get; set; } = "";

    public bool IsComplete => TgBotToken.Length > 0 && AdminId != 0 && PlayerokToken.Length > 0;

    public static AppSettings Load()
    {
        try { return JsonStore.Load<AppSettings>(FileName) ?? new AppSettings(); }
        catch { return new AppSettings(); }
    }

    public void Save() => JsonStore.Save(FileName, this);
}

public sealed class ReviewSettings
{
    public const string FileName = "review_request.json";
    public const string DefaultText =
        "Спасибо за покупку, {buyer}! 🙌 Если всё понравилось, оставьте, пожалуйста, отзыв — " +
        "для меня это очень важно ⭐\n— {seller}";

    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("text")] public string Text { get; set; } = DefaultText;

    public static ReviewSettings Load()
    {
        try
        {
            var s = JsonStore.Load<ReviewSettings>(FileName) ?? new ReviewSettings();
            if (string.IsNullOrWhiteSpace(s.Text)) s.Text = DefaultText;
            return s;
        }
        catch { return new ReviewSettings(); }
    }

    public void Save() => JsonStore.Save(FileName, this);
}

/// <summary>
/// AFK-режим: второе сообщение, которое уходит сразу после автоответа по товару.
/// Файл afk_settings.json общий с Python-ботом (он перечитывает его при каждой сделке).
/// </summary>
public sealed class AfkSettings
{
    public const string FileName = "afk_settings.json";
    public const string DefaultText =
        "Здравствуйте, {buyer}! Сейчас я не в сети (AFK) 🌙 Как только вернусь, сразу выдам заказ «{item}». " +
        "Спасибо за терпение!\n— {seller}";

    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("text")] public string Text { get; set; } = DefaultText;
    /// <summary>Пауза между ответом по товару и AFK-сообщением (чтобы сообщения пришли по порядку).</summary>
    [JsonPropertyName("delay_seconds")] public double DelaySeconds { get; set; } = 2;
    /// <summary>Отправлять AFK, даже если ответ по товару не подобран.</summary>
    [JsonPropertyName("send_without_product")] public bool SendWithoutProduct { get; set; } = true;

    public static AfkSettings Load()
    {
        try
        {
            var s = JsonStore.Load<AfkSettings>(FileName) ?? new AfkSettings();
            if (string.IsNullOrWhiteSpace(s.Text)) s.Text = DefaultText;
            s.DelaySeconds = Math.Clamp(s.DelaySeconds, 0, 60);
            return s;
        }
        catch { return new AfkSettings(); }
    }

    public void Save() => JsonStore.Save(FileName, this);
}

/// <summary>Быстрые ответы (auto_replies.json — просто список строк).</summary>
public sealed class QuickReplyStore
{
    public const string FileName = "auto_replies.json";

    private static readonly string[] Defaults =
    {
        "Здравствуйте! Сейчас всё сделаю, минутку 🙂",
        "Спасибо за покупку! Если всё устроило, подтвердите сделку и оставьте отзыв ⭐",
        "Подскажите, пожалуйста, подробнее — чем могу помочь?",
        "Сейчас проверю и вернусь с ответом.",
    };

    public List<string> Items { get; private set; }

    public QuickReplyStore()
    {
        List<string>? loaded = null;
        try { loaded = JsonStore.Load<List<string>>(FileName); } catch { loaded = new(); }
        Items = loaded is null
            ? Defaults.ToList()
            : loaded.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
    }

    public void Save() => JsonStore.Save(FileName, Items);
}

/// <summary>Ответы по товарам (product_replies.json) — формат совместим с версией на Python.</summary>
public sealed class ProductReplyStore
{
    public const string FileName = "product_replies.json";

    private sealed class Dto
    {
        [JsonPropertyName("keyword")] public string Keyword { get; set; } = "";
        [JsonPropertyName("text")] public string Text { get; set; } = "";
    }

    private static readonly ProductReply[] Defaults =
    {
        new("standoff, стандофф, standoff 2",
            "Здравствуйте, {buyer}! Спасибо за покупку Standoff 2 🙂 Напишите, пожалуйста, ваш игровой ID — я сразу всё сделаю. После получения подтвердите сделку и оставьте отзыв ⭐\n— {seller}"),
        new("brawl+гем, brawl+gem, бравл+гем, бравл+gem",
            "Здравствуйте, {buyer}! Спасибо за покупку гемов Brawl Stars 💎 Напишите, пожалуйста, данные, необходимые для начисления. После получения подтвердите сделку и оставьте отзыв ⭐\n— {seller}"),
        new("brawl+pass+plus, brawl+pass+плюс, brawl+пропуск+plus, brawl+пропуск+плюс, бравл+pass+plus, бравл+пропуск+плюс",
            "Здравствуйте, {buyer}! Спасибо за покупку Brawl Pass Plus 💎 Напишите, пожалуйста, данные, необходимые для активации. После получения подтвердите сделку и оставьте отзыв ⭐\n— {seller}"),
        new("brawl+pass, brawl+пропуск, бравл+pass, бравл+пропуск",
            "Здравствуйте, {buyer}! Спасибо за покупку Brawl Pass 🎫 Напишите, пожалуйста, данные, необходимые для активации. После получения подтвердите сделку и оставьте отзыв ⭐\n— {seller}"),
        new("brawl, бравл",
            "Здравствуйте, {buyer}! Спасибо за покупку Brawl Stars 🙂 Напишите, пожалуйста, данные, необходимые для выдачи заказа. После получения подтвердите сделку и оставьте отзыв ⭐\n— {seller}"),
    };

    public List<ProductReply> Items { get; private set; }

    public ProductReplyStore()
    {
        try
        {
            var dto = JsonStore.Load<List<Dto>>(FileName);
            Items = dto is null
                ? Defaults.ToList()
                : dto.Where(x => !string.IsNullOrWhiteSpace(x.Keyword) && !string.IsNullOrWhiteSpace(x.Text))
                     .Select(x => new ProductReply(x.Keyword, x.Text)).ToList();
        }
        catch { Items = new(); }
    }

    public void Save() =>
        JsonStore.Save(FileName, Items.Select(x => new Dto { Keyword = x.Keyword, Text = x.Text }).ToList());
}
