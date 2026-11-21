using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace PlayerokBridge.Views;

/// <summary>
/// Привязка окна к краям экрана (у окна без системной рамки её нет):
/// отпустили у верхнего края — разворачивается, у левого/правого — занимает половину экрана.
/// Потянули за заголовок развёрнутое или «прилипшее» окно — оно возвращается к прежнему размеру.
/// </summary>
public sealed partial class MainWindow
{
    private const int SnapMargin = 8;           // «зона прилипания» у края экрана, px
    private bool _snapped;                       // окно сейчас занимает половину экрана
    private Size _preSnapSize;                   // размер до привязки
    private double _preSnapMinWidth;
    private double _preSnapMinHeight;
    private PointerPressedEventArgs? _maxPress;  // нажатие на заголовок развёрнутого окна
    private Point _maxPressPoint;

    [StructLayout(LayoutKind.Sequential)]
    private struct SnapPoint { public int X; public int Y; }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out SnapPoint p);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);

    private static bool TryGetCursor(out PixelPoint p)
    {
        p = default;
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            if (!GetCursorPos(out var sp)) return false;
            p = new PixelPoint(sp.X, sp.Y);
            return true;
        }
        catch { return false; }
    }

    private static bool LeftButtonDown()
    {
        try { return (GetAsyncKeyState(0x01) & 0x8000) != 0; }
        catch { return false; }
    }

    /// <summary>Нажатие на заголовок: обычное перетаскивание + привязка к краю после отпускания.</summary>
    private void OnTitlePressed(PointerPressedEventArgs e, Control bar)
    {
        if (WindowState == WindowState.Maximized)
        {
            // Окно развернётся в обычное только когда мышь реально потянули.
            _maxPress = e;
            _maxPressPoint = e.GetPosition(bar);
            return;
        }
        if (_snapped && TryGetCursor(out var c)) UnsnapUnderCursor(c, e.GetPosition(this).X / Math.Max(1, Bounds.Width));
        DragAndSnap(e);
    }

    private void OnTitleMoved(PointerEventArgs e, Control bar)
    {
        var press = _maxPress;
        if (press is null || WindowState != WindowState.Maximized) { _maxPress = null; return; }
        if (!e.GetCurrentPoint(bar).Properties.IsLeftButtonPressed) { _maxPress = null; return; }
        var p = e.GetPosition(bar);
        if (Math.Abs(p.X - _maxPressPoint.X) + Math.Abs(p.Y - _maxPressPoint.Y) < 6) return;
        _maxPress = null;
        var ratio = _maxPressPoint.X / Math.Max(1, Bounds.Width);
        WindowState = WindowState.Normal;
        if (TryGetCursor(out var c)) UnsnapUnderCursor(c, ratio, restoreSize: false);
        try { DragAndSnap(press); } catch (Exception logEx) { global::PlayerokBridge.Program.LogCrash("MainWindow.Snap.cs:72", logEx); /* перетаскивание необязательно */ }
    }

    private void OnTitleReleased() => _maxPress = null;

    /// <summary>Возвращает окну прежний размер и ставит его так, чтобы курсор остался на заголовке.</summary>
    private void UnsnapUnderCursor(PixelPoint cursor, double ratio, bool restoreSize = true)
    {
        var scale = RenderScaling;
        if (restoreSize && _snapped && _preSnapSize.Width > 0)
        {
            MinWidth = _preSnapMinWidth;
            MinHeight = _preSnapMinHeight;
            Width = _preSnapSize.Width;
            Height = _preSnapSize.Height;
        }
        _snapped = false;
        var wPx = (int)(Width * scale);
        Position = new PixelPoint(cursor.X - (int)(wPx * Math.Clamp(ratio, 0, 1)), cursor.Y - (int)(16 * scale));
    }

    private void DragAndSnap(PointerPressedEventArgs e)
    {
        try
        {
            // Нативное перемещение Windows завершается после отпускания кнопки.
            // Проверку края откладываем на UI-диспетчер: сразу после возврата из
            // BeginMoveDrag координаты окна/курсора иногда ещё не успевают обновиться.
            BeginMoveDrag(e);
        }
        finally
        {
            if (OperatingSystem.IsWindows())
                Dispatcher.UIThread.Post(TrySnapToEdge, DispatcherPriority.Background);
        }
    }

    private void TrySnapToEdge()
    {
        if (!OperatingSystem.IsWindows() || WindowState != WindowState.Normal) return;
        if (!TryGetCursor(out var c)) return;
        var screen = Screens.ScreenFromPoint(c) ?? Screens.Primary;
        if (screen is null) return;
        var area = screen.WorkingArea;
        var scale = screen.Scaling;

        if (c.Y <= screen.Bounds.Y + SnapMargin)
        {
            if (_snapped)
            {
                MinWidth = _preSnapMinWidth;
                MinHeight = _preSnapMinHeight;
            }
            else _preSnapSize = new Size(Width, Height);
            _snapped = false;
            WindowState = WindowState.Maximized;
            return;
        }

        var left = c.X <= area.X + SnapMargin;
        var right = c.X >= area.Right - SnapMargin;
        if (!left && !right) return;

        if (!_snapped)
        {
            _preSnapSize = new Size(Width, Height);
            _preSnapMinWidth = MinWidth;
            _preSnapMinHeight = MinHeight;
        }
        // На небольших мониторах половина рабочей области может быть уже MinWidth.
        // В режиме Snap временно ослабляем минимумы, иначе окно физически не помещается.
        var wDip = Math.Max(1, area.Width / 2.0 / scale);
        var hDip = Math.Max(1, area.Height / scale);
        MinWidth = Math.Min(_preSnapMinWidth, wDip);
        MinHeight = Math.Min(_preSnapMinHeight, hDip);
        Width = wDip;
        Height = hDip;
        var wPx = (int)(wDip * scale);
        Position = new PixelPoint(left ? area.X : area.Right - wPx, area.Y);
        _snapped = true;
    }
}
