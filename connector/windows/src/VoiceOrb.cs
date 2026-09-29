using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace CodexVoice;

internal enum VoiceOrbState { Idle, Loading, Listening, Error }

internal sealed class VoiceOrb : Control
{
    private const uint SpiGetClientAreaAnimation = 0x1042;
    private const int ContourPoints = 72;

    private static readonly IBrush IdleHalo = Gradient("#2CB47D5B", "#00B47D5B");
    private static readonly IBrush LoadingHalo = Gradient("#3ABF8054", "#00BF8054");
    private static readonly IBrush ListeningHalo = Gradient("#58E59A65", "#00E59A65");
    private static readonly IBrush ErrorHalo = Gradient("#4ED66F61", "#00D66F61");
    private static readonly IBrush IdleBody = Gradient("#FFAD7555", "#FF4A302A");
    private static readonly IBrush LoadingBody = Gradient("#FFC2865D", "#FF56362B");
    private static readonly IBrush ListeningBody = Gradient("#FFFFB879", "#FF783A30");
    private static readonly IBrush ErrorBody = Gradient("#FFDA7D68", "#FF65352F");
    private static readonly Pen IdleRing = Stroke("#6B9F725A", 1.5);
    private static readonly Pen LoadingRing = Stroke("#8DBA825D", 1.5);
    private static readonly Pen ListeningRing = Stroke("#B5E9AB7B", 1.5);
    private static readonly Pen ErrorRing = Stroke("#C8E88C79", 1.5);
    private static readonly Pen BodyEdge = Stroke("#68FFD1A4", 1);
    private static readonly Pen LoadingArc = Stroke("#FFF4BE87", 3.5);
    private static readonly Pen MicInk = Stroke("#FFF9E8D9", 4);
    private static readonly Pen MutedMicInk = Stroke("#DCEBD2C4", 4);
    private static readonly Pen ErrorInk = Stroke("#FFFFE6DB", 5);
    private static readonly IBrush ErrorDot = new SolidColorBrush(Color.Parse("#FFFFE6DB"));

    private readonly DispatcherTimer _timer;
    private VoiceOrbState _state = VoiceOrbState.Idle;
    private bool _attached;
    private bool _reducedMotion;
    private int _motionCheckTicks;
    private double _phase;
    private double _level;
    private double _targetLevel;
    private long _lastLevelTick;

    public VoiceOrb()
    {
        Width = 270;
        Height = 270;
        Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += OnTick;
        AttachedToVisualTree += (_, _) =>
        {
            _attached = true;
            RefreshMotionPreference();
            UpdateTimer();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _attached = false;
            _timer.Stop();
        };
    }

    public void SetState(VoiceOrbState state)
    {
        if (_state == state) return;
        _state = state;
        if (state != VoiceOrbState.Listening)
            _targetLevel = _level = 0;
        UpdateTimer();
        InvalidateVisual();
    }

    public void SetLevel(float level)
    {
        if (_state != VoiceOrbState.Listening) return;
        // AudioLevel is microphone RMS, so ordinary speech is far below 1.0.
        var raw = float.IsFinite(level) ? Math.Clamp(level, 0, 1) : 0;
        _targetLevel = Math.Sqrt(Math.Clamp((raw - 0.004) / 0.065, 0, 1));
        _lastLevelTick = Environment.TickCount64;
        if (_reducedMotion)
        {
            _level = _targetLevel;
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var scale = Math.Min(Bounds.Width, Bounds.Height) / 270;
        if (scale <= 0) return;
        var cx = Bounds.Width / 2;
        var cy = Bounds.Height / 2;
        var listening = _state == VoiceOrbState.Listening;
        var level = listening ? _level : 0;

        var halo = _state switch
        {
            VoiceOrbState.Loading => LoadingHalo,
            VoiceOrbState.Listening => ListeningHalo,
            VoiceOrbState.Error => ErrorHalo,
            _ => IdleHalo
        };
        var body = _state switch
        {
            VoiceOrbState.Loading => LoadingBody,
            VoiceOrbState.Listening => ListeningBody,
            VoiceOrbState.Error => ErrorBody,
            _ => IdleBody
        };
        var ring = _state switch
        {
            VoiceOrbState.Loading => LoadingRing,
            VoiceOrbState.Listening => ListeningRing,
            VoiceOrbState.Error => ErrorRing,
            _ => IdleRing
        };

        DrawCircle(context, cx, cy, (112 + level * 17) * scale, halo);
        context.DrawEllipse(null, ring, Circle(cx, cy, 116 * scale));
        if (_state == VoiceOrbState.Loading)
            DrawLoadingArc(context, cx, cy, 116 * scale, _reducedMotion ? -Math.PI / 2 : _phase);

        var wave = _reducedMotion ? 0 : level * 7;
        DrawContour(context, cx, cy, (82 + level * 16) * scale, wave * scale,
            listening ? _phase : 0, body, listening ? BodyEdge : null);

        if (_state == VoiceOrbState.Error)
            DrawErrorMark(context, cx, cy, scale);
        else
            DrawMic(context, cx, cy, scale, _state == VoiceOrbState.Idle ? MutedMicInk : MicInk);
    }

    private void OnTick(object? sender, EventArgs args)
    {
        if (++_motionCheckTicks >= 60)
        {
            _motionCheckTicks = 0;
            RefreshMotionPreference();
        }
        if (_state == VoiceOrbState.Listening && Environment.TickCount64 - _lastLevelTick > 220)
            _targetLevel = 0;

        if (_reducedMotion)
        {
            if (Math.Abs(_level - _targetLevel) > 0.001)
            {
                _level = _targetLevel;
                InvalidateVisual();
            }
            return;
        }

        if (_state == VoiceOrbState.Loading || _state == VoiceOrbState.Listening)
            _phase += 0.075;
        var response = _targetLevel > _level ? 0.48 : 0.16;
        _level += (_targetLevel - _level) * response;
        if (_state == VoiceOrbState.Loading || _level > 0.003 || _targetLevel > 0.003)
            InvalidateVisual();
    }

    private void UpdateTimer()
    {
        if (_attached && _state is VoiceOrbState.Loading or VoiceOrbState.Listening)
            _timer.Start();
        else
            _timer.Stop();
    }

    private void RefreshMotionPreference()
    {
        // Windows Accessibility > Visual effects > Animation effects.
        var reduced = OperatingSystem.IsWindows()
            && SystemParametersInfo(SpiGetClientAreaAnimation, 0, out var enabled, 0)
            && !enabled;
        if (_reducedMotion == reduced) return;
        _reducedMotion = reduced;
        _timer.Interval = TimeSpan.FromMilliseconds(reduced ? 120 : 33);
        if (reduced) _level = _targetLevel;
        InvalidateVisual();
    }

    private static void DrawContour(DrawingContext context, double cx, double cy,
        double radius, double wave, double phase, IBrush brush, Pen? edge)
    {
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            for (var index = 0; index < ContourPoints; index++)
            {
                var angle = index * Math.PI * 2 / ContourPoints;
                var shape = 0.62 * Math.Sin(3 * angle + phase)
                    + 0.26 * Math.Sin(5 * angle - phase * 0.7)
                    + 0.12 * Math.Sin(8 * angle + phase * 1.2);
                var distance = radius + wave * shape;
                var point = new Point(cx + Math.Cos(angle) * distance,
                    cy + Math.Sin(angle) * distance);
                if (index == 0) path.BeginFigure(point, true);
                else path.LineTo(point);
            }
            path.EndFigure(true);
        }
        context.DrawGeometry(brush, edge, geometry);
    }

    private static void DrawLoadingArc(DrawingContext context, double cx, double cy,
        double radius, double start)
    {
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            for (var index = 0; index <= 20; index++)
            {
                var angle = start + index * Math.PI * 0.68 / 20;
                var point = new Point(cx + Math.Cos(angle) * radius,
                    cy + Math.Sin(angle) * radius);
                if (index == 0) path.BeginFigure(point, false);
                else path.LineTo(point);
            }
            path.EndFigure(false);
        }
        context.DrawGeometry(null, LoadingArc, geometry);
    }

    private static void DrawMic(DrawingContext context, double cx, double cy,
        double scale, Pen pen)
    {
        var top = cy - 21 * scale;
        context.DrawRectangle(null, pen,
            new RoundedRect(new Rect(cx - 10 * scale, top, 20 * scale, 38 * scale), 10 * scale));
        context.DrawLine(pen, new Point(cx - 18 * scale, cy + 4 * scale),
            new Point(cx - 16 * scale, cy + 17 * scale));
        context.DrawLine(pen, new Point(cx + 18 * scale, cy + 4 * scale),
            new Point(cx + 16 * scale, cy + 17 * scale));
        context.DrawLine(pen, new Point(cx - 16 * scale, cy + 17 * scale),
            new Point(cx, cy + 24 * scale));
        context.DrawLine(pen, new Point(cx + 16 * scale, cy + 17 * scale),
            new Point(cx, cy + 24 * scale));
        context.DrawLine(pen, new Point(cx, cy + 24 * scale),
            new Point(cx, cy + 31 * scale));
        context.DrawLine(pen, new Point(cx - 11 * scale, cy + 31 * scale),
            new Point(cx + 11 * scale, cy + 31 * scale));
    }

    private static void DrawErrorMark(DrawingContext context, double cx, double cy, double scale)
    {
        context.DrawLine(ErrorInk, new Point(cx, cy - 22 * scale),
            new Point(cx, cy + 10 * scale));
        context.DrawEllipse(ErrorDot, null, Circle(cx, cy + 22 * scale, 3.5 * scale));
    }

    private static void DrawCircle(DrawingContext context, double cx, double cy,
        double radius, IBrush brush) => context.DrawEllipse(brush, null, Circle(cx, cy, radius));

    private static Rect Circle(double cx, double cy, double radius) =>
        new(cx - radius, cy - radius, radius * 2, radius * 2);

    private static Pen Stroke(string color, double width) =>
        new(new SolidColorBrush(Color.Parse(color)), width);

    private static IBrush Gradient(string center, string edge)
    {
        return new RadialGradientBrush
        {
            GradientOrigin = new RelativePoint(0.36, 0.28, RelativeUnit.Relative),
            GradientStops = new GradientStops
            {
                new(Color.Parse(center), 0),
                new(Color.Parse(edge), 1)
            }
        };
    }

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint parameter,
        [MarshalAs(UnmanagedType.Bool)] out bool value, uint update);
}
