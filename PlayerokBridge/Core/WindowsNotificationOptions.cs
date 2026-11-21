namespace PlayerokBridge.Core;

/// <summary>Настройки системных уведомлений Windows.</summary>
public static class WindowsNotificationOptions
{
    private const string FileName = "windows_notifications.json";
    private static bool? _enabled;

    public static bool Enabled
    {
        get
        {
            if (_enabled is not null) return _enabled.Value;
            try { _enabled = JsonStore.Load<Options>(FileName)?.Enabled ?? true; }
            catch { _enabled = true; }
            return _enabled.Value;
        }
        set
        {
            _enabled = value;
            JsonStore.Save(FileName, new Options { Enabled = value });
        }
    }

    private sealed class Options
    {
        public bool Enabled { get; set; } = true;
    }
}
