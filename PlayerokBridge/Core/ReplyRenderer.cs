using System.Text.RegularExpressions;

namespace PlayerokBridge.Core;

public sealed record DealInfo(
    string? Id,
    string? BuyerName,
    string? SellerName,
    string? ItemName,
    decimal? Price,
    string? ChatId);

public static class ReplyRenderer
{
    private static readonly Regex Placeholder = new(@"\{(\w+)\}", RegexOptions.Compiled);

    /// <summary>Подставляет {buyer}, {seller}, {item}, {price}, {deal_id}; неизвестные поля остаются как есть.</summary>
    public static string Render(string text, DealInfo deal, string? fallbackSeller = null)
    {
        // Как в bot.py: запасные слова вместо пустоты, а известные поля без значения заменяются пустой строкой.
        // Раньше покупателю уходил буквальный текст «Здравствуйте, {buyer}!», если ник не пришёл.
        var values = new Dictionary<string, string>
        {
            ["buyer"] = !string.IsNullOrWhiteSpace(deal.BuyerName) ? deal.BuyerName! : "покупатель",
            ["seller"] = !string.IsNullOrWhiteSpace(deal.SellerName) ? deal.SellerName!
                         : !string.IsNullOrWhiteSpace(fallbackSeller) ? fallbackSeller! : "продавец",
            ["item"] = deal.ItemName ?? "",
            ["price"] = deal.Price?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) ?? "",
            ["deal_id"] = deal.Id ?? "",
        };
        return Placeholder.Replace(text, m =>
            values.TryGetValue(m.Groups[1].Value.ToLowerInvariant(), out var v) ? v : m.Value);
    }
}
