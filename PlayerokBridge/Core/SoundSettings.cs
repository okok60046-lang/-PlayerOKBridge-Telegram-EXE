namespace PlayerokBridge.Core;

/// <summary>Настройки звука: звуки событий и фоновая музыка. Хранятся в sound_settings.json.</summary>
public sealed class SoundSettings
{
    public const string FileName = "sound_settings.json";

    /// <summary>Общий выключатель звуков событий (покупка, сообщения, уведомления).</summary>
    public bool Enabled { get; set; } = true;
    public bool Purchase { get; set; } = true;
    /// <summary>Один и тот же звук для входящих и исходящих сообщений.</summary>
    public bool Message { get; set; } = true;
    public bool Notification { get; set; } = true;
    /// <summary>Громкость звуков событий, 0–100.</summary>
    public int Volume { get; set; } = 70;

    /// <summary>Фоновая музыка под выбранный фон.</summary>
    public bool Music { get; set; } = true;
    /// <summary>Громкость музыки, 0–100 (по умолчанию тихо, чтобы не мешала).</summary>
    public int MusicVolume { get; set; } = 25;
    /// <summary>Музыкальная тема: auto — автоматически под текущий фон.</summary>
    public string MusicTheme { get; set; } = "auto";
    /// <summary>Путь к выбранной пользователем фоновой музыке (необязательно).</summary>
    public string? CustomMusicPath { get; set; }
    /// <summary>Пользовательские звуки для отдельных событий.</summary>
    public string? PurchaseSoundPath { get; set; }
    public string? MessageSoundPath { get; set; }
    public string? NotificationSoundPath { get; set; }

    public static SoundSettings Load()
    {
        try { return JsonStore.Load<SoundSettings>(FileName) ?? new SoundSettings(); }
        catch { return new SoundSettings(); }
    }

    public void Save()
    {
        try { JsonStore.Save(FileName, this); } catch (Exception logEx) { global::PlayerokBridge.Program.LogCrash("SoundSettings.cs:32", logEx); /* настройка звука не должна ронять приложение */ }
    }
}
