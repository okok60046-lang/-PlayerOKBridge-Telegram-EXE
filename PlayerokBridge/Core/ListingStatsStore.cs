namespace PlayerokBridge.Core;

/// <summary>
/// Локальная история просмотров объявлений. PlayerOK отдаёт накопительный счётчик,
/// поэтому интервальные изменения можно достоверно считать только после первого снимка.
/// </summary>
public sealed class ListingStatsStore
{
    public const string FileName = "listing_views_history.json";

    public sealed record ViewPoint(DateTimeOffset At, int Count);

    private readonly object _gate = new();
    private Dictionary<string, List<ViewPoint>> _history;

    public ListingStatsStore()
    {
        try { _history = JsonStore.Load<Dictionary<string, List<ViewPoint>>>(FileName) ?? new(); }
        catch { _history = new(); }
    }

    public void Capture(IEnumerable<PlayerokBridge.Playerok.ListingSummary> listings, DateTimeOffset now)
    {
        lock (_gate)
        {
            var changed = false;
            var activeIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in listings)
            {
                if (string.IsNullOrWhiteSpace(item.Id)) continue;
                activeIds.Add(item.Id);
                if (!_history.TryGetValue(item.Id, out var points))
                    _history[item.Id] = points = new List<ViewPoint>();

                if (points.Count > 0 && now - points[^1].At < TimeSpan.FromMinutes(10))
                    continue;

                points.Add(new ViewPoint(now, Math.Max(0, item.ViewsCounter)));
                changed = true;
                // 10-минутный шаг и 5 000 записей покрывают более месяца даже при частых обновлениях.
                points.RemoveAll(p => p.At < now.AddDays(-45));
                if (points.Count > 5_000) points.RemoveRange(0, points.Count - 5_000);
            }

            // Удалённые объявления храним ограниченное время, затем вычищаем их ключи.
            // Это не позволяет JSON расти бесконечно при смене ассортимента.
            var staleIds = _history
                .Where(pair => pair.Value.Count == 0 || (!activeIds.Contains(pair.Key) && pair.Value[^1].At < now.AddDays(-90)))
                .Select(pair => pair.Key).ToList();
            foreach (var id in staleIds)
            {
                _history.Remove(id);
                changed = true;
            }

            // Защита от аномально большого архива: сохраняем 10 000 наиболее недавно обновлявшихся лотов.
            if (_history.Count > 10_000)
            {
                foreach (var id in _history.OrderBy(pair => pair.Value.Count == 0 ? DateTimeOffset.MinValue : pair.Value[^1].At)
                             .Take(_history.Count - 10_000).Select(pair => pair.Key).ToList())
                    _history.Remove(id);
                changed = true;
            }

            // Файл переписываем только при изменениях.
            if (!changed) return;
            try { JsonStore.Save(FileName, _history); } catch (Exception logEx) { global::PlayerokBridge.Program.LogCrash("ListingStatsStore.cs:45", logEx); /* История не должна ломать страницу товаров. */ }
        }
    }

    /// <summary>Возвращает прирост после первого из двух снимков в диапазоне; null означает, что истории пока недостаточно.</summary>
    public int? GetViewsSince(string listingId, int currentCount, DateTimeOffset since)
    {
        lock (_gate)
        {
            if (!_history.TryGetValue(listingId, out var points) || points.Count == 0) return null;
            var inRange = points.Where(p => p.At >= since).ToList();
            // Одной точки недостаточно, чтобы посчитать прирост за интервал.
            if (inRange.Count < 2) return null;
            return Math.Max(0, currentCount - inRange[0].Count);
        }
    }
}
