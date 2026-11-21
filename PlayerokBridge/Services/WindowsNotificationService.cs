using System.Runtime.InteropServices;

namespace PlayerokBridge.Services;

/// <summary>
/// Системные уведомления Windows через область уведомлений. Не требует внешних NuGet-пакетов.
/// На Windows 10/11 balloon-уведомления обычно отображаются системным центром уведомлений.
/// </summary>
public sealed class WindowsNotificationService : IDisposable
{
    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NifInfo = 0x00000010;
    private const uint NiifInfo = 0x00000001;
    private const uint NiifNoSound = 0x00000010;
    private const uint CallbackMessage = 0x8000 + 0x51;
    private static readonly IntPtr ApplicationIcon = new(32512); // IDI_APPLICATION

    private IntPtr _window;
    private bool _added;

    public bool Initialize(IntPtr windowHandle)
    {
        if (!OperatingSystem.IsWindows() || windowHandle == IntPtr.Zero) return false;
        _window = windowHandle;
        var data = CreateData();
        data.Flags = NifMessage | NifIcon | NifTip;
        data.CallbackMessage = CallbackMessage;
        data.Icon = LoadIcon(IntPtr.Zero, ApplicationIcon);
        data.Tip = "PlayerOK Bridge";
        _added = Shell_NotifyIcon(NimAdd, ref data);
        return _added;
    }

    /// <param name="silent">true — без системного звука (звук играет приложение само).</param>
    public bool Show(string title, string message, bool silent = false)
    {
        if (!OperatingSystem.IsWindows() || !_added || _window == IntPtr.Zero) return false;
        var data = CreateData();
        data.Flags = NifInfo;
        data.InfoTitle = Limit(title, 63);
        data.Info = Limit(message, 255);
        data.InfoFlags = silent ? NiifInfo | NiifNoSound : NiifInfo;
        data.TimeoutOrVersion = 5000;
        return Shell_NotifyIcon(NimModify, ref data);
    }

    private NotifyIconData CreateData() => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(),
        Window = _window,
        Id = 1,
        Tip = "",
        Info = "",
        InfoTitle = "",
        GuidItem = Guid.Empty
    };

    private static string Limit(string? value, int max)
    {
        var text = string.IsNullOrWhiteSpace(value) ? "PlayerOK Bridge" : value.Trim();
        return text.Length <= max ? text : text[..max];
    }

    public void Dispose()
    {
        if (_added && _window != IntPtr.Zero)
        {
            var data = CreateData();
            Shell_NotifyIcon(NimDelete, ref data);
        }
        _added = false;
        _window = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid GuidItem;
        public IntPtr BalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr iconName);
}
