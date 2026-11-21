namespace PlayerokBridge.Playerok;

public enum DealStatus { Unknown, Paid, Pending, Sent, Confirmed, ConfirmedAutomatically, RolledBack }

public enum MessageKind { Own, Support, System, Buyer }

public sealed record Balance(decimal Value, decimal Available, decimal Frozen, decimal Withdrawable, decimal PendingIncome);

public sealed record ChatMessage(
    string Id, string? AuthorId, string? AuthorName, string? AuthorRole,
    string? Text, DateTimeOffset? CreatedAt, string? DealId = null, bool HasImages = false,
    string? PurchasedItemName = null,
    IReadOnlyList<string>? ImageUrls = null, string? ItemImageUrl = null, decimal? ItemPrice = null,
    string? DealUserName = null, string? SellerName = null, int? Rating = null,
    string? ReviewText = null, DateTimeOffset? ReviewAt = null, string? ReviewerName = null);

public sealed record ChatSummary(
    string Id, string PartnerName, string? LastText, int Unread, DateTimeOffset? LastAt,
    Deal? Deal = null);

public sealed record Deal(
    string Id, DealStatus Status, bool HasProblem, string? ItemName, decimal Price,
    string? BuyerName, string? SellerName, string? ChatId, string? BuyerComment,
    DateTimeOffset? CreatedAt, bool HasReview, int? ReviewRating = null, string? ReviewText = null,
    string? ItemId = null);

public sealed record ListingSummary(
    string Id, string Name, decimal Price, string StatusName, string? Slug,
    int ViewsCounter = 0, int DealsCounter = 0, string? GameName = null,
    string? CategoryName = null, DateTimeOffset? CreatedAt = null, int? PriorityPosition = null);

public sealed record ItemPriorityStatus(string Id, int Price, string Name, string Type, int Period);

public sealed record GameChoice(string Id, string Name);
public sealed record GameCategoryChoice(string Id, string Name, IReadOnlyList<GameCategoryOption> Options);
public sealed record GameCategoryOption(string Id, string Group, string Label, string Type, string Field, string Value);
public sealed record GameObtainingChoice(string Id, string Name);
public sealed record GameDataField(string Id, string Label, string Type, bool Required);
public sealed record CreateItemAttachment(string FileName, byte[] Bytes);

/// <summary>Событие из слушателя PlayerOK.</summary>
public abstract record PlayerokEvent(string? ChatId);
public sealed record NewMessageEvent(string? ChatId, ChatMessage Message) : PlayerokEvent(ChatId);
public sealed record NewDealEvent(string? ChatId, Deal Deal) : PlayerokEvent(ChatId);
public sealed record DealConfirmedEvent(string? ChatId, Deal Deal) : PlayerokEvent(ChatId);
public sealed record DealRolledBackEvent(string? ChatId, Deal Deal) : PlayerokEvent(ChatId);
public sealed record DealHasProblemEvent(string? ChatId, Deal Deal) : PlayerokEvent(ChatId);
public sealed record DealProblemResolvedEvent(string? ChatId, Deal Deal) : PlayerokEvent(ChatId);
public sealed record NewReviewEvent(string? ChatId, Deal Deal, int? Rating, string? ReviewText) : PlayerokEvent(ChatId);
