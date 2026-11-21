using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PlayerokBridge.Core;

namespace PlayerokBridge.Playerok;

public class PlayerokException : Exception
{
    public PlayerokException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>PlayerOK отдал страницу защиты (DDoS-Guard/Cloudflare) вместо JSON.</summary>
public sealed class BotCheckException : PlayerokException
{
    public BotCheckException() : base(
        "PlayerOK вернул проверку на бота. Обновите cookie __ddg (PlayerokDdg3) и User-Agent из браузера, " +
        "запускайте с того же IP. Если не помогает — у .NET другой TLS-отпечаток, см. README (curl-impersonate).") { }
}

public sealed class UnauthorizedException : PlayerokException
{
    public UnauthorizedException() : base("Токен PlayerOK недействителен или просрочен.") { }
}

/// <summary>
/// Транспорт GraphQL PlayerOK, повторяющий playerokapi: чтение — GET с persisted-хэшем
/// (variables/extensions в query string), запись — POST с полным текстом запроса.
/// Хэши и тексты запросов лежат во встроенном gql.json (извлечён из playerokapi/misc.py).
/// </summary>
public sealed class GraphQlTransport : IDisposable
{
    private const string Endpoint = "https://playerok.com/graphql";

    private static readonly string[] BotSignatures =
    {
        "<title>Just a moment...</title>", "window._cf_chl_opt", "Enable JavaScript and cookies to continue",
        "Checking your browser before accessing", "cf-browser-verification", "Cloudflare Ray ID",
    };

    private readonly HttpClient _http;
    private readonly Dictionary<string, string> _persisted;
    private readonly Dictionary<string, string> _queries;
    private readonly SemaphoreSlim _gate = new(3, 3); // чтение: до 3 запросов одновременно (открытие чата не ждёт чужие запросы)
    // Запись (отправка сообщения) идёт по своей очереди, чтобы не ждать долгую загрузку истории чата.
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public GraphQlTransport(AppSettings settings)
    {
        (_persisted, _queries) = LoadCatalog();

        var handler = new HttpClientHandler { UseCookies = false };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

        var cookies = $"token={settings.PlayerokToken}";
        if (!string.IsNullOrEmpty(settings.PlayerokDdg3)) cookies = $"__ddg3={settings.PlayerokDdg3}; " + cookies;

        var h = _http.DefaultRequestHeaders;
        h.TryAddWithoutValidation("user-agent", settings.UserAgent);
        h.TryAddWithoutValidation("accept-language", "ru-RU,ru;q=0.9,en-US;q=0.8,en;q=0.7");
        h.TryAddWithoutValidation("apollo-require-preflight", "true");
        h.TryAddWithoutValidation("apollographql-client-name", "web");
        h.TryAddWithoutValidation("origin", "https://playerok.com");
        h.TryAddWithoutValidation("referer", "https://playerok.com/");
        h.TryAddWithoutValidation("cookie", cookies);
        h.TryAddWithoutValidation("x-gql-path", "/");
        h.TryAddWithoutValidation("x-timezone-offset", "-240");
    }

    private static (Dictionary<string, string>, Dictionary<string, string>) LoadCatalog()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames().First(n => n.EndsWith("gql.json", StringComparison.Ordinal));
        using var stream = asm.GetManifestResourceStream(name)!;
        var root = JsonNode.Parse(stream)!;
        return (
            root["persisted"]!.AsObject().ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<string>()),
            root["queries"]!.AsObject().ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<string>()));
    }

    private static HttpRequestMessage Decorate(HttpRequestMessage request, string operation)
    {
        request.Headers.TryAddWithoutValidation("x-gql-op", operation);
        request.Headers.TryAddWithoutValidation("x-apollo-operation-name", operation);
        return request;
    }

    /// <summary>Чтение: GET с persisted query.</summary>
    public async Task<JsonNode> QueryAsync(string operation, JsonObject variables, CancellationToken ct)
    {
        if (!_persisted.TryGetValue(operation, out var hash))
            throw new PlayerokException($"Нет persisted-хэша для операции «{operation}».");

        var extensions = new JsonObject
        {
            ["persistedQuery"] = new JsonObject { ["version"] = 1, ["sha256Hash"] = hash },
        };
        var url = $"{Endpoint}?operationName={Uri.EscapeDataString(operation)}" +
                  $"&variables={Uri.EscapeDataString(variables.ToJsonString())}" +
                  $"&extensions={Uri.EscapeDataString(extensions.ToJsonString())}";

        return await SendAsync(() => Decorate(new HttpRequestMessage(HttpMethod.Get, url), operation), ct, _gate);
    }

    /// <summary>Запись: POST с полным текстом запроса.</summary>
    public async Task<JsonNode> MutateAsync(string operation, JsonObject variables, CancellationToken ct)
    {
        if (!_queries.TryGetValue(operation, out var query))
            throw new PlayerokException($"Нет текста запроса для операции «{operation}».");

        var body = new JsonObject { ["operationName"] = operation, ["query"] = query, ["variables"] = variables };
        return await SendAsync(() =>
        {
            var req = Decorate(new HttpRequestMessage(HttpMethod.Post, Endpoint), operation);
            req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            return req;
        }, ct, _writeGate);
    }

    /// <summary>Создаёт объявление через GraphQL multipart (как create_item в playerokapi).</summary>
    public async Task<JsonNode> CreateItemAsync(
        JsonObject input, IReadOnlyList<CreateItemAttachment> attachments, CancellationToken ct)
    {
        const string operation = "createItem";
        if (!_queries.TryGetValue(operation, out var query))
            throw new PlayerokException("Нет текста запроса для создания объявления.");

        var attachmentNodes = new JsonArray();
        var map = new JsonObject();
        for (var i = 0; i < attachments.Count; i++)
        {
            attachmentNodes.Add((JsonNode?)null);
            map[i.ToString(System.Globalization.CultureInfo.InvariantCulture)] =
                new JsonArray($"variables.attachments.{i}");
        }

        var operations = new JsonObject
        {
            ["operationName"] = operation,
            ["query"] = query,
            ["variables"] = new JsonObject
            {
                ["input"] = input,
                ["attachments"] = attachmentNodes,
            },
        };

        return await SendAsync(() =>
        {
            var request = Decorate(new HttpRequestMessage(HttpMethod.Post, Endpoint), operation);
            var form = new MultipartFormDataContent();
            form.Add(new StringContent(operations.ToJsonString(), Encoding.UTF8), "operations");
            form.Add(new StringContent(map.ToJsonString(), Encoding.UTF8), "map");
            for (var i = 0; i < attachments.Count; i++)
            {
                var attachment = attachments[i];
                var file = new ByteArrayContent(attachment.Bytes);
                var extension = Path.GetExtension(attachment.FileName).ToLowerInvariant();
                file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(extension switch
                {
                    ".png" => "image/png",
                    ".webp" => "image/webp",
                    ".gif" => "image/gif",
                    ".bmp" => "image/bmp",
                    _ => "image/jpeg",
                });
                form.Add(file, i.ToString(System.Globalization.CultureInfo.InvariantCulture), Path.GetFileName(attachment.FileName));
            }
            request.Content = form;
            return request;
        }, ct, _writeGate);
    }

    /// <summary>GraphQL multipart upload (как в playerokapi.upload_chat_image_into_temporary_store).</summary>
    public async Task<JsonNode> UploadImageAsync(string chatId, byte[] bytes, string fileName, CancellationToken ct)
    {
        const string operation = "uploadChatImageIntoTemporaryStore";
        if (!_queries.TryGetValue(operation, out var query))
            throw new PlayerokException("Нет текста запроса для загрузки изображения.");

        var ops = new JsonObject
        {
            ["operationName"] = operation,
            ["query"] = query,
            ["variables"] = new JsonObject
            {
                ["file"] = null,
                ["input"] = new JsonObject { ["chatId"] = chatId, ["clientAttachmentId"] = Guid.NewGuid().ToString() },
            },
        };
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        var mime = ext switch { ".png" => "image/png", ".webp" => "image/webp", ".gif" => "image/gif", _ => "image/jpeg" };

        return await SendAsync(() =>
        {
            var req = Decorate(new HttpRequestMessage(HttpMethod.Post, Endpoint), operation);
            var form = new MultipartFormDataContent();
            form.Add(new StringContent(ops.ToJsonString(), Encoding.UTF8), "operations");
            form.Add(new StringContent("{\"1\":[\"variables.file\"]}", Encoding.UTF8), "map");
            var file = new ByteArrayContent(bytes);
            file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mime);
            form.Add(file, "1", Path.GetFileName(fileName));
            req.Content = form;
            return req;
        }, ct, _writeGate);
    }

    private async Task<JsonNode> SendAsync(Func<HttpRequestMessage> build, CancellationToken ct, SemaphoreSlim gate)
    {
        await gate.WaitAsync(ct);
        try
        {
            Exception? last = null;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    using var request = build();
                    using var response = await _http.SendAsync(request, ct);
                    var raw = await response.Content.ReadAsStringAsync(ct);

                    // Страница защиты — это HTML. Проверяем только не-JSON ответы: раньше шесть поисков подстроки
                    // шли по каждому (иногда очень большому) JSON, а сообщение покупателя с фразой вроде
                    // «Checking your browser before accessing» ложно считалось проверкой на бота.
                    var looksLikeJson = false;
                    foreach (var ch in raw)
                    {
                        if (char.IsWhiteSpace(ch) || ch == '\uFEFF') continue;
                        looksLikeJson = ch is '{' or '[';
                        break;
                    }
                    if (!looksLikeJson && BotSignatures.Any(raw.Contains)) throw new BotCheckException();

                    // Временные сбои шлюза и лимит частоты (HTTP) — повторяем, как и сетевые ошибки.
                    var statusCode = (int)response.StatusCode;
                    if (!looksLikeJson && statusCode is 429 or 502 or 503 or 504 && attempt < 2)
                    {
                        last = new PlayerokException($"PlayerOK вернул {statusCode}");
                        await Task.Delay(1500 * (attempt + 1), ct);
                        continue;
                    }

                    JsonNode? json = null;
                    try { json = JsonNode.Parse(raw); } catch (JsonException) { /* не JSON */ }

                    if (json?["errors"] is JsonArray { Count: > 0 } errors)
                    {
                        // Читаемый текст вместо \uXXXX-экранирования.
                        var text = string.Join("; ", errors.Select(e => e?["message"]?.ToString()).Where(m => !string.IsNullOrWhiteSpace(m)));
                        if (text.Length == 0) text = errors.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                        // «Слишком много попыток» — ограничение частоты запросов: ждём и повторяем.
                        if (IsRateLimit(text) && attempt < 2)
                        {
                            last = new PlayerokException("GraphQL: " + text);
                            await Task.Delay(2000 * (attempt + 1), ct);
                            continue;
                        }
                        throw new PlayerokException("GraphQL: " + Trim(text));
                    }
                    if ((int)response.StatusCode != 200 && (int)response.StatusCode != 304)
                        throw new PlayerokException($"PlayerOK вернул {(int)response.StatusCode}: {Trim(raw)}");

                    return json ?? throw new PlayerokException("Пустой ответ PlayerOK");
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    last = ex; // сетевой сбой — повторяем, как в playerokapi
                    await Task.Delay(500 * (attempt + 1), ct);
                }
            }
            throw new PlayerokException("Не удалось отправить запрос к PlayerOK: " + last?.Message, last);
        }
        finally { gate.Release(); }
    }

    private static bool IsRateLimit(string text) =>
        text.Contains("много попыток", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("too many", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("rate limit", StringComparison.OrdinalIgnoreCase);

    private static string Trim(string s) => s.Length > 300 ? s[..300] + "…" : s;

    public void Dispose() { _http.Dispose(); _gate.Dispose(); _writeGate.Dispose(); }
}
