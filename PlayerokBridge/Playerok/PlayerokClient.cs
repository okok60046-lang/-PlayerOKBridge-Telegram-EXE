using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PlayerokBridge.Core;

namespace PlayerokBridge.Playerok;

/// <summary>
/// Реальный клиент PlayerOK поверх GraphQL (повторяет методы playerokapi.Account).
/// Слушатель событий — опрос чатов раз в несколько секунд: проще и надёжнее websocket-варианта
/// оригинала и не требует отдельных потоков.
/// </summary>
public sealed partial class PlayerokClient : IPlayerokClient, IDisposable
{
    private static readonly string[] AllItemStatuses =
    {
        "PENDING_APPROVAL", "PENDING_MODERATION", "APPROVED", "DECLINED", "BLOCKED", "EXPIRED", "SOLD", "DRAFT",
    };

    private readonly GraphQlTransport _gql;
    public string? UserId { get; private set; }
    public string? Username { get; private set; }
    public string? AvatarUrl { get; private set; }
    public DateTimeOffset? RegisteredAt { get; private set; }

    public PlayerokClient(AppSettings settings) => _gql = new GraphQlTransport(settings);

    // ---------- Аккаунт ----------

    public async Task ConnectAsync(CancellationToken ct)
    {
        var json = await _gql.MutateAsync("viewer", new JsonObject(), ct);
        var viewer = json["data"]?["viewer"] ?? throw new UnauthorizedException();
        UserId = viewer["id"]?.ToString();
        Username = viewer["username"]?.ToString();
        AvatarUrl = viewer["profile"]?["avatarURL"]?.ToString();
        RegisteredAt = ParseTime(viewer["createdAt"]);
    }

    public async Task<Balance> GetBalanceAsync(CancellationToken ct)
    {
        var vars = new JsonObject { ["username"] = Username, ["hasSupportAccess"] = false };
        var json = await _gql.QueryAsync("user", vars, ct);
        var b = json["data"]?["user"]?["balance"];
        return new Balance(Dec(b?["value"]), Dec(b?["available"]), Dec(b?["frozen"]),
            Dec(b?["withdrawable"]), Dec(b?["pendingIncome"]));
    }

    // ---------- Чаты ----------

    private async Task<(JsonArray Nodes, string? NextCursor)> FetchChatsPageAsync(string? after, int count, CancellationToken ct)
    {
        var vars = new JsonObject
        {
            ["pagination"] = new JsonObject { ["first"] = count, ["after"] = after },
            ["filter"] = new JsonObject { ["userId"] = UserId, ["type"] = null, ["status"] = null },
            ["hasSupportAccess"] = false,
        };
        var json = await _gql.QueryAsync("userChats", vars, ct);
        var chats = json["data"]?["chats"];
        var nodes = new JsonArray();
        foreach (var edge in chats?["edges"]?.AsArray() ?? new JsonArray())
            if (edge?["node"] is JsonNode node) nodes.Add(node.DeepClone());

        var hasNext = chats?["pageInfo"]?["hasNextPage"]?.GetValue<bool>() ?? false;
        return (nodes, hasNext ? chats?["pageInfo"]?["endCursor"]?.ToString() : null);
    }

    private ChatSummary ToChatSummary(JsonNode chat)
    {
        var last = chat["lastMessage"];
        string? partner = null;
        foreach (var user in chat["participants"]?.AsArray() ?? new JsonArray())
            if (user?["id"]?.ToString() != UserId) { partner = user?["username"]?.ToString(); break; }
        if (partner is null && last?["user"]?["id"]?.ToString() != UserId)
            partner = last?["user"]?["username"]?.ToString();

        var chatId = chat["id"]!.ToString();
        var linkedDeals = new List<Deal>();
        // При нескольких покупках в одном чате главной считаем сделку без статуса.
        // Любая уже перешедшая из Unknown сделка (кроме Paid) остаётся побочной.
        // Это не меняет статусы на сервере — только выбирает сделку для интерфейса и команд.
        foreach (var dealNode in chat["deals"]?.AsArray() ?? new JsonArray())
        {
            if (dealNode is null) continue;
            try { linkedDeals.Add(ToDeal(dealNode) with { ChatId = chatId }); }
            catch { /* Одна неполная сделка не должна мешать загрузке чата. */ }
        }
        var activeDeal = SelectPrimaryChatDeal(linkedDeals);

        return new ChatSummary(
            chatId, partner ?? "Чат PlayerOK", last?["text"]?.ToString(),
            (int?)Dec(chat["unreadMessagesCounter"]) ?? 0, ParseTime(last?["createdAt"]), activeDeal);
    }

    private static Deal? SelectPrimaryChatDeal(IEnumerable<Deal> deals) => deals
        .OrderBy(d => d.Status switch
        {
            DealStatus.Unknown => 0,
            DealStatus.Paid => 1,
            _ => 2
        })
        .ThenByDescending(d => d.CreatedAt ?? DateTimeOffset.MinValue)
        .FirstOrDefault();

    public async Task<IReadOnlyList<ChatSummary>> GetAllChatsAsync(CancellationToken ct)
    {
        var all = new List<ChatSummary>();
        string? cursor = null;
        var seen = new HashSet<string>();
        for (var i = 0; i < 1000; i++) // защита от зацикливания курсора
        {
            var (nodes, next) = await FetchChatsPageAsync(cursor, 24, ct);
            all.AddRange(nodes.Where(n => n is not null).Select(n => ToChatSummary(n!)));
            if (next is null || !seen.Add(next)) break;
            cursor = next;
        }
        return all;
    }

    public async Task<IReadOnlyList<ChatMessage>> GetChatMessagesAsync(string chatId, int count, CancellationToken ct)
    {
        var page = await GetChatMessagesPageAsync(chatId, count, null, ct);
        return page.Messages;
    }

    /// <summary>
    /// Загружает одну страницу переписки. PlayerOK отдаёт сообщения от новых к старым:
    /// first=24 без курсора — самые свежие 24, а afterCursor (endCursor прошлой страницы) — предыдущие, более старые.
    /// Запрос «last без before», который использовался раньше, сервер отклоняет:
    /// «When paging backwards, a 'before' argument is required!».
    /// Возвращённые сообщения отсортированы по времени (старые сверху).
    /// </summary>
    public async Task<(IReadOnlyList<ChatMessage> Messages, string? BeforeCursor, bool HasOlder)> GetChatMessagesPageAsync(
        string chatId, int count, string? afterCursor, CancellationToken ct)
    {
        var size = Math.Clamp(count, 1, 24);
        var vars = new JsonObject
        {
            ["pagination"] = new JsonObject { ["first"] = size, ["after"] = afterCursor },
            ["filter"] = new JsonObject { ["chatId"] = chatId },
            ["hasSupportAccess"] = false,
            ["showForbiddenImage"] = true,
        };
        var json = await _gql.QueryAsync("chatMessages", vars, ct);
        var page = json["data"]?["chatMessages"];
        var edges = page?["edges"]?.AsArray() ?? new JsonArray();
        var messages = new List<ChatMessage>();
        foreach (var edge in edges)
            if (edge?["node"] is JsonNode node) messages.Add(ToMessage(node));

        var pageInfo = page?["pageInfo"];
        var cursor = pageInfo?["endCursor"]?.ToString();
        if (string.IsNullOrWhiteSpace(cursor)) cursor = edges.LastOrDefault()?["cursor"]?.ToString();
        // hasNextPage у PlayerOK иногда врёт, поэтому полная страница тоже считается признаком «есть ещё».
        // Если новых сообщений на следующей странице не окажется, интерфейс сам остановит подгрузку.
        var hasNext = pageInfo?["hasNextPage"]?.GetValue<bool>() ?? false;
        var hasOlder = (hasNext || edges.Count >= size) && !string.IsNullOrWhiteSpace(cursor);
        return (messages.OrderBy(m => m.CreatedAt ?? DateTimeOffset.MinValue)
            .ThenBy(m => m.Id, StringComparer.Ordinal).ToList(), cursor, hasOlder);
    }

    private static ChatMessage ToMessage(JsonNode m)
    {
        var urls = new List<string>();
        foreach (var img in m["images"]?.AsArray() ?? new JsonArray())
            if (img?["url"]?.ToString() is { Length: > 0 } u) urls.Add(u);
        if (m["file"]?["url"]?.ToString() is { Length: > 0 } fu && (m["file"]?["mime"]?.ToString() ?? "image").StartsWith("image")) urls.Add(fu);
        foreach (var link in m["imageLinks"]?.AsArray() ?? new JsonArray())
            if (link?.ToString() is { Length: > 0 } l) urls.Add(l);

        var deal = m["deal"];
        var item = deal?["item"] ?? m["item"];
        var itemImage = item?["attachments"]?.AsArray().FirstOrDefault()?["url"]?.ToString() ?? item?["attachment"]?["url"]?.ToString();
        var review = deal?["testimonial"];
        int? rating = review?["rating"] is JsonNode rn && int.TryParse(rn.ToString(), out var rv) ? rv : null;

        return new ChatMessage(
            m["id"]?.ToString() ?? "", m["user"]?["id"]?.ToString(), m["user"]?["username"]?.ToString(),
            m["user"]?["role"]?.ToString(), m["text"]?.ToString(),
            // У сообщений оплаты сам createdAt иногда отсутствует, но связанная транзакция содержит время платежа.
            ParseTime(m["createdAt"]) ?? ParseTime(m["transaction"]?["createdAt"]),
            deal?["id"]?.ToString() ?? m["transaction"]?["props"]?["dealId"]?.ToString(),
            urls.Count > 0 || (m["images"]?.AsArray().Count ?? 0) > 0,
            item?["name"]?.ToString(),
            urls.Distinct().ToList(), itemImage, item?["price"] is JsonNode pr ? (decimal?)Dec(pr) : null,
            deal?["user"]?["username"]?.ToString() ?? m["transaction"]?["user"]?["username"]?.ToString(),
            item?["user"]?["username"]?.ToString(), rating,
            review?["text"]?.ToString(), ParseTime(review?["createdAt"]), review?["creator"]?["username"]?.ToString());
    }

    /// <summary>Отправляет изображение (с необязательной подписью): загрузка во временное хранилище + сообщение.</summary>
    public async Task SendImageAsync(string chatId, byte[] image, string fileName, string? text, CancellationToken ct)
    {
        var upload = await _gql.UploadImageAsync(chatId, image, fileName, ct);
        var id = upload["data"]?["uploadChatImageIntoTemporaryStore"]?["id"]?.ToString()
                 ?? throw new PlayerokException("PlayerOK не вернул ID загруженного изображения.");
        var vars = new JsonObject
        {
            ["input"] = new JsonObject { ["chatId"] = chatId, ["imagesIds"] = new JsonArray(id), ["text"] = text ?? "" },
        };
        await _gql.MutateAsync("createChatMessage", vars, ct);
    }

    public async Task SendMessageAsync(string chatId, string text, CancellationToken ct)
    {
        var vars = new JsonObject
        {
            ["input"] = new JsonObject { ["chatId"] = chatId, ["imagesIds"] = new JsonArray(), ["text"] = text },
        };
        await _gql.MutateAsync("createChatMessage", vars, ct);
    }

    public async Task MarkChatReadAsync(string chatId, CancellationToken ct)
    {
        var vars = new JsonObject { ["input"] = new JsonObject { ["chatId"] = chatId } };
        await _gql.MutateAsync("markChatAsRead", vars, ct);
    }

    // ---------- Сделки ----------

    public async Task<Deal> UpdateDealStatusAsync(string dealId, DealStatus status, CancellationToken ct)
    {
        var name = status switch
        {
            DealStatus.Sent => "SENT",
            DealStatus.RolledBack => "ROLLED_BACK",
            _ => throw new ArgumentException("Поддерживаются только статусы «Выдано» и «Возврат».", nameof(status)),
        };
        var vars = new JsonObject { ["input"] = new JsonObject { ["id"] = dealId, ["status"] = name } };
        var json = await _gql.MutateAsync("updateDeal", vars, ct);
        var node = json["data"]?["updateDeal"] ?? throw new PlayerokException("PlayerOK не вернул обновлённую сделку.");
        return ToDeal(node);
    }

    public async Task<Deal?> GetDealAsync(string dealId, CancellationToken ct)
    {
        var vars = new JsonObject { ["id"] = dealId, ["hasSupportAccess"] = false, ["showForbiddenImage"] = true };
        var json = await _gql.QueryAsync("deal", vars, ct);
        return json["data"]?["deal"] is JsonNode node ? ToDeal(node) : null;
    }

    private Deal ToDeal(JsonNode d)
    {
        var item = d["item"];
        var review = d["testimonial"];
        return new Deal(
            d["id"]!.ToString(), ParseStatus(d["status"]?.ToString()),
            d["hasProblem"]?.GetValue<bool>() ?? false, item?["name"]?.ToString(), Dec(item?["price"]),
            d["user"]?["username"]?.ToString(), item?["user"]?["username"]?.ToString() ?? Username,
            d["chat"]?["id"]?.ToString(), d["commentFromBuyer"]?.ToString(),
            // createdAt — момент создания сделки/покупки; transaction.createdAt — запасной
            // timestamp платежа, если конкретный ответ PlayerOK не прислал createdAt сделки.
            ParseTime(d["createdAt"]) ?? ParseTime(d["transaction"]?["createdAt"]),
            review is not null, review is null ? null : (int?)Dec(review["rating"]), review?["text"]?.ToString(),
            item?["id"]?.ToString());
    }

    private static DealStatus ParseStatus(string? s) => s switch
    {
        "PAID" => DealStatus.Paid,
        "PENDING" => DealStatus.Pending,
        "SENT" => DealStatus.Sent,
        "CONFIRMED" => DealStatus.Confirmed,
        "CONFIRMED_AUTOMATICALLY" => DealStatus.ConfirmedAutomatically,
        "ROLLED_BACK" => DealStatus.RolledBack,
        _ => DealStatus.Unknown,
    };

    /// <summary>Все ваши продажи (direction OUT), страницами по 24.</summary>
    public async Task<IReadOnlyList<Deal>> GetAllSalesAsync(CancellationToken ct)
    {
        var deals = new List<Deal>();
        string? cursor = null;
        var seen = new HashSet<string>();
        for (var i = 0; i < 500; i++)
        {
            var vars = new JsonObject
            {
                ["pagination"] = new JsonObject { ["first"] = 24, ["after"] = cursor },
                ["filter"] = new JsonObject { ["userId"] = UserId, ["direction"] = "OUT", ["status"] = null },
                ["showForbiddenImage"] = true,
            };
            var json = await _gql.QueryAsync("deals", vars, ct);
            var page = json["data"]?["deals"];
            foreach (var edge in page?["edges"]?.AsArray() ?? new JsonArray())
                if (edge?["node"] is JsonNode node) deals.Add(ToDeal(node));

            var hasNext = page?["pageInfo"]?["hasNextPage"]?.GetValue<bool>() ?? false;
            cursor = page?["pageInfo"]?["endCursor"]?.ToString();
            if (!hasNext || cursor is null || !seen.Add(cursor)) break;
        }
        return deals.OrderByDescending(d => d.CreatedAt ?? DateTimeOffset.MinValue).ToList();
    }

    /// <summary>Последние продажи: только первые страницы (быстро, для догона пропущенных покупок).</summary>
    public async Task<IReadOnlyList<Deal>> GetRecentSalesAsync(int pages, CancellationToken ct)
    {
        var deals = new List<Deal>();
        string? cursor = null;
        for (var i = 0; i < Math.Max(1, pages); i++)
        {
            var vars = new JsonObject
            {
                ["pagination"] = new JsonObject { ["first"] = 24, ["after"] = cursor },
                ["filter"] = new JsonObject { ["userId"] = UserId, ["direction"] = "OUT", ["status"] = null },
                ["showForbiddenImage"] = true,
            };
            var json = await _gql.QueryAsync("deals", vars, ct);
            var page = json["data"]?["deals"];
            foreach (var edge in page?["edges"]?.AsArray() ?? new JsonArray())
                if (edge?["node"] is JsonNode node) deals.Add(ToDeal(node));

            var hasNext = page?["pageInfo"]?["hasNextPage"]?.GetValue<bool>() ?? false;
            cursor = page?["pageInfo"]?["endCursor"]?.ToString();
            if (!hasNext || cursor is null) break;
        }
        return deals.OrderByDescending(d => d.CreatedAt ?? DateTimeOffset.MinValue).ToList();
    }

    // ---------- Объявления ----------

    public async Task<IReadOnlyList<ListingSummary>> GetMyListingsAsync(CancellationToken ct)
    {
        var result = new List<ListingSummary>();
        string? cursor = null;
        var seen = new HashSet<string>();
        for (var i = 0; i < 100; i++)
        {
            var vars = new JsonObject
            {
                ["pagination"] = new JsonObject { ["first"] = 24, ["after"] = cursor },
                ["filter"] = new JsonObject
                {
                    ["userId"] = UserId,
                    ["status"] = new JsonArray(AllItemStatuses.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray()),
                },
                ["showForbiddenImage"] = true,
            };
            var json = await _gql.QueryAsync("items", vars, ct);
            var page = json["data"]?["items"];
            foreach (var edge in page?["edges"]?.AsArray() ?? new JsonArray())
                if (edge?["node"] is JsonNode n)
                    result.Add(ToListingSummary(n));

            var hasNext = page?["pageInfo"]?["hasNextPage"]?.GetValue<bool>() ?? false;
            cursor = page?["pageInfo"]?["endCursor"]?.ToString();
            if (!hasNext || cursor is null || !seen.Add(cursor)) break;
        }
        return result;
    }

    private static ListingSummary ToListingSummary(JsonNode n) => new(
        n["id"]?.ToString() ?? "",
        n["name"]?.ToString() ?? "Без названия",
        Dec(n["price"]),
        n["status"]?.ToString() ?? "",
        n["slug"]?.ToString(),
        (int)Dec(n["viewsCounter"]),
        (int)Dec(n["dealsCounter"]),
        n["game"]?["name"]?.ToString(),
        n["category"]?["name"]?.ToString(),
        ParseTime(n["createdAt"]),
        ParsePosition(n["priorityPosition"]));

    /// <summary>Место в выдаче: число или строка, значения меньше 1 считаются «нет места».</summary>
    private static int? ParsePosition(JsonNode? n)
    {
        if (n is null) return null;
        var v = Dec(n);
        return v >= 1 && v < 1_000_000 ? (int)v : null;
    }

    /// <summary>Актуальное место объявления в выдаче (запрос карточки объявления).</summary>
    public async Task<int?> GetItemPositionAsync(string itemId, CancellationToken ct)
    {
        var vars = new JsonObject
        {
            ["id"] = itemId,
            ["slug"] = null,
            ["hasSupportAccess"] = false,
            ["showForbiddenImage"] = true,
        };
        var json = await _gql.QueryAsync("item", vars, ct);
        return ParsePosition(json["data"]?["item"]?["priorityPosition"]);
    }

    /// <summary>Ищет игры и приложения в каталоге PlayerOK.</summary>
    public async Task<IReadOnlyList<GameChoice>> SearchGamesAsync(string name, CancellationToken ct)
    {
        var vars = new JsonObject
        {
            ["pagination"] = new JsonObject { ["first"] = 24, ["after"] = null },
            ["filter"] = new JsonObject { ["name"] = name, ["type"] = null },
        };
        var json = await _gql.QueryAsync("games", vars, ct);
        return (json["data"]?["games"]?["edges"]?.AsArray() ?? new JsonArray())
            .Where(edge => edge?["node"] is JsonObject)
            .Select(edge => new GameChoice(edge!["node"]!["id"]?.ToString() ?? "", edge["node"]!["name"]?.ToString() ?? ""))
            .Where(game => game.Id.Length > 0 && game.Name.Length > 0)
            .ToList();
    }

    /// <summary>Получает категории выбранной игры/приложения.</summary>
    public async Task<IReadOnlyList<GameCategoryChoice>> GetGameCategoriesAsync(string gameId, CancellationToken ct)
    {
        var vars = new JsonObject { ["id"] = gameId, ["slug"] = null };
        var json = await _gql.QueryAsync("GamePage", vars, ct);
        var categories = json["data"]?["game"]?["categories"]?.AsArray() ?? new JsonArray();
        return categories.Where(node => node is JsonObject).Select(node => new GameCategoryChoice(
            node!["id"]?.ToString() ?? "",
            node["name"]?.ToString() ?? "Без названия",
            (node["options"]?.AsArray() ?? new JsonArray())
                .Where(option => option is JsonObject)
                .Select(option => new GameCategoryOption(
                    option!["id"]?.ToString() ?? "",
                    option["group"]?.ToString() ?? "Характеристики",
                    option["label"]?.ToString() ?? option["value"]?.ToString() ?? "Вариант",
                    option["type"]?.ToString() ?? "SELECTOR",
                    option["field"]?.ToString() ?? "",
                    option["value"]?.ToString() ?? ""))
                .Where(option => option.Id.Length > 0 && option.Field.Length > 0)
                .ToList()))
            .Where(category => category.Id.Length > 0)
            .ToList();
    }

    /// <summary>Получает полную схему категории, включая характеристики выбора.</summary>
    public async Task<GameCategoryChoice?> GetGameCategoryDetailsAsync(string categoryId, CancellationToken ct)
    {
        var vars = new JsonObject { ["id"] = categoryId, ["gameId"] = null, ["slug"] = null };
        var json = await _gql.QueryAsync("GamePageCategory", vars, ct);
        var category = json["data"]?["gameCategory"];
        if (category is not JsonObject node) return null;
        var options = (node["options"]?.AsArray() ?? new JsonArray())
            .Where(option => option is JsonObject)
            .Select(option => new GameCategoryOption(
                option!["id"]?.ToString() ?? "",
                option["group"]?.ToString() ?? "Характеристики",
                option["label"]?.ToString() ?? option["value"]?.ToString() ?? "Вариант",
                option["type"]?.ToString() ?? "SELECTOR",
                option["field"]?.ToString() ?? "",
                option["value"]?.ToString() ?? ""))
            .Where(option => option.Id.Length > 0 && option.Field.Length > 0)
            .ToList();
        return new GameCategoryChoice(node["id"]?.ToString() ?? categoryId,
            node["name"]?.ToString() ?? "Категория", options);
    }

    /// <summary>Получает способы передачи выбранной категории.</summary>
    public async Task<IReadOnlyList<GameObtainingChoice>> GetObtainingTypesAsync(string categoryId, CancellationToken ct)
    {
        var vars = new JsonObject
        {
            ["pagination"] = new JsonObject { ["first"] = 24, ["after"] = null },
            ["filter"] = new JsonObject { ["gameCategoryId"] = categoryId },
        };
        var json = await _gql.QueryAsync("gameCategoryObtainingTypes", vars, ct);
        return (json["data"]?["gameCategoryObtainingTypes"]?["edges"]?.AsArray() ?? new JsonArray())
            .Where(edge => edge?["node"] is JsonObject)
            .Select(edge => new GameObtainingChoice(edge!["node"]!["id"]?.ToString() ?? "", edge["node"]!["name"]?.ToString() ?? ""))
            .Where(type => type.Id.Length > 0 && type.Name.Length > 0)
            .ToList();
    }

    /// <summary>Возвращает поля, которые продавец обязан/может заполнить для выбранного способа передачи.</summary>
    public async Task<IReadOnlyList<GameDataField>> GetItemDataFieldsAsync(
        string categoryId, string obtainingTypeId, CancellationToken ct)
    {
        var vars = new JsonObject
        {
            ["pagination"] = new JsonObject { ["first"] = 24, ["after"] = null },
            ["filter"] = new JsonObject
            {
                ["gameCategoryId"] = categoryId,
                ["obtainingTypeId"] = obtainingTypeId,
                ["type"] = null,
            },
        };
        var json = await _gql.QueryAsync("gameCategoryDataFields", vars, ct);
        return (json["data"]?["gameCategoryDataFields"]?["edges"]?.AsArray() ?? new JsonArray())
            .Where(edge => edge?["node"] is JsonObject)
            .Select(edge => edge!["node"]!)
            .Where(field => string.Equals(field["type"]?.ToString(), "ITEM_DATA", StringComparison.OrdinalIgnoreCase))
            .Select(field => new GameDataField(
                field["id"]?.ToString() ?? "",
                field["label"]?.ToString() ?? "Данные товара",
                field["inputType"]?.ToString() ?? "INPUT",
                field["required"]?.GetValue<bool>() ?? false))
            .Where(field => field.Id.Length > 0)
            .ToList();
    }

    /// <summary>Создаёт черновик с характеристиками, данными продавца и изображениями.</summary>
    public async Task<ListingSummary> CreateItemAsync(
        string categoryId, string obtainingTypeId, string name, int price, string description,
        IReadOnlyDictionary<string, string> attributes,
        IReadOnlyList<(string FieldId, string Value)> dataFields,
        IReadOnlyList<CreateItemAttachment> attachments, CancellationToken ct)
    {
        var attributeNode = new JsonObject();
        foreach (var (field, value) in attributes) attributeNode[field] = value;
        var dataFieldNodes = new JsonArray();
        foreach (var field in dataFields)
            dataFieldNodes.Add(new JsonObject { ["fieldId"] = field.FieldId, ["value"] = field.Value });
        var input = new JsonObject
        {
            ["gameCategoryId"] = categoryId,
            ["obtainingTypeId"] = obtainingTypeId,
            ["name"] = name,
            ["price"] = price,
            ["description"] = description,
            ["attributes"] = attributeNode,
            ["dataFields"] = dataFieldNodes,
        };
        var json = await _gql.CreateItemAsync(input, attachments, ct);
        return json["data"]?["createItem"] is JsonObject created
            ? ToListingSummary(created)
            : throw new PlayerokException("PlayerOK не вернул созданное объявление.");
    }

    /// <summary>Доступные тарифы поднятия/публикации для конкретного объявления.</summary>
    public async Task<IReadOnlyList<ItemPriorityStatus>> GetItemPriorityStatusesAsync(
        string itemId, decimal itemPrice, CancellationToken ct)
    {
        var vars = new JsonObject { ["itemId"] = itemId, ["price"] = decimal.ToInt32(decimal.Round(itemPrice)) };
        var json = await _gql.QueryAsync("itemPriorityStatuses", vars, ct);
        var rows = json["data"]?["itemPriorityStatuses"]?.AsArray() ?? new JsonArray();
        return rows.Where(x => x is not null).Select(x => new ItemPriorityStatus(
            x!["id"]?.ToString() ?? "", (int)Dec(x["price"]), x["name"]?.ToString() ?? "Приоритет",
            x["type"]?.ToString() ?? "", (int)Dec(x["period"]))
        ).Where(x => x.Id.Length > 0).ToList();
    }

    /// <summary>Поднимает активное объявление. Платёж проводится с локального баланса PlayerOK.</summary>
    public async Task IncreaseItemPriorityStatusAsync(
        string itemId, string priorityStatusId, CancellationToken ct)
    {
        var vars = new JsonObject
        {
            ["input"] = new JsonObject
            {
                ["itemId"] = itemId,
                ["priorityStatuses"] = new JsonArray(priorityStatusId),
                ["transactionProviderData"] = new JsonObject { ["paymentMethodId"] = null },
                ["transactionProviderId"] = "LOCAL",
            }
        };
        await _gql.MutateAsync("increaseItemPriorityStatus", vars, ct);
    }

    /// <summary>Публикует черновик/неопубликованное объявление с выбранным тарифом.</summary>
    public async Task PublishItemAsync(string itemId, string priorityStatusId, CancellationToken ct)
    {
        var vars = new JsonObject
        {
            ["input"] = new JsonObject
            {
                ["itemId"] = itemId,
                ["priorityStatuses"] = new JsonArray(priorityStatusId),
                ["transactionProviderId"] = "LOCAL",
            }
        };
        await _gql.MutateAsync("publishItem", vars, ct);
    }

    // ---------- Слушатель событий (опрос) ----------

    private readonly Dictionary<string, string> _lastMessageByChat = new();
    private readonly HashSet<string> _seenMessages = new();
    private readonly Queue<string> _seenOrder = new();
    private readonly HashSet<string> _processedDeals = new();
    private readonly Dictionary<string, (int Tries, DateTime Next)> _reviewWatch = new();
    private bool _primed;
    private DateTimeOffset _primedAt = DateTimeOffset.MinValue;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Каждый опрос отдаёт свежую первую страницу чатов (для автообновления списка в окне).</summary>
    public event Action<IReadOnlyList<ChatSummary>>? ChatsPolled;

    /// <summary>Ошибка опроса (сеть, проверка на бота и т.п.). Слушатель продолжает работать сам.</summary>
    public event Action<string>? PollError;

    public async IAsyncEnumerable<PlayerokEvent> ListenAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            List<PlayerokEvent> events;
            try
            {
                events = await PollOnceAsync(ct);
                failures = 0;
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                failures++;
                if (failures == 1 || failures % 10 == 0)
                    try { PollError?.Invoke($"Опрос PlayerOK не удался ({failures}): {ex.Message}"); } catch { }
                // Отмена во время паузы не должна бросать исключение наружу.
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(5 * failures, 60)), ct).ContinueWith(_ => { });
                continue; // ошибка опроса не должна останавливать слушатель
            }

            foreach (var e in events) yield return e;
            await Task.Delay(PollInterval, ct).ContinueWith(_ => { });
        }
    }

    private bool MarkSeen(string id)
    {
        if (!_seenMessages.Add(id)) return false;
        _seenOrder.Enqueue(id);
        while (_seenOrder.Count > 4000) _seenMessages.Remove(_seenOrder.Dequeue());
        return true;
    }

    private async Task<List<PlayerokEvent>> PollOnceAsync(CancellationToken ct)
    {
        var events = new List<PlayerokEvent>();
        var (nodes, _) = await FetchChatsPageAsync(null, 24, ct);

        try
        {
            ChatsPolled?.Invoke(nodes.Where(n => n is not null).Select(n => ToChatSummary(n!)).ToList());
        }
        catch (Exception logEx) { global::PlayerokBridge.Program.LogCrash("PlayerokClient.cs:618", logEx); /* обработчик интерфейса не должен ломать опрос */ }

        if (!_primed) // первый проход только запоминает состояние, старые сообщения не воспроизводим
        {
            foreach (var chat in nodes)
                if (chat?["id"]?.ToString() is { } id && chat["lastMessage"]?["id"]?.ToString() is { } last)
                {
                    _lastMessageByChat[id] = last;
                    MarkSeen(last);
                }
            _primed = true;
            _primedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            return events;
        }

        foreach (var chat in nodes)
        {
            var chatId = chat?["id"]?.ToString();
            var lastId = chat?["lastMessage"]?["id"]?.ToString();
            if (chatId is null || lastId is null) continue;

            var known = _lastMessageByChat.TryGetValue(chatId, out var prev);
            if (known && prev == lastId) continue;

            // Не сдвигаем контрольную точку до успешного получения истории: иначе временный
            // сетевой сбой мог бы навсегда пропустить сообщение или покупку.
            IReadOnlyList<ChatMessage> messages;
            try { messages = await GetChatMessagesAsync(chatId, 24, ct); }
            catch (OperationCanceledException) { throw; }
            catch (BotCheckException) { throw; }
            catch (UnauthorizedException) { throw; }
            catch (PlayerokException ex)
            {
                // Чат удалён/недоступен («не удалось найти в базе данных запись с указанным айди»):
                // раньше это валило весь опрос на каждом цикле. Теперь пропускаем только этот чат
                // и запоминаем его последнее сообщение, чтобы не дёргать его снова.
                _lastMessageByChat[chatId] = lastId;
                try { PollError?.Invoke($"Чат {chatId} пропущен: {ex.Message}"); } catch { }
                continue;
            }
            // Чат, которого не было в первой странице при старте, целиком не воспроизводим: берём только сообщения
            // за последние 15 минут (новый покупатель мог сразу после оплаты написать — {{ITEM_PAID}} не должен потеряться).
            var since = DateTimeOffset.UtcNow.AddMinutes(-15);
            List<ChatMessage> candidates;
            if (known)
            {
                // Только сообщения после последнего известного; иначе старые {{ITEM_PAID}} из истории
                // были бы приняты за новые покупки.
                var list = messages.ToList();
                var idx = list.FindIndex(m => m.Id == prev);
                candidates = idx >= 0
                    ? list.Skip(idx + 1).ToList()
                    : list.Where(m => m.CreatedAt is { } t && t >= _primedAt).ToList();
            }
            else candidates = messages.Where(m => m.CreatedAt is { } t && t >= since).ToList();
            if (!known && candidates.Count == 0 && messages.Count > 0) candidates = messages.TakeLast(1).ToList();
            var fresh = candidates.Where(m => !_seenMessages.Contains(m.Id)).ToList();
            var processedAll = true;
            foreach (var message in fresh)
            {
                try
                {
                    var converted = await ToEventsAsync(message, chatId, ct);
                    events.AddRange(converted);
                    MarkSeen(message.Id); // помечаем только после успешной обработки
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    processedAll = false;
                    try { PollError?.Invoke($"Не удалось обработать сообщение {message.Id} в чате {chatId}: {ex.Message}"); } catch { }
                }
            }
            if (processedAll) _lastMessageByChat[chatId] = lastId;
        }

        events.AddRange(await CheckReviewsAsync(ct));
        return events;
    }

    private async Task<List<PlayerokEvent>> ToEventsAsync(ChatMessage message, string chatId, CancellationToken ct)
    {
        var text = message.Text;
        if (string.IsNullOrEmpty(text))
            return message.HasImages ? new() { new NewMessageEvent(chatId, message with { Text = null }) } : new();

        async Task<Deal?> ActualDealAsync()
        {
            if (message.DealId is null) return null;
            // PlayerOK отдаёт свежие данные сделки не сразу: до трёх попыток.
            for (var attempt = 0; attempt < 3; attempt++)
            {
                await Task.Delay(attempt == 0 ? 1500 : 2500, ct);
                try
                {
                    var d = await GetDealAsync(message.DealId, ct);
                    if (d is not null) return d;
                }
                catch (PlayerokException) { /* повторим */ }
            }
            return null;
        }

        switch (text)
        {
            case "{{ITEM_PAID}}" or "{{NEW_DEAL}}" or "{{DEAL_PAID}}":
            {
                var deal = await ActualDealAsync();
                // Покупку нельзя терять: сообщение ITEM_PAID часто содержит точную дату,
                // даже когда отдельный запрос сделки вернул CreatedAt = null.
                var fromMessage = DealFromMessage(message, chatId);
                if (deal is null) deal = fromMessage;
                else if (fromMessage is not null)
                {
                    deal = deal with
                    {
                        Status = deal.Status == DealStatus.Unknown ? DealStatus.Paid : deal.Status,
                        ItemName = deal.ItemName ?? fromMessage.ItemName,
                        Price = deal.Price == 0m ? fromMessage.Price : deal.Price,
                        BuyerName = deal.BuyerName ?? fromMessage.BuyerName,
                        SellerName = deal.SellerName ?? fromMessage.SellerName,
                        ChatId = deal.ChatId ?? chatId,
                        CreatedAt = deal.CreatedAt ?? fromMessage.CreatedAt,
                    };
                }
                if (deal is null || !_processedDeals.Add(deal.Id)) return new();
                return new() { new NewDealEvent(chatId, deal) };
            }
            case "{{ITEM_SENT}}":
                return new(); // в боте для этого события нет уведомления
            case "{{DEAL_ROLLED_BACK}}":
                return await Wrap(ActualDealAsync(), d => new DealRolledBackEvent(chatId, d));
            case "{{DEAL_HAS_PROBLEM}}":
                return await Wrap(ActualDealAsync(), d => new DealHasProblemEvent(chatId, d));
            case "{{DEAL_PROBLEM_RESOLVED}}":
                return await Wrap(ActualDealAsync(), d => new DealProblemResolvedEvent(chatId, d));
        }

        if (text.StartsWith("{{DEAL_CONFIRMED", StringComparison.Ordinal) && text.EndsWith("}}", StringComparison.Ordinal))
        {
            var deal = await ActualDealAsync();
            if (deal is null) return new();
            _reviewWatch[deal.Id] = (0, DateTime.UtcNow.AddMinutes(2));
            return new() { new DealConfirmedEvent(chatId, deal) };
        }

        return new() { new NewMessageEvent(chatId, message) };
    }

    private Deal? DealFromMessage(ChatMessage m, string chatId)
    {
        if (string.IsNullOrEmpty(m.DealId)) return null;
        return new Deal(m.DealId, DealStatus.Paid, false, m.PurchasedItemName, m.ItemPrice ?? 0m,
            m.DealUserName, m.SellerName ?? Username, chatId, null, m.CreatedAt, false);
    }

    private static async Task<List<PlayerokEvent>> Wrap(Task<Deal?> dealTask, Func<Deal, PlayerokEvent> make)
    {
        var deal = await dealTask;
        return deal is null ? new() : new() { make(deal) };
    }

    /// <summary>После подтверждения сделки раз в 2 минуты проверяет, не появился ли отзыв (до суток).</summary>
    private async Task<List<PlayerokEvent>> CheckReviewsAsync(CancellationToken ct)
    {
        var events = new List<PlayerokEvent>();
        var now = DateTime.UtcNow;
        foreach (var (dealId, state) in _reviewWatch.ToList())
        {
            if (state.Next > now) continue;
            Deal? deal = null;
            try { deal = await GetDealAsync(dealId, ct); } catch (PlayerokException) { /* повторим позже */ }

            if (deal is { HasReview: true })
            {
                _reviewWatch.Remove(dealId);
                events.Add(new NewReviewEvent(deal.ChatId, deal, deal.ReviewRating, deal.ReviewText));
            }
            else if (state.Tries >= 720) _reviewWatch.Remove(dealId);
            else _reviewWatch[dealId] = (state.Tries + 1, now.AddMinutes(2));
        }
        return events;
    }

    // ---------- Утилиты ----------

    private static decimal Dec(JsonNode? n) =>
        n is not null && decimal.TryParse(n.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0m;

    [GeneratedRegex(@"(\.\d{6})\d+")]
    private static partial Regex FractionRegex();

    private static DateTimeOffset? ParseTime(JsonNode? n)
    {
        var s = n?.ToString();
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = FractionRegex().Replace(s, "$1");
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt) ? dt : null;
    }

    public void Dispose() => _gql.Dispose();
}
