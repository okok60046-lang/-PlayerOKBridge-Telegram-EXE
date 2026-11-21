using PlayerokBridge.Core;
using PlayerokBridge.Playerok;

namespace PlayerokBridge.Services;

/// <summary>
/// Автоответ по купленному товару и просьба об отзыве после подтверждения сделки
/// (порт auto_reply_for_deal и review_request_for_deal). Каждое действие выполняется один раз на сделку.
/// </summary>
public sealed class AutoReplyService
{
    private readonly IPlayerokClient _client;
    private readonly ProductReplyStore _products;
    private readonly ReviewSettings _review;
    private readonly AfkSettings _afk;
    private readonly NotifySettings _notify;
    private readonly Func<string, string?, Task> _report; // (текст, chatId) -> уведомление админу

    private const string RepliedFile = "autoreplied_deals.json";

    private readonly HashSet<string> _autoReplied = new();
    private readonly List<string> _repliedOrder = new();
    private readonly HashSet<string> _reviewRequested = new();
    private readonly object _lock = new();

    public AutoReplyService(
        IPlayerokClient client, ProductReplyStore products, ReviewSettings review, AfkSettings afk,
        NotifySettings notify, Func<string, string?, Task> report)
    {
        _client = client; _products = products; _review = review; _afk = afk; _notify = notify; _report = report;
        try
        {
            // Уже обработанные сделки переживают перезапуск приложения — повторных автоответов не будет.
            foreach (var id in JsonStore.Load<List<string>>(RepliedFile) ?? new List<string>())
                if (_autoReplied.Add(id)) _repliedOrder.Add(id);
        }
        catch { /* повреждённый файл — начинаем с пустого списка */ }
    }

    public bool WasReplied(string dealId)
    {
        lock (_lock) return _autoReplied.Contains(dealId);
    }

    public void MarkReplied(string dealId)
    {
        lock (_lock)
        {
            if (!_autoReplied.Add(dealId)) return;
            _repliedOrder.Add(dealId);
            while (_repliedOrder.Count > 3000) { _autoReplied.Remove(_repliedOrder[0]); _repliedOrder.RemoveAt(0); }
            try { JsonStore.Save(RepliedFile, _repliedOrder); } catch (Exception logEx) { global::PlayerokBridge.Program.LogCrash("AutoReplyService.cs:52", logEx); /* не критично */ }
        }
    }

    private static DealInfo ToInfo(Deal d) =>
        new(d.Id, d.BuyerName, d.SellerName, d.ItemName, d.Price, d.ChatId);

    public async Task OnNewDealAsync(NewDealEvent e, CancellationToken ct)
    {
        var deal = e.Deal;
        lock (_lock) if (_autoReplied.Contains(deal.Id)) return;

        var chatId = e.ChatId ?? deal.ChatId;
        if (string.IsNullOrEmpty(chatId))
        {
            await _report($"⚠️ Автоответ не отправлен: у сделки {deal.Id} нет чата с покупателем. Ответьте вручную.", null);
            return;
        }

        var idx = ProductMatcher.FindIndex(_products.Items, deal.ItemName);
        if (idx is null)
        {
            MarkReplied(deal.Id); // нет подходящего ключевого слова — повторять нечего
            await _report($"ℹ️ Автоответ не сработал: название лота «{deal.ItemName}» не подошло ни под одно ключевое слово. Ответьте покупателю вручную.", chatId);
            // Покупатель всё равно должен узнать, что вас нет на месте.
            if (AfkActive && _afk.SendWithoutProduct) await SendAfkAsync(deal, chatId, 0, ct);
            return;
        }

        var entry = _products.Items[idx.Value];
        var text = ReplyRenderer.Render(entry.Text, ToInfo(deal), _client.Username);
        try
        {
            await _client.SendMessageAsync(chatId, text, ct);
            MarkReplied(deal.Id);
            if (_notify.IsOn("autoreply"))
                await _report($"🤖 Автоответ отправлен («{entry.Title}»), покупатель {deal.BuyerName}:\n{text}", chatId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Ошибки показываются всегда, успешные отчёты — по настройке.
            await _report($"❌ Не удалось отправить автоответ по сделке {deal.Id}: {ex.Message}", chatId);
            return;
        }

        // Порядок: сначала ответ по товару, потом (с паузой) AFK-сообщение.
        if (AfkActive) await SendAfkAsync(deal, chatId, _afk.DelaySeconds, ct);
    }

    private bool AfkActive => _afk.Enabled && !string.IsNullOrWhiteSpace(_afk.Text);

    private async Task SendAfkAsync(Deal deal, string chatId, double delaySeconds, CancellationToken ct)
    {
        try
        {
            if (delaySeconds > 0) await Task.Delay(TimeSpan.FromSeconds(Math.Min(delaySeconds, 60)), ct);
            var text = ReplyRenderer.Render(_afk.Text, ToInfo(deal), _client.Username);
            await _client.SendMessageAsync(chatId, text, ct);
            if (_notify.IsOn("autoreply"))
                await _report($"🌙 AFK-сообщение отправлено, покупатель {deal.BuyerName}:\n{text}", chatId);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            await _report($"❌ Не удалось отправить AFK-сообщение по сделке {deal.Id}: {ex.Message}", chatId);
        }
    }

    public async Task OnDealConfirmedAsync(DealConfirmedEvent e, CancellationToken ct)
    {
        if (!_review.Enabled) return;
        var deal = e.Deal;
        var chatId = e.ChatId ?? deal.ChatId;
        if (string.IsNullOrEmpty(chatId) || deal.HasReview) return;
        lock (_lock) if (_reviewRequested.Contains(deal.Id)) return;

        var text = ReplyRenderer.Render(_review.Text, ToInfo(deal), _client.Username);
        try
        {
            await _client.SendMessageAsync(chatId, text, ct);
            lock (_lock) _reviewRequested.Add(deal.Id);
            if (_notify.IsOn("autoreply"))
                await _report($"⭐ Просьба об отзыве отправлена:\n{text}", chatId);
        }
        catch (Exception ex)
        {
            await _report($"❌ Не удалось отправить просьбу об отзыве по сделке {deal.Id}: {ex.Message}", chatId);
        }
    }
}
