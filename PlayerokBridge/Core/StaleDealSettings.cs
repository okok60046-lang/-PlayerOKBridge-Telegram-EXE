using System.Text.Json.Serialization;

namespace PlayerokBridge.Core;

/// <summary>Напоминание о «зависшей» сделке: оплачена, но товар долго не выдан.</summary>
public sealed class StaleDealSettings
{
    public const string FileName = "stale_deal_settings.json";

    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    /// <summary>Через сколько минут после оплаты считать сделку зависшей.</summary>
    [JsonPropertyName("minutes")] public int Minutes { get; set; } = 15;
    /// <summary>Как часто повторять напоминание по той же сделке (0 — не повторять).</summary>
    [JsonPropertyName("repeat_minutes")] public int RepeatMinutes { get; set; } = 30;

    public static StaleDealSettings Load()
    {
        try
        {
            var s = JsonStore.Load<StaleDealSettings>(FileName) ?? new StaleDealSettings();
            s.Minutes = Math.Clamp(s.Minutes, 1, 24 * 60);
            s.RepeatMinutes = Math.Clamp(s.RepeatMinutes, 0, 24 * 60);
            return s;
        }
        catch { return new StaleDealSettings(); }
    }

    public void Save() => JsonStore.Save(FileName, this);
}

/// <summary>Оплаченная сделка, по которой товар не выдан дольше заданного времени.</summary>
public sealed record StaleDealInfo(PlayerokBridge.Playerok.Deal Deal, TimeSpan Age);
