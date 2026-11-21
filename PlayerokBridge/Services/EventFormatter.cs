using System.Net;
using System.Text.RegularExpressions;
using PlayerokBridge.Playerok;

namespace PlayerokBridge.Services;

public sealed record Formatted(string Html, string? ChatId, string Category);

/// <summary>Порт readable_system_message / classify_message / format_event из bot.py.</summary>
public static class EventFormatter
{
    private static readonly Dictionary<string, string> SystemMessageMap = new()
    {
        ["{{DEAL_CONFIRMED}}"] = "✅ Покупатель подтвердил сделку",
        ["{{DEAL_CONFIRMED_AUTOMATICALLY}}"] = "✅ Сделка подтверждена автоматически",
        ["{{DEAL_ROLLED_BACK}}"] = "↩️ Вы сделали возврат средств",
        ["{{DEAL_REFUNDED}}"] = "↩️ Вы сделали возврат средств",
        ["{{DEAL_ROLLED_BACK_BY_SUPPORT}}"] = "🔰 Поддержка сделала возврат средств",
        ["{{DEAL_REFUNDED_BY_SUPPORT}}"] = "🔰 Поддержка сделала возврат средств",
        ["{{DEAL_CONFIRMED_BY_BUYER}}"] = "✅ Покупатель подтвердил получение",
        ["{{DEAL_HAS_PROBLEM}}"] = "⚠️ Покупатель сообщил о проблеме",
        ["{{DEAL_PROBLEM_RESOLVED}}"] = "🔰 Поддержка решила проблему",
        ["{{NEW_DEAL}}"] = "🛒 Новая оплаченная сделка",
        ["{{DEAL_PAID}}"] = "🛒 Сделка оплачена",
        ["{{DEAL_CREATED}}"] = "🛒 Создана новая сделка",
        ["{{DEAL_COMPLETED}}"] = "✅ Сделка завершена",
        ["{{DEAL_CANCELED}}"] = "↩️ Сделка отменена",
        ["{{DEAL_CANCELLED}}"] = "↩️ Сделка отменена",
        ["{{NEW_REVIEW}}"] = "📝 Получен новый отзыв",
        ["{{ITEM_PAID}}"] = "💳 Покупатель оплатил товар",
        ["{{ITEM_SENT}}"] = "📦 Товар отправлен покупателю",
    };

    private static readonly (Regex Pattern, string Replacement)[] MarkerPatterns =
        SystemMessageMap.Select(kv => (new Regex(Regex.Escape(kv.Key), RegexOptions.IgnoreCase | RegexOptions.Compiled), kv.Value))
            .ToArray();

    private static readonly Regex UnknownMarker = new(@"\{\{[^{}]+\}\}", RegexOptions.Compiled);

    private static readonly (Regex Pattern, string Replacement)[] BuyerEventPatterns =
    {
        (new(@"^.+?\s+(оплатил покупку)$", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Покупатель $1"),
        (new(@"^.+?\s+(сообщил о проблеме)$", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Покупатель $1"),
        (new(@"^.+?\s+(подтвердил получение)$", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Покупатель $1"),
    };

    private static readonly Regex SupportRefund =
        new(@"^🔰?\s*поддержка\s+сделала\s+возврат\s+средств$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex OwnRefund =
        new(@"^вы\s+сделали\s+возврат\s+средств$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AnyBraces = new(@"\{[^{}]*\}", RegexOptions.Compiled);

    /// <summary>Служебное сообщение PlayerOK: шаблон в фигурных скобках ({{ITEM_SENT}} и т.п.).</summary>
    public static bool IsSystemMarker(string? text)
    {
        var raw = (text ?? "").Trim();
        if (raw.Length == 0 || !AnyBraces.IsMatch(raw)) return false;
        if (raw.StartsWith("{{")) return true;
        var leftover = AnyBraces.Replace(raw.Replace("{{", "{").Replace("}}", "}"), "").Trim(' ', '\n', '\t', '.', ',', ';', ':', '-', '—');
        return leftover.Length == 0;
    }

    public static string Esc(object? value) => value is null ? "—" : WebUtility.HtmlEncode(value.ToString()) ?? "";

    /// <summary>Переводит известные маркеры PlayerOK в читаемый текст и скрывает имена в системных событиях.</summary>
    public static string ReadableSystemMessage(string? text)
    {
        var stripped = (text ?? "").Trim();
        if (SystemMessageMap.TryGetValue(stripped, out var direct)) return direct;

        foreach (var (pattern, replacement) in MarkerPatterns) stripped = pattern.Replace(stripped, replacement);

        if (stripped.Contains("{{") && stripped.Contains("}}"))
        {
            stripped = UnknownMarker.Replace(stripped, "").Trim();
            if (stripped.Length == 0) return "ℹ️ Событие сделки обновлено в PlayerOK.";
        }

        foreach (var (pattern, replacement) in BuyerEventPatterns) stripped = pattern.Replace(stripped, replacement);

        if (SupportRefund.IsMatch(stripped)) return "🔰 Поддержка сделала возврат средств";
        if (OwnRefund.IsMatch(stripped)) return "↩️ Вы сделали возврат средств";
        return stripped;
    }

    public static MessageKind Classify(ChatMessage msg, string? myUserId)
    {
        if (msg.AuthorId is not null && msg.AuthorId == myUserId) return MessageKind.Own;

        var raw = (msg.Text ?? "").Trim();
        var role = (msg.AuthorRole ?? "").ToUpperInvariant();
        if (role is "MODERATOR" or "CHECKER" || role.Contains("SUPPORT")) return MessageKind.Support;

        var hasMarker = raw.Contains("{{") && raw.Contains("}}");
        if (msg.AuthorId is null || role == "BOT" || hasMarker)
        {
            if (raw.ToUpperInvariant().Contains("SUPPORT") ||
                ReadableSystemMessage(raw).Contains("поддержк", StringComparison.OrdinalIgnoreCase))
                return MessageKind.Support;
            return MessageKind.System;
        }
        return MessageKind.Buyer;
    }

    public static string DealSummary(Deal deal)
    {
        var lines = new List<string>
        {
            $"📦 <b>{Esc(deal.ItemName)}</b>",
            $"💰 {Esc(deal.Price)} ₽",
            $"👤 {Esc(deal.BuyerName)}",
            $"🆔 сделка <code>{Esc(deal.Id)}</code>",
        };
        if (!string.IsNullOrEmpty(deal.BuyerComment)) lines.Add($"💬 {Esc(deal.BuyerComment)}");
        return string.Join("\n", lines);
    }

    /// <summary>Возвращает готовое HTML-уведомление или null, если событие присылать не нужно.</summary>
    public static Formatted? Format(PlayerokEvent e, string? myUserId)
    {
        switch (e)
        {
            case NewMessageEvent nm:
            {
                var kind = Classify(nm.Message, myUserId);
                if (kind == MessageKind.Own) return null; // свои сообщения не дублируем

                var text = ReadableSystemMessage(nm.Message.Text ?? "[вложение / без текста]");
                var author = nm.Message.AuthorName;
                switch (kind)
                {
                    case MessageKind.Support:
                        return text.StartsWith("🔰")
                            ? new Formatted(Esc(text), nm.ChatId, "msg_support")
                            : new Formatted($"🔰 <b>{Esc(author ?? "Поддержка")}</b>:\n{Esc(text)}", nm.ChatId, "msg_support");
                    case MessageKind.System:
                        return new Formatted(Esc(text), nm.ChatId, "msg_system");
                    default:
                        return new Formatted(
                            $"💬 <b>{Esc(author ?? "Покупатель")}</b>:\n{Esc(text)}\n\n" +
                            "<i>Ответьте на это сообщение (Reply), чтобы отправить ответ на PlayerOK.</i>",
                            nm.ChatId, "msg_buyer");
                }
            }
            case NewDealEvent d:
                return new($"🛒 <b>Новая оплаченная сделка</b>\n{DealSummary(d.Deal)}", d.ChatId, "deal_new");
            case DealConfirmedEvent d:
                return new($"✅ <b>Покупатель подтвердил сделку</b>\n{DealSummary(d.Deal)}", d.ChatId, "deal_confirmed");
            case DealRolledBackEvent d:
                return new($"↩️ <b>Возврат средств по сделке</b>\n{DealSummary(d.Deal)}", d.ChatId, "deal_refund");
            case DealHasProblemEvent d:
                return new($"⚠️ <b>Проблема в сделке</b>\n{DealSummary(d.Deal)}", d.ChatId, "deal_problem");
            case DealProblemResolvedEvent d:
                return new($"🟢 <b>Проблема в сделке решена</b>\n{DealSummary(d.Deal)}", d.ChatId, "deal_problem");
            case NewReviewEvent r:
            {
                var extra = r.Rating is null && r.ReviewText is null ? "" : $"\n⭐ {Esc(r.Rating)}\n{Esc(r.ReviewText)}";
                return new($"📝 <b>Новый отзыв</b>\n{DealSummary(r.Deal)}{extra}", r.ChatId, "review");
            }
            default:
                return null;
        }
    }
}
