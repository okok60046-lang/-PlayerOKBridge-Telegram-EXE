using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace PlayerokBridge.Services;

/// <summary>Минимальный клиент Telegram Bot API поверх HTTP (без внешних библиотек).</summary>
public sealed class TelegramApi : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _base;

    public TelegramApi(string token, string? proxy)
    {
        var handler = new HttpClientHandler();
        if (!string.IsNullOrWhiteSpace(proxy)) handler.Proxy = new WebProxy(proxy);
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(75) }; // long polling 50 c + запас
        _base = $"https://api.telegram.org/bot{token}/";
    }

    public async Task<JsonNode> CallAsync(string method, JsonObject body, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(_base + method, content, ct);
            var raw = await response.Content.ReadAsStringAsync(ct);

            JsonNode? json;
            try { json = JsonNode.Parse(raw); }
            catch (System.Text.Json.JsonException)
            {
                // Прокси или шлюз вернул не JSON (HTML-страница ошибки): показываем код, а не «ошибку разбора».
                throw new InvalidOperationException($"Telegram {method}: HTTP {(int)response.StatusCode}, ответ не JSON");
            }
            if (json is null) throw new InvalidOperationException("Пустой ответ Telegram");

            if (json["ok"]?.GetValue<bool>() == true) return json["result"] ?? new JsonObject();

            // Flood-лимит: Telegram сам говорит, сколько ждать. Раньше уведомление в этот момент пропадало.
            var retryAfter = json["parameters"]?["retry_after"]?.GetValue<int>() ?? 0;
            if ((int)response.StatusCode == 429 && attempt < 2 && retryAfter > 0 && retryAfter <= 60)
            {
                await Task.Delay(TimeSpan.FromSeconds(retryAfter + 1), ct);
                continue;
            }
            throw new InvalidOperationException($"Telegram {method}: {json["description"]}");
        }
    }

    public async Task<long> SendMessageAsync(long chatId, string html, JsonNode? keyboard, CancellationToken ct)
    {
        html = TruncateHtml(html);
        var body = new JsonObject
        {
            ["chat_id"] = chatId,
            ["text"] = html,
            ["parse_mode"] = "HTML",
            ["disable_web_page_preview"] = true,
        };
        if (keyboard is not null) body["reply_markup"] = keyboard;
        try
        {
            var result = await CallAsync("sendMessage", body, ct);
            return result["message_id"]?.GetValue<long>() ?? 0;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("parse entities", StringComparison.OrdinalIgnoreCase))
        {
            // Оборванный после обрезки тег или сущность: отправляем обычным текстом, а не теряем уведомление.
            body.Remove("parse_mode");
            body["text"] = System.Net.WebUtility.HtmlDecode(HtmlTag.Replace(html, ""));
            var result = await CallAsync("sendMessage", body, ct);
            return result["message_id"]?.GetValue<long>() ?? 0;
        }
    }

    private const int MaxText = 4000; // предел Telegram — 4096 символов, берём с запасом
    private static readonly System.Text.RegularExpressions.Regex HtmlTag = new("<[^>]+>");

    /// <summary>Обрезает HTML, не разрывая тег или сущность (&amp; и т. п.) посередине.</summary>
    internal static string TruncateHtml(string html)
    {
        if (html.Length <= MaxText) return html;
        var cut = html[..(MaxText - 1)];
        var lt = cut.LastIndexOf('<');
        if (lt > cut.LastIndexOf('>')) cut = cut[..lt];
        var amp = cut.LastIndexOf('&');
        if (amp >= 0 && amp > cut.LastIndexOf(';') && cut.Length - amp <= 10) cut = cut[..amp];
        return cut + "…";
    }

    public Task AnswerCallbackAsync(string callbackId, string? text, bool alert, CancellationToken ct) =>
        CallAsync("answerCallbackQuery", new JsonObject
        {
            ["callback_query_id"] = callbackId,
            ["text"] = text ?? "",
            ["show_alert"] = alert,
        }, ct);

    public async Task<JsonArray> GetUpdatesAsync(long offset, CancellationToken ct)
    {
        var result = await CallAsync("getUpdates", new JsonObject
        {
            ["offset"] = offset,
            ["timeout"] = 50,
            ["allowed_updates"] = new JsonArray("message", "callback_query"),
        }, ct);
        return result.AsArray();
    }

    /// <summary>Inline-клавиатура из строк вида (текст, callback_data).</summary>
    public static JsonNode Keyboard(IEnumerable<(string Text, string Data)> rows)
    {
        var arr = new JsonArray();
        foreach (var (text, data) in rows)
            arr.Add(new JsonArray(new JsonObject
            {
                ["text"] = text.Length > 40 ? text[..37] + "…" : text,
                ["callback_data"] = data,
            }));
        return new JsonObject { ["inline_keyboard"] = arr };
    }

    public void Dispose() => _http.Dispose();
}
