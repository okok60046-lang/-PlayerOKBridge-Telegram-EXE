using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using PlayerokBridge.Core;

namespace PlayerokBridge.Views;

public enum BgMode { Solid, Flow, Lava, Snow, Bubbles, Aurora, Stars }

public sealed record GuiPreset(string Key, string Title, BgMode Mode, string[] Colors, int Density, double Speed);

/// <summary>Настройки оформления (вкладка GUI). Хранятся в ui_preferences.json.</summary>
public sealed class GuiSettings
{
    public static readonly GuiPreset[] Presets =
    {
        new("lava", "🫧 Лава-лампа", BgMode.Lava, new[] { "#FFB347", "#FF5E62", "#C2338F", "#7B2FBE" }, 10, 1.0),
        new("flow", "🌈 Переливание", BgMode.Flow, new[] { "#FF512F", "#DD2476", "#7F00FF", "#2575FC" }, 10, 1.0),
        new("sunset", "🌇 Закат", BgMode.Flow, new[] { "#F7971E", "#FF5E62", "#B24592" }, 10, 0.8),
        new("fire", "🔥 Огонь", BgMode.Lava, new[] { "#F5AF19", "#F12711", "#7A0A0A" }, 12, 1.3),
        new("snow", "❄️ Снежинки", BgMode.Snow, new[] { "#0B1B3A", "#1B3A6B", "#2E5C9A" }, 70, 1.0),
        new("bubbles", "🫧 Пузыри", BgMode.Bubbles, new[] { "#0F2027", "#203A43", "#2C5364" }, 35, 1.0),
        new("aurora", "🌌 Аврора", BgMode.Aurora, new[] { "#06131F", "#0B3D4A", "#20E3B2", "#8E54E9" }, 10, 1.0),
        new("stars", "✨ Звёздное небо", BgMode.Stars, new[] { "#05060F", "#0D1033", "#1B1464" }, 60, 1.0),
        new("solid", "▪ Однотонный", BgMode.Solid, new[] { "#111318" }, 10, 1.0),
    };

    public static readonly string[] ModeTitles =
        { "Однотонный", "Переливание", "Лава-лампа", "Снежинки", "Пузыри", "Аврора", "Звёзды" };

    public string Preset { get; set; } = "lava";
    public BgMode Mode { get; set; } = BgMode.Lava;
    public List<string> Colors { get; set; } = Presets[0].Colors.ToList();
    public double Speed { get; set; } = 1.0;
    public int Density { get; set; } = 10;
    public bool Animate { get; set; } = true;
    public bool ShowDealCards { get; set; } = true;
    public double PanelOpacity { get; set; } = 0.45;
    public double CornerRadius { get; set; } = 18;
    public string BuyerBubble { get; set; } = "#252A34";
    public string OwnBubble { get; set; } = "#2457A6";
    /// <summary>Вкладка «Мои товары»: список (true) или карточки (false).</summary>
    public bool MyProductsList { get; set; }
    /// <summary>Вкладка «Мои товары»: индекс сортировки.</summary>
    public int MyProductsSort { get; set; }
    /// <summary>Анимация перехода между страницами.</summary>
    public TransitionSettings Transition { get; set; } = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static string FilePath => Path.Combine(JsonStore.DataDir, "ui_preferences.json");

    public static GuiSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<GuiSettings>(File.ReadAllText(FilePath), Options) ?? new GuiSettings();
                // Миграция нетронутых старых настроек: прежний заводской Diagonal/650ms
                // был тяжёлым на слабых ПК. Пользовательские настройки не перезаписываем.
                var old = loaded.Transition;
                if (old is not null &&
                    old.Kind == TransitionKind.Diagonal &&
                    old.Direction == TransitionDirection.RightToLeft &&
                    old.Ease == TransitionEase.EaseInOut &&
                    old.DurationMs == 650 && Math.Abs(old.BandPercent - 14) < 0.01 &&
                    Math.Abs(old.BandOpacity - 0.9) < 0.01 &&
                    Math.Abs(old.Angle - 22) < 0.01 && old.Parts == 12 &&
                    Math.Abs(old.Strength - 50) < 0.01 && old.Parallax && old.Glow &&
                    !old.UseBackgroundColors &&
                    string.Equals(old.Color1, "#FF5E62", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(old.Color2, "#7B2FBE", StringComparison.OrdinalIgnoreCase))
                {
                    loaded.Transition = new TransitionSettings { Enabled = old.Enabled };
                }
                return loaded;
            }
        }
        catch { /* битый файл — берём значения по умолчанию */ }
        return new GuiSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(JsonStore.DataDir);
        // Атомарная запись: сбой посреди сохранения не должен оставлять битый файл настроек.
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Options));
        File.Move(temp, FilePath, overwrite: true);
    }

    public void ApplyPreset(GuiPreset p)
    {
        Preset = p.Key; Mode = p.Mode; Colors = p.Colors.ToList(); Density = p.Density; Speed = p.Speed;
    }
}

/// <summary>Анимированный фон: переливающийся градиент, лава-лампа, снег, пузыри, аврора, звёзды.</summary>
public sealed class AnimatedBackground : Control
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Color[] _colors = { Color.Parse("#111318") };
    private BgMode _mode = BgMode.Solid;
    private double _speed = 1, _t, _last;
    private int _density = 20;
    private bool _animate = true;
    private bool _isAttached;
    private bool _temporarilyPaused;
    private bool _suspended;
    private RadialGradientBrush[] _lava = Array.Empty<RadialGradientBrush>();
    private Particle[] _p = Array.Empty<Particle>();

    private readonly record struct Particle(double X, double Y, double R, double S, double Ph, int C);

    public AnimatedBackground()
    {
        IsHitTestVisible = false;
        _timer.Tick += (_, _) =>
        {
            var now = _clock.Elapsed.TotalSeconds;
            if (_animate && _mode != BgMode.Solid && !_temporarilyPaused) { _t += (now - _last) * _speed; InvalidateVisual(); }
            _last = now;
        };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        SyncTimer();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _isAttached = false;
        _timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    public void Configure(BgMode mode, Color[] colors, double speed, int density, bool animate)
    {
        _mode = mode; _colors = colors.Length > 0 ? colors : new[] { Color.Parse("#111318") };
        _speed = Math.Clamp(speed, 0.05, 6); _density = Math.Clamp(density, 1, 300); _animate = animate;
        Reseed();
        SyncTimer();
        InvalidateVisual();
    }

    /// <summary>Приостанавливает фон на время смены вкладки, чтобы он не конкурировал с переходом за UI-поток.</summary>
    public void PauseAnimation(bool paused)
    {
        _temporarilyPaused = paused;
        SyncTimer();
    }

    /// <summary>Полная остановка анимации (например, окно свёрнуто) — независимо от пауз на время переходов.</summary>
    public void Suspend(bool suspended)
    {
        if (_suspended == suspended) return;
        _suspended = suspended;
        SyncTimer();
    }

    private void SyncTimer()
    {
        // Однотонный фон и отключённая анимация не требуют постоянного DispatcherTimer.
        if (_isAttached && _animate && _mode != BgMode.Solid && !_temporarilyPaused && !_suspended)
        {
            _last = _clock.Elapsed.TotalSeconds;
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }

    private void Reseed()
    {
        var count = _mode switch
        {
            BgMode.Lava => _density,
            BgMode.Snow => _density * 2,
            BgMode.Bubbles => _density,
            BgMode.Stars => _density * 3,
            _ => 0
        };
        var rnd = new Random(7);
        _p = new Particle[count];
        for (var i = 0; i < count; i++)
            _p[i] = new Particle(rnd.NextDouble(), rnd.NextDouble(),
                _mode == BgMode.Lava ? 0.10 + rnd.NextDouble() * 0.25 : rnd.NextDouble(),
                0.6 + rnd.NextDouble() * 0.8, rnd.NextDouble() * Math.PI * 2, rnd.Next(0, 16));
        // Кисти «капель» не меняются от кадра к кадру — создаём один раз на цвет.
        _lava = _mode == BgMode.Lava
            ? _colors.Select(col =>
            {
                var c = Lighten(col, 0.35);
                return new RadialGradientBrush
                {
                    GradientStops =
                    {
                        new GradientStop(Color.FromArgb(120, c.R, c.G, c.B), 0),
                        new GradientStop(Color.FromArgb(45, c.R, c.G, c.B), 1),
                    }
                };
            }).ToArray()
            : Array.Empty<RadialGradientBrush>();
    }

    private static Color Lerp(Color a, Color b, double f) => Color.FromArgb(255,
        (byte)(a.R + (b.R - a.R) * f), (byte)(a.G + (b.G - a.G) * f), (byte)(a.B + (b.B - a.B) * f));

    private Color ColorAt(double u)
    {
        var n = _colors.Length;
        if (n == 1) return _colors[0];
        var p = u * n;
        var i = (int)Math.Floor(p);
        return Lerp(_colors[i % n], _colors[(i + 1) % n], p - Math.Floor(p));
    }

    private static Color Lighten(Color c, double f) => Color.FromRgb(
        (byte)(c.R + (255 - c.R) * f), (byte)(c.G + (255 - c.G) * f), (byte)(c.B + (255 - c.B) * f));

    /// <summary>Замкнутый градиент, который медленно «переливается» и поворачивается.</summary>
    private IBrush Flow(double angle0, double angleRate, double phaseRate)
    {
        var a = angle0 + _t * angleRate;
        double dx = Math.Cos(a) * 0.5, dy = Math.Sin(a) * 0.5;
        var br = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0.5 - dx, 0.5 - dy, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0.5 + dx, 0.5 + dy, RelativeUnit.Relative),
        };
        var phase = (_t * phaseRate) % 1.0;
        const int N = 12;
        for (var i = 0; i <= N; i++)
        {
            var u = i / (double)N;
            br.GradientStops.Add(new GradientStop(ColorAt((u + phase) % 1.0), u));
        }
        return br;
    }

    /// <summary>Спокойный вертикальный градиент для «ночных» тем.</summary>
    private IBrush Vertical()
    {
        var br = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        };
        if (_colors.Length == 1) { br.GradientStops.Add(new GradientStop(_colors[0], 0)); br.GradientStops.Add(new GradientStop(_colors[0], 1)); }
        else for (var i = 0; i < _colors.Length; i++) br.GradientStops.Add(new GradientStop(_colors[i], i / (double)(_colors.Length - 1)));
        return br;
    }

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w <= 0 || h <= 0) return;
        var rect = new Rect(0, 0, w, h);
        var min = Math.Min(w, h);
        var n = _colors.Length;

        switch (_mode)
        {
            case BgMode.Solid:
                ctx.DrawRectangle(new SolidColorBrush(_colors[0]), null, rect);
                break;

            case BgMode.Flow:
                ctx.DrawRectangle(Flow(0.6, 0.05, 0.03), null, rect);
                break;

            case BgMode.Lava:
                ctx.DrawRectangle(Flow(0.8, 0.02, 0.015), null, rect);
                foreach (var p in _p)
                {
                    var cx = (p.X + 0.10 * Math.Sin(_t * 0.15 * p.S + p.Ph)) * w;
                    var cy = (p.Y + 0.12 * Math.Cos(_t * 0.12 * p.S + p.Ph * 1.3)) * h;
                    var r = p.R * (1 + 0.08 * Math.Sin(_t * 0.3 * p.S + p.Ph)) * min * 1.4;
                    if (_lava.Length != n) break;
                    ctx.DrawEllipse(_lava[p.C % n], null, new Point(cx, cy), r, r);
                }
                break;

            case BgMode.Snow:
                ctx.DrawRectangle(Vertical(), null, rect);
                foreach (var p in _p)
                {
                    var y = ((p.Y + _t * 0.02 * p.S) % 1.1) - 0.05;
                    var x = p.X + 0.015 * Math.Sin(_t * 0.5 * p.S + p.Ph);
                    double px = x * w, py = y * h, r = 1.5 + p.R * 4;
                    var white = new SolidColorBrush(Color.FromArgb((byte)(120 + 135 * p.R), 255, 255, 255));
                    if (r > 3.6)
                    {
                        var pen = new Pen(white, 1.2);
                        var rot = _t * 0.3 * p.S + p.Ph;
                        for (var k = 0; k < 3; k++)
                        {
                            var a = rot + k * Math.PI / 3;
                            double dx = Math.Cos(a) * r * 1.6, dy = Math.Sin(a) * r * 1.6;
                            ctx.DrawLine(pen, new Point(px - dx, py - dy), new Point(px + dx, py + dy));
                        }
                    }
                    else ctx.DrawEllipse(white, null, new Point(px, py), r, r);
                }
                break;

            case BgMode.Bubbles:
                ctx.DrawRectangle(Vertical(), null, rect);
                foreach (var p in _p)
                {
                    var y = 1.1 - ((p.Y + _t * 0.04 * p.S) % 1.2);
                    var x = p.X + 0.02 * Math.Sin(_t * 0.6 * p.S + p.Ph);
                    var r = (0.008 + p.R * 0.045) * min * 1.6;
                    var edge = Lighten(_colors[p.C % n], 0.6);
                    ctx.DrawEllipse(new SolidColorBrush(Color.FromArgb(28, edge.R, edge.G, edge.B)),
                        new Pen(new SolidColorBrush(Color.FromArgb(120, edge.R, edge.G, edge.B)), 1.2),
                        new Point(x * w, y * h), r, r);
                }
                break;

            case BgMode.Aurora:
                ctx.DrawRectangle(Vertical(), null, rect);
                for (var b = 0; b < 3; b++)
                {
                    var col = Lighten(_colors[(b + 1) % n], 0.15);
                    var geo = new StreamGeometry();
                    using (var g = geo.Open())
                    {
                        const int steps = 40;
                        double Top(double x) => h * (0.18 + 0.12 * b) + h * 0.07 * Math.Sin(x / w * (3 + b) + _t * 0.25 * (1 + b * 0.3) + b);
                        g.BeginFigure(new Point(0, Top(0)), true);
                        for (var i = 1; i <= steps; i++) { var x = w * i / steps; g.LineTo(new Point(x, Top(x))); }
                        g.LineTo(new Point(w, h * (0.18 + 0.12 * b) + h * 0.5));
                        g.LineTo(new Point(0, h * (0.18 + 0.12 * b) + h * 0.5));
                        g.EndFigure(true);
                    }
                    var brush = new LinearGradientBrush
                    {
                        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                        GradientStops =
                        {
                            new GradientStop(Color.FromArgb(150, col.R, col.G, col.B), 0),
                            new GradientStop(Color.FromArgb(0, col.R, col.G, col.B), 1),
                        }
                    };
                    ctx.DrawGeometry(brush, null, geo);
                }
                break;

            case BgMode.Stars:
                ctx.DrawRectangle(Vertical(), null, rect);
                foreach (var p in _p)
                {
                    var a = 0.35 + 0.65 * (0.5 + 0.5 * Math.Sin(_t * p.S * 1.5 + p.Ph));
                    var r = 0.6 + p.R * 1.8;
                    ctx.DrawEllipse(new SolidColorBrush(Color.FromArgb((byte)(255 * a), 255, 255, 255)), null,
                        new Point(p.X * w, p.Y * h), r, r);
                }
                break;
        }
    }
}
