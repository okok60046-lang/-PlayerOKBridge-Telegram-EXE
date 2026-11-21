using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace PlayerokBridge.Views;

public enum TransitionKind
{
    Diagonal, Wave, Slide, Zoom, Fade, Circle, Diamond, Blinds, Mosaic, Curtain, Slices, Flip, Random
}

public enum TransitionDirection { RightToLeft, LeftToRight, TopToBottom, BottomToTop }

public enum TransitionEase { Linear, EaseOut, EaseInOut, Back, Expo }

/// <summary>Настройки анимации перехода между страницами (вкладка GUI → «Переходы»). Хранятся в ui_preferences.json.</summary>
public sealed class TransitionSettings
{
    // Порядок заголовков соответствует порядку значений enum.
    public static readonly string[] KindTitles =
    {
        "◢ Диагональный пролёт", "🌊 Волна", "➡ Сдвиг", "🔍 Приближение", "🌫 Затухание", "⭕ Круг", "◆ Ромб",
        "🪟 Жалюзи", "🔲 Мозаика", "🎭 Шторки", "🧵 Полосы", "🔄 Переворот", "🎲 Случайный"
    };

    public static readonly string[] DirectionTitles =
        { "Справа налево", "Слева направо", "Сверху вниз", "Снизу вверх" };

    public static readonly string[] EaseTitles =
        { "Линейно", "Замедление в конце", "Плавно (разгон и торможение)", "С отскоком", "Резкий старт" };

    public bool Enabled { get; set; } = true;
    // Лёгкий fade выбран как безопасный стандарт: сложные эффекты доступны вручную.
    public TransitionKind Kind { get; set; } = TransitionKind.Fade;
    public TransitionDirection Direction { get; set; } = TransitionDirection.RightToLeft;
    public TransitionEase Ease { get; set; } = TransitionEase.EaseInOut;
    /// <summary>Длительность, мс.</summary>
    public int DurationMs { get; set; } = 280;
    public bool UseBackgroundColors { get; set; }
    public string Color1 { get; set; } = "#FF5E62";
    public string Color2 { get; set; } = "#7B2FBE";
    /// <summary>Толщина полосы/кольца, % от размера страницы (0 — без полосы).</summary>
    public double BandPercent { get; set; } = 0;
    public double BandOpacity { get; set; } = 0.9;
    /// <summary>Наклон кромки диагонального пролёта, градусы.</summary>
    public double Angle { get; set; } = 22;
    /// <summary>Количество частей: жалюзи, мозаика, полосы.</summary>
    public int Parts { get; set; } = 12;
    /// <summary>«Сила» эффекта, %: амплитуда волны, масштаб, разброс/задержка частей.</summary>
    public double Strength { get; set; } = 50;
    /// <summary>Параллакс: старая страница уезжает медленнее, новая подъезжает с запасом.</summary>
    public bool Parallax { get; set; } = false;
    public bool Glow { get; set; } = false;
    /// <summary>true — анимация фона замирает на время перехода; false — фон продолжает двигаться во время перехода.</summary>
    public bool PauseBackground { get; set; } = true;

    public TransitionSettings Clone() => (TransitionSettings)MemberwiseClone();
}

internal readonly record struct V(double X, double Y)
{
    public static V operator +(V a, V b) => new(a.X + b.X, a.Y + b.Y);
    public static V operator -(V a, V b) => new(a.X - b.X, a.Y - b.Y);
    public static V operator *(V a, double k) => new(a.X * k, a.Y * k);
    public Point P => new(X, Y);
}

/// <summary>
/// Слой поверх страницы: рисует переход между двумя снимками (старая → новая страница).
/// Все эффекты — функции от прогресса 0..1, поэтому их легко добавлять и настраивать.
/// </summary>
public sealed class TransitionOverlay : Control
{
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = new();
    private readonly float[] _rand = new float[2048];
    private IImage? _from, _to;
    private TransitionSettings _s = new();
    private TransitionKind _kind;
    private Color _c1 = Colors.White, _c2 = Colors.White;
    private double _dur = 500, _p, _acc, _last;
    private bool _playing;
    private Action<bool>? _done;

    public bool IsPlaying => _playing;

    public TransitionOverlay()
    {
        IsHitTestVisible = false;
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) => Tick();
    }

    /// <summary>Запускает переход. done(true) — доиграл до конца, done(false) — прерван.</summary>
    public void Play(IImage from, IImage to, TransitionSettings s, Color c1, Color c2, Action<bool>? done)
    {
        Cancel();
        _from = from; _to = to; _s = s; _c1 = c1; _c2 = c2; _done = done;
        // Показываем слой только на время реального перехода. Скрытый overlay не может
        // случайно перекрывать страницу после быстрой отмены/повторного запуска.
        IsVisible = true;
        if (s.Kind == TransitionKind.Random)
        {
            var kinds = Enum.GetValues<TransitionKind>().Where(k => k != TransitionKind.Random).ToArray();
            _kind = kinds[Random.Shared.Next(kinds.Length)];
        }
        else _kind = s.Kind;
        _dur = Math.Clamp(s.DurationMs, 80, 5000);
        // Случайные значения требуются только мозаике; не тратим время на остальные эффекты.
        if (_kind == TransitionKind.Mosaic)
            for (var i = 0; i < _rand.Length; i++) _rand[i] = (float)Random.Shared.NextDouble();
        _p = 0; _acc = 0; _last = 0; _playing = true;
        _clock.Restart();
        _timer.Start();
        InvalidateVisual();
    }

    public void Cancel()
    {
        if (_playing) Finish(false);
    }

    private void Tick()
    {
        if (!_playing) { _timer.Stop(); return; }
        // Если UI-поток «подвис» (тяжёлая страница), не перескакиваем сразу в конец:
        // за один кадр анимация продвигается не больше чем на 50 мс, иначе переход
        // выглядел как замершая старая вкладка поверх новой.
        var now = _clock.Elapsed.TotalMilliseconds;
        _acc += Math.Min(now - _last, 50);
        _last = now;
        _p = _acc / _dur;
        if (_p >= 1) { Finish(true); return; }
        InvalidateVisual();
    }

    private void Finish(bool completed)
    {
        _playing = false;
        _timer.Stop();
        var cb = _done;
        _done = null;
        _from = null;
        _to = null;
        // Сначала скрываем overlay, затем вызываем callback: callback может сразу
        // запустить обратный кадр предпросмотра и снова показать этот же control.
        IsVisible = false;
        InvalidateVisual();
        cb?.Invoke(completed);
    }

    // ---------- Вспомогательное ----------

    private static double Ease(TransitionEase e, double t)
    {
        t = Math.Clamp(t, 0, 1);
        switch (e)
        {
            case TransitionEase.EaseOut: return 1 - Math.Pow(1 - t, 3);
            case TransitionEase.EaseInOut: return t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
            case TransitionEase.Back:
                const double c1 = 1.70158, c3 = c1 + 1;
                return 1 + c3 * Math.Pow(t - 1, 3) + c1 * Math.Pow(t - 1, 2);
            case TransitionEase.Expo: return t >= 1 ? 1 : 1 - Math.Pow(2, -10 * t);
            default: return t;
        }
    }

    private V Dir => _s.Direction switch
    {
        TransitionDirection.RightToLeft => new V(-1, 0),
        TransitionDirection.LeftToRight => new V(1, 0),
        TransitionDirection.TopToBottom => new V(0, 1),
        _ => new V(0, -1),
    };

    private static Color Col(Color c, double alpha) =>
        Color.FromArgb((byte)Math.Clamp(alpha * 255, 0, 255), c.R, c.G, c.B);

    private static StreamGeometry Poly(IReadOnlyList<V> pts)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(pts[0].P, true);
            for (var i = 1; i < pts.Count; i++) c.LineTo(pts[i].P);
            c.EndFigure(true);
        }
        return g;
    }

    private static void AddRect(StreamGeometryContext c, Rect q)
    {
        c.BeginFigure(new Point(q.Left, q.Top), true);
        c.LineTo(new Point(q.Right, q.Top));
        c.LineTo(new Point(q.Right, q.Bottom));
        c.LineTo(new Point(q.Left, q.Bottom));
        c.EndFigure(true);
    }

    private static void Draw(DrawingContext ctx, IImage img, Rect r, V shift = default, double opacity = 1)
    {
        using (ctx.PushOpacity(Math.Clamp(opacity, 0, 1)))
        using (ctx.PushTransform(Matrix.CreateTranslation(shift.X, shift.Y)))
            ctx.DrawImage(img, r);
    }

    /// <summary>
    /// Рисует картинку везде, КРОМЕ области hole. Снимки страниц полупрозрачны, поэтому старую страницу нельзя
    /// просто рисовать целиком под новой — её текст просвечивал бы сквозь новую. Дополнение строится правилом even-odd.
    /// </summary>
    private static void DrawOutside(DrawingContext ctx, IImage img, Rect r, Geometry? hole, V shift = default, double opacity = 1)
    {
        if (hole is null) { Draw(ctx, img, r, shift, opacity); return; }
        var group = new GeometryGroup { FillRule = FillRule.EvenOdd };
        group.Children.Add(new RectangleGeometry(new Rect(-20000, -20000, 40000, 40000)));
        group.Children.Add(hole);
        using (ctx.PushGeometryClip(group))
            Draw(ctx, img, r, shift, opacity);
    }

    private static void DrawScaled(DrawingContext ctx, IImage img, Rect r, double sx, double sy, double opacity = 1)
    {
        double cx = r.Width / 2, cy = r.Height / 2;
        var m = Matrix.CreateTranslation(-cx, -cy) * Matrix.CreateScale(sx, sy) * Matrix.CreateTranslation(cx, cy);
        using (ctx.PushOpacity(Math.Clamp(opacity, 0, 1)))
        using (ctx.PushTransform(m))
            ctx.DrawImage(img, r);
    }

    /// <summary>Мягкое движение страницы: масштаб вокруг центра плюс независимый сдвиг.</summary>
    private static void DrawMotion(DrawingContext ctx, IImage img, Rect r,
        double sx, double sy, double dx, double dy, double opacity = 1)
    {
        double cx = r.Width / 2, cy = r.Height / 2;
        var m = Matrix.CreateTranslation(-cx, -cy)
              * Matrix.CreateScale(sx, sy)
              * Matrix.CreateTranslation(cx + dx, cy + dy);
        using (ctx.PushOpacity(Math.Clamp(opacity, 0, 1)))
        using (ctx.PushTransform(m))
            ctx.DrawImage(img, r);
    }

    /// <summary>Цветная вспышка в середине перехода (для эффектов без собственной полосы).</summary>
    private void Flash(DrawingContext ctx, Rect r, double ec)
    {
        var a = _s.BandOpacity * Math.Clamp(_s.BandPercent / 20.0, 0, 1) * 0.4 * Math.Sin(Math.PI * ec);
        if (a > 0.005) ctx.DrawRectangle(new SolidColorBrush(Col(_c1, a)), null, r);
    }

    // ---------- Отрисовка ----------

    public override void Render(DrawingContext ctx)
    {
        if (!_playing || _from is null || _to is null) return;
        double W = Bounds.Width, H = Bounds.Height;
        if (W <= 1 || H <= 1) return;
        var r = new Rect(0, 0, W, H);
        var raw = Ease(_s.Ease, _p);
        var ec = Math.Clamp(raw, 0, 1);

        using (ctx.PushClip(r))
        {
            switch (_kind)
            {
                case TransitionKind.Diagonal: DrawSweep(ctx, r, ec, false); break;
                case TransitionKind.Wave: DrawSweep(ctx, r, ec, true); break;
                case TransitionKind.Slide: DrawSlide(ctx, r, raw, ec); break;
                case TransitionKind.Zoom: DrawZoom(ctx, r, ec); break;
                case TransitionKind.Fade: DrawFade(ctx, r, ec); break;
                case TransitionKind.Circle: DrawReveal(ctx, r, ec, false); break;
                case TransitionKind.Diamond: DrawReveal(ctx, r, ec, true); break;
                case TransitionKind.Blinds: DrawBlinds(ctx, r, ec); break;
                case TransitionKind.Mosaic: DrawMosaic(ctx, r, ec); break;
                case TransitionKind.Curtain: DrawCurtain(ctx, r, ec); break;
                case TransitionKind.Slices: DrawSlices(ctx, r, ec); break;
                case TransitionKind.Flip: DrawFlip(ctx, r, ec); break;
            }
        }
    }

    /// <summary>
    /// Диагональный пролёт (и волна): по странице справа налево пролетает наклонная полоса;
    /// справа от неё появляется новая страница, слева исчезает старая.
    /// </summary>
    private void DrawSweep(DrawingContext ctx, Rect r, double ec, bool wave)
    {
        double W = r.Width, H = r.Height;
        var d = Dir;
        var a = Math.Clamp(_s.Angle, 0, 70) * Math.PI / 180;
        var perp = new V(-d.Y, d.X);
        var u = perp * Math.Cos(a) + d * Math.Sin(a);   // направление кромки
        var n = new V(u.Y, -u.X);                       // нормаль к кромке
        if (n.X * d.X + n.Y * d.Y < 0) n = n * -1;

        var extent = Math.Abs(d.X) * W + Math.Abs(d.Y) * H;
        var bandPx = Math.Max(0, _s.BandPercent) / 100.0 * extent;
        var bw = bandPx / 2;
        var h = (Math.Abs(n.X) * W + Math.Abs(n.Y) * H) / 2;
        var amp = wave ? Math.Clamp(_s.Strength, 0, 100) / 100.0 * 0.09 * extent : 0;
        var travel = h + bw + amp;
        var s = -travel + ec * 2 * travel;
        var c = new V(W / 2, H / 2);
        var big = W + H + bandPx * 2 + amp * 4;
        var phase = ec * 7;
        var lambda = Math.Max(60, extent * 0.12);

        List<V> Line(double off)
        {
            var o = c + n * off;
            if (!wave) return new List<V> { o - u * big, o + u * big };
            var pts = new List<V>();
            const int steps = 96;
            for (var i = 0; i <= steps; i++)
            {
                var k = -big + 2 * big * i / steps;
                pts.Add(o + u * k + n * (amp * Math.Sin(k / lambda + phase)));
            }
            return pts;
        }

        List<V> BandPoly(double hw)
        {
            var back = Line(s - hw);
            var front = Line(s + hw);
            front.Reverse();
            back.AddRange(front);
            return back;
        }

        // Старая страница уезжает по ходу движения, новая подъезжает навстречу.
        var shiftOld = _s.Parallax ? d * (ec * 0.22 * extent) : default;
        var shiftNew = _s.Parallax ? d * (-(1 - ec) * 0.22 * extent) : default;
        // Новая страница — всё, что осталось позади передней кромки полосы; старая — только перед ней.
        var lead = Line(s + bw);
        var behind = new List<V>(lead) { lead[^1] - n * big * 2, lead[0] - n * big * 2 };
        var behindGeo = Poly(behind);
        DrawOutside(ctx, _from!, r, behindGeo, shiftOld);
        using (ctx.PushGeometryClip(behindGeo))
            Draw(ctx, _to!, r, shiftNew);

        if (bandPx < 0.5) return;
        var alpha = Math.Clamp(_s.BandOpacity, 0, 1);

        if (_s.Glow)
        {
            ctx.DrawGeometry(new SolidColorBrush(Col(_c1, alpha * 0.12)), null, Poly(BandPoly(bw * 1.9)));
            ctx.DrawGeometry(new SolidColorBrush(Col(_c2, alpha * 0.2)), null, Poly(BandPoly(bw * 1.35)));
        }

        if (!wave)
        {
            // Градиентная полоса: рисуется в собственных координатах (ось X вдоль кромки, ось Y — поперёк).
            var origin = c + n * (s - bw) - u * big;
            var m = new Matrix(u.X, u.Y, n.X, n.Y, origin.X, origin.Y);
            var brush = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Col(_c2, alpha), 0),
                    new GradientStop(Col(_c1, alpha), 1),
                }
            };
            using (ctx.PushTransform(m))
                ctx.DrawRectangle(brush, null, new Rect(0, 0, 2 * big, bandPx));
        }
        else
        {
            ctx.DrawGeometry(new SolidColorBrush(Col(_c1, alpha)), null, Poly(BandPoly(bw)));
            ctx.DrawGeometry(new SolidColorBrush(Col(_c2, alpha)), null, Poly(BandPoly(bw * 0.45)));
        }
    }

    /// <summary>Сдвиг: страницы едут друг за другом; на стыке — цветная тень.</summary>
    private void DrawSlide(DrawingContext ctx, Rect r, double e, double ec)
    {
        double W = r.Width, H = r.Height;
        var d = Dir;
        var extent = Math.Abs(d.X) * W + Math.Abs(d.Y) * H;
        var depth = _s.Parallax ? 0.075 : 0.025;
        var shiftOld = d * (e * extent * depth);
        var shiftNew = d * ((e - 1) * extent);

        // Старая страница уходит чуть медленнее, новая приходит поверх неё.
        var newArea = new RectangleGeometry(new Rect(shiftNew.X, shiftNew.Y, W, H));
        DrawOutside(ctx, _from!, r, newArea,
            shiftOld, _s.Parallax ? 1 - 0.12 * ec : 1 - 0.04 * ec);
        DrawMotion(ctx, _to!, r,
            0.992 + 0.008 * ec, 0.992 + 0.008 * ec,
            shiftNew.X, shiftNew.Y, 1);

        var bandPx = Math.Max(0, _s.BandPercent) / 100.0 * extent * 0.45;
        if (bandPx < 0.5) return;
        var vertical = d.X == 0;
        var towardNeg = d.X < 0 || d.Y < 0;
        double edge = vertical
            ? (towardNeg ? shiftNew.Y : H + shiftNew.Y)
            : (towardNeg ? shiftNew.X : W + shiftNew.X);
        var along = towardNeg ? edge - bandPx : edge;
        var rect = vertical ? new Rect(0, along, W, bandPx) : new Rect(along, 0, bandPx, H);
        var alpha = Math.Clamp(_s.BandOpacity, 0, 1) * (1 - ec) * 0.42;
        var startPoint = new RelativePoint(vertical ? 0 : (towardNeg ? 1 : 0),
            vertical ? (towardNeg ? 1 : 0) : 0, RelativeUnit.Relative);
        var endPoint = new RelativePoint(vertical ? 0 : (towardNeg ? 0 : 1),
            vertical ? (towardNeg ? 0 : 1) : 0, RelativeUnit.Relative);
        var brush = new LinearGradientBrush
        {
            StartPoint = startPoint, EndPoint = endPoint,
            GradientStops =
            {
                new GradientStop(Col(_c1, alpha), 0),
                new GradientStop(Col(_c2, alpha * 0.35), 0.45),
                new GradientStop(Col(_c2, 0), 1)
            }
        };
        using (ctx.PushTransform(Matrix.CreateTranslation(rect.X, rect.Y)))
            ctx.DrawRectangle(brush, null, new Rect(0, 0, rect.Width, rect.Height));
    }

    private void DrawZoom(DrawingContext ctx, Rect r, double ec)
    {
        // Камера мягко отъезжает от старой страницы и приближается к новой.
        var strength = Math.Clamp(_s.Strength, 0, 100) / 100.0;
        var outScale = 1.025 + strength * 0.055;
        var inScale = 0.94 - strength * 0.035;
        DrawMotion(ctx, _from!, r,
            1 + (outScale - 1) * ec, 1 + (outScale - 1) * ec,
            0, 0, 1 - ec);
        DrawMotion(ctx, _to!, r,
            inScale + (1 - inScale) * ec, inScale + (1 - inScale) * ec,
            0, 0, ec);
        var sheen = Math.Sin(Math.PI * ec) * Math.Clamp(_s.BandOpacity, 0, 1) * 0.045;
        if (sheen > 0.001)
            ctx.DrawRectangle(new SolidColorBrush(Col(_c2, sheen)), null, r);
    }

    private void DrawFade(DrawingContext ctx, Rect r, double ec)
    {
        // Кинематографичный crossfade: едва заметный zoom противоположного направления.
        var oldScale = 1.012 - 0.012 * ec;
        var newScale = 0.986 + 0.014 * ec;
        DrawMotion(ctx, _from!, r, oldScale, oldScale, 0, 0, 1 - ec);
        DrawMotion(ctx, _to!, r, newScale, newScale, 0, 0, ec);

        var accent = Math.Sin(Math.PI * ec) * Math.Clamp(_s.BandOpacity, 0, 1) * 0.025;
        if (accent > 0.001)
            ctx.DrawRectangle(new SolidColorBrush(Col(_c1, accent)), null, r);
    }

    /// <summary>Круг или ромб растёт из стороны, откуда приходит новая страница.</summary>
    private void DrawReveal(DrawingContext ctx, Rect r, double ec, bool diamond)
    {
        double W = r.Width, H = r.Height;
        var d = Dir;
        double ox = W / 2 - d.X * W / 2, oy = H / 2 - d.Y * H / 2;
        double Far(double x, double y) =>
            diamond ? Math.Abs(x - ox) + Math.Abs(y - oy) : Math.Sqrt((x - ox) * (x - ox) + (y - oy) * (y - oy));
        var max = Math.Max(Math.Max(Far(0, 0), Far(W, 0)), Math.Max(Far(0, H), Far(W, H)));
        var rad = ec * max;

        if (rad < 0.5) { Draw(ctx, _from!, r); return; }

        Geometry shape;
        if (diamond)
            shape = Poly(new[] { new V(ox, oy - rad), new V(ox + rad, oy), new V(ox, oy + rad), new V(ox - rad, oy) });
        else
            shape = new EllipseGeometry(new Rect(ox - rad, oy - rad, 2 * rad, 2 * rad));
        DrawOutside(ctx, _from!, r, shape);
        using (ctx.PushGeometryClip(shape))
            Draw(ctx, _to!, r);

        var th = Math.Max(0, _s.BandPercent) / 100.0 * Math.Min(W, H) * 0.35;
        if (th < 0.5) return;
        var alpha = Math.Clamp(_s.BandOpacity, 0, 1) * (1 - ec * ec);
        if (_s.Glow)
            ctx.DrawGeometry(null, new Pen(new SolidColorBrush(Col(_c2, alpha * 0.25)), th * 2.2), shape);
        ctx.DrawGeometry(null, new Pen(new SolidColorBrush(Col(_c1, alpha)), th), shape);
    }

    /// <summary>Жалюзи: полосы по очереди «раскрываются», открывая новую страницу.</summary>
    private void DrawBlinds(DrawingContext ctx, Rect r, double ec)
    {
        double W = r.Width, H = r.Height;
        var d = Dir;
        var horizontal = d.X != 0;
        var n = Math.Clamp(_s.Parts, 2, 60);
        var size = (horizontal ? W : H) / n;
        var stagger = Math.Clamp(_s.Strength, 0, 100) / 100.0 * 0.8;
        var dcomp = horizontal ? d.X : d.Y;
        var th = Math.Max(2, Math.Min(size, Math.Max(0, _s.BandPercent) / 100.0 * (horizontal ? W : H)) * 0.25);
        var fronts = new List<Rect>();
        var any = false;

        var geo = new StreamGeometry();
        using (var gc = geo.Open())
        {
            for (var j = 0; j < n; j++)
            {
                var centre = (j + 0.5) / n;                    // 0..1 вдоль оси движения
                var t = (centre - 0.5) * dcomp + 0.5;          // 0 — откуда приходит новая страница
                var local = Math.Clamp((ec - stagger * t) / (1 - stagger), 0, 1);
                if (local <= 0.001) continue;
                var x0 = j * size;
                Rect q, front;
                if (horizontal)
                {
                    var wNew = size * local;
                    var left = d.X < 0 ? x0 + size - wNew : x0;
                    q = new Rect(left, 0, wNew, H);
                    var fx = d.X < 0 ? left : left + wNew;
                    front = new Rect(fx - th / 2, 0, th, H);
                }
                else
                {
                    var hNew = size * local;
                    var top = d.Y < 0 ? x0 + size - hNew : x0;
                    q = new Rect(0, top, W, hNew);
                    var fy = d.Y < 0 ? top : top + hNew;
                    front = new Rect(0, fy - th / 2, W, th);
                }
                AddRect(gc, q);
                any = true;
                if (local < 0.999 && _s.BandPercent > 0.5) fronts.Add(front);
            }
        }

        if (!any) { Draw(ctx, _from!, r); return; }
        DrawOutside(ctx, _from!, r, geo);
        using (ctx.PushGeometryClip(geo)) Draw(ctx, _to!, r);
        var brush = new SolidColorBrush(Col(_c1, Math.Clamp(_s.BandOpacity, 0, 1)));
        foreach (var f in fronts) ctx.DrawRectangle(brush, null, f);
    }

    /// <summary>Мозаика: квадраты вырастают в случайном порядке (разброс — «Сила»).</summary>
    private void DrawMosaic(DrawingContext ctx, Rect r, double ec)
    {
        double W = r.Width, H = r.Height;
        var d = Dir;
        var cols = Math.Clamp(_s.Parts, 3, 28);
        var cell = W / cols;
        var rows = (int)Math.Ceiling(H / cell);
        var mix = Math.Clamp(_s.Strength, 0, 100) / 100.0;
        const double window = 0.45;
        var tint = new List<(Rect, double)>();
        var any = false;

        var geo = new StreamGeometry();
        var exact = new StreamGeometry(); // без нахлёста: для вырезания старой страницы (even-odd не терпит перекрытий)
        using (var gc = geo.Open())
        using (var gx = exact.Open())
        {
            for (var row = 0; row < rows; row++)
                for (var col = 0; col < cols; col++)
                {
                    double cx = (col + 0.5) * cell, cy = (row + 0.5) * cell;
                    var pos = Math.Clamp((cx / W - 0.5) * d.X + (cy / H - 0.5) * d.Y + 0.5, 0, 1);
                    var rnd = _rand[(row * cols + col) % _rand.Length];
                    var delay = mix * rnd + (1 - mix) * pos;
                    var local = Math.Clamp((ec - delay * (1 - window)) / window, 0, 1);
                    if (local <= 0.001) continue;
                    var half = cell / 2 * local + (local >= 0.999 ? 0.6 : 0);   // небольшой нахлёст — без швов
                    var q = new Rect(cx - half, cy - half, half * 2, half * 2);
                    AddRect(gc, q);
                    var hx = cell / 2 * local;
                    AddRect(gx, new Rect(cx - hx, cy - hx, hx * 2, hx * 2));
                    any = true;
                    if (local < 0.999) tint.Add((q, local));
                }
        }

        if (!any) { Draw(ctx, _from!, r); return; }
        DrawOutside(ctx, _from!, r, exact);
        using (ctx.PushGeometryClip(geo)) Draw(ctx, _to!, r);
        if (_s.BandPercent < 0.5) return;
        foreach (var (q, local) in tint)
            ctx.DrawRectangle(new SolidColorBrush(Col(_c1, Math.Clamp(_s.BandOpacity, 0, 1) * (1 - local) * 0.7)), null, q);
    }

    /// <summary>Шторки: старая страница раздвигается от центра, открывая новую.</summary>
    private void DrawCurtain(DrawingContext ctx, Rect r, double ec)
    {
        double W = r.Width, H = r.Height;
        var horizontal = Dir.X != 0;
        // Новая страница видна только в раскрывшейся щели, иначе она просвечивала бы сквозь старую.
        using (ctx.PushClip(horizontal
            ? new Rect(W / 2 - ec * W / 2, 0, ec * W, H)
            : new Rect(0, H / 2 - ec * H / 2, W, ec * H)))
            Draw(ctx, _to!, r);

        if (horizontal)
        {
            var off = ec * W / 2;
            using (ctx.PushTransform(Matrix.CreateTranslation(-off, 0)))
            using (ctx.PushClip(new Rect(0, 0, W / 2, H))) ctx.DrawImage(_from!, r);
            using (ctx.PushTransform(Matrix.CreateTranslation(off, 0)))
            using (ctx.PushClip(new Rect(W / 2, 0, W / 2, H))) ctx.DrawImage(_from!, r);
        }
        else
        {
            var off = ec * H / 2;
            using (ctx.PushTransform(Matrix.CreateTranslation(0, -off)))
            using (ctx.PushClip(new Rect(0, 0, W, H / 2))) ctx.DrawImage(_from!, r);
            using (ctx.PushTransform(Matrix.CreateTranslation(0, off)))
            using (ctx.PushClip(new Rect(0, H / 2, W, H / 2))) ctx.DrawImage(_from!, r);
        }

        var th = Math.Max(0, _s.BandPercent) / 100.0 * (horizontal ? W : H) * 0.25;
        if (th < 0.5 || ec > 0.995) return;
        var alpha = Math.Clamp(_s.BandOpacity, 0, 1);
        var main = new SolidColorBrush(Col(_c1, alpha));
        var glow = new SolidColorBrush(Col(_c2, alpha * 0.3));
        if (horizontal)
        {
            double xl = W / 2 - ec * W / 2, xr = W / 2 + ec * W / 2;
            if (_s.Glow) { ctx.DrawRectangle(glow, null, new Rect(xl - th * 1.8, 0, th * 1.8, H)); ctx.DrawRectangle(glow, null, new Rect(xr, 0, th * 1.8, H)); }
            ctx.DrawRectangle(main, null, new Rect(xl - th * 0.5, 0, th * 0.5, H));
            ctx.DrawRectangle(main, null, new Rect(xr, 0, th * 0.5, H));
        }
        else
        {
            double yt = H / 2 - ec * H / 2, yb = H / 2 + ec * H / 2;
            if (_s.Glow) { ctx.DrawRectangle(glow, null, new Rect(0, yt - th * 1.8, W, th * 1.8)); ctx.DrawRectangle(glow, null, new Rect(0, yb, W, th * 1.8)); }
            ctx.DrawRectangle(main, null, new Rect(0, yt - th * 0.5, W, th * 0.5));
            ctx.DrawRectangle(main, null, new Rect(0, yb, W, th * 0.5));
        }
    }

    /// <summary>Полосы: соседние полосы уезжают в противоположные стороны, на их место приезжает новая страница.</summary>
    private void DrawSlices(DrawingContext ctx, Rect r, double ec)
    {
        double W = r.Width, H = r.Height;
        var d = Dir;
        var horizontal = d.X != 0;
        var n = Math.Clamp(_s.Parts, 2, 40);
        var extent = horizontal ? W : H;
        var size = (horizontal ? H : W) / n;
        var stagger = Math.Clamp(_s.Strength, 0, 100) / 100.0 * 0.6;

        for (var i = 0; i < n; i++)
        {
            var local = Math.Clamp((ec - stagger * (n == 1 ? 0 : i / (n - 1.0))) / (1 - stagger), 0, 1);
            var dir = i % 2 == 0 ? d : d * -1;
            var strip = horizontal
                ? new Rect(0, i * size, W, size + 0.6)
                : new Rect(i * size, 0, size + 0.6, H);
            using (ctx.PushClip(strip))
            {
                Draw(ctx, _from!, r, dir * (local * extent));
                Draw(ctx, _to!, r, dir * ((local - 1) * extent));
            }
        }
        Flash(ctx, r, ec);
    }

    /// <summary>Переворот: страница сжимается в линию, затем разворачивается уже новая.</summary>
    private void DrawFlip(DrawingContext ctx, Rect r, double ec)
    {
        var horizontal = Dir.X != 0;
        var first = ec < 0.5;
        var k = Math.Max(0.002, first ? 1 - ec * 2 : ec * 2 - 1);
        DrawScaled(ctx, first ? _from! : _to!, r, horizontal ? k : 1, horizontal ? 1 : k);
        Flash(ctx, r, ec);
    }
}
