using PlayerokBridge.Playerok;

namespace PlayerokBridge.Core;

/// <summary>
/// Сохраняет фактическую дату появления покупки. Иногда API возвращает сделку без createdAt;
/// тогда дата первого события покупки сохраняется локально и используется аналитикой.
/// </summary>
public sealed class SalesHistoryStore
{
    public const string FileName = "sales_purchase_dates.json";

    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _purchaseDates;

    public SalesHistoryStore()
    {
        try
        {
            _purchaseDates = JsonStore.Load<Dictionary<string, DateTimeOffset>>(FileName)
                ?? new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        }
        catch
        {
            _purchaseDates = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        }
    }

    /// <summary>Обогащает сделки датами; серверная дата имеет приоритет над локальной.</summary>
    public IReadOnlyList<Deal> Enrich(IEnumerable<Deal> deals)
    {
        lock (_gate)
        {
            var changed = false;
            var result = new List<Deal>();
            foreach (var deal in deals)
            {
                if (string.IsNullOrWhiteSpace(deal.Id))
                {
                    result.Add(deal);
                    continue;
                }

                DateTimeOffset at;
                if (deal.CreatedAt is { } serverDate)
                {
                    at = serverDate;
                    if (!_purchaseDates.TryGetValue(deal.Id, out var saved) || saved != serverDate)
                    {
                        _purchaseDates[deal.Id] = serverDate;
                        changed = true;
                    }
                }
                else if (_purchaseDates.TryGetValue(deal.Id, out var knownDate))
                {
                    at = knownDate;
                }
                else
                {
                    // Не подменяем неизвестную дату старой сделки текущим временем:
                    // это искусственно нарисовало бы все старые покупки сегодняшним днём.
                    result.Add(deal);
                    continue;
                }

                result.Add(deal with { CreatedAt = at });
            }

            if (changed) Persist();
            return result;
        }
    }

    /// <summary>Фиксирует момент события новой покупки, если API не вернул дату сделки.</summary>
    public Deal CapturePurchase(Deal deal, DateTimeOffset observedAt)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(deal.Id))
                return deal with { CreatedAt = deal.CreatedAt ?? observedAt };

            var at = deal.CreatedAt
                     ?? (_purchaseDates.TryGetValue(deal.Id, out var known) ? known : observedAt);
            if (!_purchaseDates.TryGetValue(deal.Id, out var saved) || saved != at)
            {
                _purchaseDates[deal.Id] = at;
                Persist();
            }
            return deal with { CreatedAt = at };
        }
    }

    private void Persist()
    {
        try { JsonStore.Save(FileName, _purchaseDates); }
        catch (Exception ex) { global::PlayerokBridge.Program.LogCrash("SalesHistoryStore.Persist", ex); }
    }
}
