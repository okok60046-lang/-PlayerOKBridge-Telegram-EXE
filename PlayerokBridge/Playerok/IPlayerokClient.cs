namespace PlayerokBridge.Playerok;

/// <summary>
/// Всё, что приложению нужно от PlayerOK. Остальной код зависит только от этого интерфейса,
/// поэтому реальный GraphQL-клиент можно заменять и дорабатывать независимо.
/// </summary>
public interface IPlayerokClient
{
    string? UserId { get; }
    string? Username { get; }

    Task ConnectAsync(CancellationToken ct);
    Task<Balance> GetBalanceAsync(CancellationToken ct);

    Task<IReadOnlyList<ChatSummary>> GetAllChatsAsync(CancellationToken ct);
    Task<IReadOnlyList<ChatMessage>> GetChatMessagesAsync(string chatId, int count, CancellationToken ct);
    Task SendMessageAsync(string chatId, string text, CancellationToken ct);
    Task SendImageAsync(string chatId, byte[] image, string fileName, string? text, CancellationToken ct);
    Task MarkChatReadAsync(string chatId, CancellationToken ct);

    /// <summary>Меняет статус сделки: SENT — подтвердить выдачу, ROLLED_BACK — возврат средств покупателю.</summary>
    Task<Deal> UpdateDealStatusAsync(string dealId, DealStatus status, CancellationToken ct);

    /// <summary>Все ваши продажи (direction OUT).</summary>
    Task<IReadOnlyList<Deal>> GetAllSalesAsync(CancellationToken ct);

    Task<IReadOnlyList<ListingSummary>> GetMyListingsAsync(CancellationToken ct);

    /// <summary>Бесконечный поток событий; реализация сама переподключается при сбоях.</summary>
    IAsyncEnumerable<PlayerokEvent> ListenAsync(CancellationToken ct);
}
