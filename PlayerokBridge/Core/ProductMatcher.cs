namespace PlayerokBridge.Core;

public sealed record ProductReply(string Keyword, string Text)
{
    /// <summary>Первое ключевое слово — заголовок записи.</summary>
    public string Title
    {
        get
        {
            var first = Keyword.Split(',')[0].Trim();
            return first.Length == 0 ? "товар" : first;
        }
    }
}

public sealed record MatchResult(int PartCount, int TotalLength, int Index, string Alias);

/// <summary>
/// Подбор автоответа по названию лота. Ключевое слово может состоять из частей через «+»:
/// все части должны встречаться в названии. Побеждает самое точное совпадение
/// (больше частей, затем больше символов), при равенстве — запись выше в списке.
/// </summary>
public static class ProductMatcher
{
    private static IEnumerable<string> Aliases(ProductReply entry) =>
        entry.Keyword.Split(',')
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => TextNorm.Normalize(a).Trim());

    private static (int parts, int len)? Score(string alias, string name)
    {
        var parts = alias.Split('+').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        if (parts.Count == 0 || !parts.All(name.Contains)) return null;
        return (parts.Count, parts.Sum(p => p.Length));
    }

    public static List<MatchResult> Match(IReadOnlyList<ProductReply> replies, string? itemName)
    {
        var name = TextNorm.Normalize(itemName);
        var found = new List<MatchResult>();
        if (name.Length == 0) return found;

        for (var i = 0; i < replies.Count; i++)
        {
            MatchResult? best = null;
            foreach (var alias in Aliases(replies[i]))
            {
                var score = Score(alias, name);
                if (score is null) continue;
                var (parts, len) = score.Value;
                if (best is null || (parts, len).CompareTo((best.PartCount, best.TotalLength)) > 0)
                    best = new MatchResult(parts, len, i, alias);
            }
            if (best is not null) found.Add(best);
        }

        return found
            .OrderByDescending(m => m.PartCount)
            .ThenByDescending(m => m.TotalLength)
            .ThenBy(m => m.Index)
            .ToList();
    }

    public static int? FindIndex(IReadOnlyList<ProductReply> replies, string? itemName)
    {
        var m = Match(replies, itemName);
        return m.Count > 0 ? m[0].Index : null;
    }
}
