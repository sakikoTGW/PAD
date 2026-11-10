using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Pad.Views;

/// <summary>
/// Motion for what a style trigger cannot express: page entrances, staggered lists, panes
/// and overlays. Two rules hold everywhere in this file:
///
/// 1. Only opacity and a Transform move. Nothing here animates layout, so no frame inside
///    a motion reflows the window.
/// 2. Entrances settle like a critically damped spring: fast start, long tail, no
///    overshoot. Bounce is reserved for things a gesture threw, and nothing in PAD's
///    chrome qualifies — see .agents/skills/emotion-design.
/// </summary>
public static class Fx
{
    static Fx()
    {
        // Wheel notches jump by a whole line by default. Rewrite every ScrollViewer so the
        // offset travels linearly instead — nested viewers still win when they can move.
        EventManager.RegisterClassHandler(
            typeof(ScrollViewer),
            UIElement.PreviewMouseWheelEvent,
            new MouseWheelEventHandler(OnScrollViewerWheel));
        // Buttons already lift 1px on IsMouseOver; add a light lateral follow toward the pointer.
        EventManager.RegisterClassHandler(
            typeof(Button), Mouse.MouseMoveEvent, new MouseEventHandler(OnButtonFollowMove));
        EventManager.RegisterClassHandler(
            typeof(Button), Mouse.MouseLeaveEvent, new MouseEventHandler(OnButtonFollowLeave));
    }

    static bool? _forced;
    static double _speed = 1;

    // Cruise mostly flat, then a short sticky tail when the notch lands — not a spring.
    static readonly PowerEase ScrollSettle = new() { Power = 2.8, EasingMode = EasingMode.EaseOut };

    /// <summary>
    /// Animatable stand-in for <see cref="ScrollViewer.VerticalOffset"/> (read-only).
    /// Each frame writes the real offset; wheel gestures retarget this DP.
    /// </summary>
    static readonly DependencyProperty ScrollYProperty = DependencyProperty.RegisterAttached(
        "ScrollY", typeof(double), typeof(Fx), new PropertyMetadata(0.0, OnScrollYChanged));

    static readonly DependencyProperty ScrollTargetYProperty = DependencyProperty.RegisterAttached(
        "ScrollTargetY", typeof(double), typeof(Fx), new PropertyMetadata(double.NaN));

    /// <summary>
    /// Keep the vertical bar's column, and fade the bar instead of collapsing it.
    /// Collapsing changes the content width and the list jumps.
    /// </summary>
    public static readonly DependencyProperty FadeBarProperty = DependencyProperty.RegisterAttached(
        "FadeBar", typeof(bool), typeof(Fx), new PropertyMetadata(false, OnFadeBarChanged));

    static readonly DependencyProperty FadeBarShownProperty = DependencyProperty.RegisterAttached(
        "FadeBarShown", typeof(bool?), typeof(Fx), new PropertyMetadata(null));

    public static void SetFadeBar(DependencyObject d, bool value) => d.SetValue(FadeBarProperty, value);

    public static bool GetFadeBar(DependencyObject d) => (bool)d.GetValue(FadeBarProperty);

    /// <summary>
    /// Spin a fresh rotate transform. One declared in a sealed template is frozen,
    /// and animating it throws instead of turning.
    /// </summary>
    public static readonly DependencyProperty SpinProperty = DependencyProperty.RegisterAttached(
        "Spin", typeof(bool), typeof(Fx), new PropertyMetadata(false, OnSpinChanged));

    public static void SetSpin(DependencyObject d, bool value) => d.SetValue(SpinProperty, value);

    public static bool GetSpin(DependencyObject d) => (bool)d.GetValue(SpinProperty);

    static void OnSpinChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el) return;
        el.IsVisibleChanged -= Spin_Visible;
        if (e.NewValue is not true) return;
        el.IsVisibleChanged += Spin_Visible;
        if (el.IsVisible) StartSpin(el);
    }

    static void Spin_Visible(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is FrameworkElement { IsVisible: true } el) StartSpin(el);
    }

    static void StartSpin(FrameworkElement el)
    {
        var spin = new RotateTransform();
        el.RenderTransformOrigin = new Point(0.5, 0.5);
        el.RenderTransform = spin;
        spin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(900))
        {
            RepeatBehavior = RepeatBehavior.Forever,
        });
    }

    static void OnFadeBarChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer sv) return;
        sv.Loaded -= FadeBar_Loaded;
        sv.ScrollChanged -= FadeBar_Scroll;
        if (e.NewValue is not true) return;
        sv.Loaded += FadeBar_Loaded;
        sv.ScrollChanged += FadeBar_Scroll;
        if (sv.IsLoaded) FadeBar_Loaded(sv, new RoutedEventArgs());
    }

    static void FadeBar_Loaded(object sender, RoutedEventArgs e) =>
        ApplyBarFade((ScrollViewer)sender, animate: false);

    static void FadeBar_Scroll(object sender, ScrollChangedEventArgs e) =>
        ApplyBarFade((ScrollViewer)sender, animate: true);

    static void ApplyBarFade(ScrollViewer sv, bool animate)
    {
        if (sv.Template?.FindName("BarGhost", sv) is not FrameworkElement ghost) return;
        if (sv.Template.FindName("BarGhostShift", sv) is not TranslateTransform shift) return;
        if (sv.Template.FindName("PART_VerticalScrollBar", sv) is ScrollBar bar)
        {
            bar.Visibility = Visibility.Visible;
            bar.Opacity = 0;
        }

        var trackH = (ghost.Parent as FrameworkElement)?.ActualHeight ?? 0;
        var show = sv.ScrollableHeight > 0.5 && sv.ExtentHeight > 0 && trackH > 1;
        if (show)
        {
            var thumbH = Math.Min(trackH, Math.Max(28, trackH * sv.ViewportHeight / sv.ExtentHeight));
            ghost.Height = thumbH;
            var travel = Math.Max(0, trackH - thumbH);
            shift.Y = travel * (sv.VerticalOffset / sv.ScrollableHeight);
        }

        if (sv.GetValue(FadeBarShownProperty) is bool prev && prev == show) return;
        sv.SetValue(FadeBarShownProperty, show);
        var to = show ? 0.7 : 0.0;
        if (!animate || !Animated)
        {
            ghost.BeginAnimation(UIElement.OpacityProperty, null);
            ghost.Opacity = to;
            return;
        }
        ghost.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(to, Ms(280)) { EasingFunction = FadeEase });
    }

    static void OnScrollYChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ScrollViewer sv) sv.ScrollToVerticalOffset((double)e.NewValue);
    }

    static void OnScrollViewerWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || e.Delta == 0 || sender is not ScrollViewer sv) return;

        // Tunneling hits outer viewers first. Only the deepest one that can still move in
        // this direction should claim the notch; others let the event keep traveling.
        var target = FindScrollable(e.OriginalSource as DependencyObject, e.Delta);
        if (!ReferenceEquals(target, sv)) return;

        var step = WheelStep(sv);
        var delta = -(e.Delta / 120.0) * step;
        var max = sv.ScrollableHeight;
        if (max <= 0) return;

        var to = Math.Clamp(ResolveScrollTarget(sv) + delta, 0, max);
        sv.SetValue(ScrollTargetYProperty, to);
        e.Handled = true;
        QuiesceHoverUnder(e.OriginalSource as DependencyObject);

        if (!Animated)
        {
            sv.BeginAnimation(ScrollYProperty, null);
            sv.SetValue(ScrollYProperty, to);
            sv.ScrollToVerticalOffset(to);
            sv.SetValue(ScrollTargetYProperty, double.NaN);
            ResumeHoverUnder(Mouse.DirectlyOver as DependencyObject);
            return;
        }

        var from = sv.VerticalOffset;
        if (Math.Abs(to - from) < 0.5) return;

        // Keep the DP's base value at the live offset. FillBehavior.Stop used to revert to
        // the metadata default (0) and yank the page back to the top every notch — that
        // looked like the release list "refreshing" on the first few wheels.
        sv.SetValue(ScrollYProperty, from);

        // Slightly slower than a hard jump so EaseOut's sticky tail can read.
        var ms = Math.Clamp((int)Math.Round(Math.Abs(to - from) / 1.9), 120, 360);
        var anim = new DoubleAnimation(from, to, Ms(ms))
        {
            EasingFunction = ScrollSettle,
            FillBehavior = FillBehavior.HoldEnd,
        };
        anim.Completed += (_, _) =>
        {
            // A newer wheel notch already retargeted — let that animation finish the pin.
            if (sv.GetValue(ScrollTargetYProperty) is double live
                && !double.IsNaN(live)
                && Math.Abs(live - to) > 0.5)
            {
                return;
            }
            sv.SetValue(ScrollYProperty, to);
            sv.BeginAnimation(ScrollYProperty, null);
            sv.ScrollToVerticalOffset(to);
            sv.SetValue(ScrollTargetYProperty, double.NaN);
            ResumeHoverUnder(Mouse.DirectlyOver as DependencyObject);
        };
        sv.BeginAnimation(ScrollYProperty, anim);
    }

    static double ResolveScrollTarget(ScrollViewer sv)
    {
        if (sv.GetValue(ScrollTargetYProperty) is double t && !double.IsNaN(t)) return t;
        return sv.VerticalOffset;
    }

    static double WheelStep(ScrollViewer sv)
    {
        var lines = SystemParameters.WheelScrollLines;
        if (lines > 0) return Math.Max(16, lines * 16);
        // 0 = "scroll one page" (accessibility setting).
        return Math.Max(sv.ViewportHeight * 0.9, 48);
    }

    static ScrollViewer? FindScrollable(DependencyObject? start, int wheelDelta)
    {
        for (var d = start; d is not null; d = ParentOf(d))
        {
            if (d is not ScrollViewer sv || sv.ScrollableHeight <= 0) continue;
            // delta > 0 = wheel up = content moves down = offset decreases.
            if (wheelDelta > 0 && sv.VerticalOffset > 0) return sv;
            if (wheelDelta < 0 && sv.VerticalOffset < sv.ScrollableHeight - 0.5) return sv;
        }
        return null;
    }

    static DependencyObject? ParentOf(DependencyObject? d) => d switch
    {
        null => null,
        Visual or System.Windows.Media.Media3D.Visual3D => VisualTreeHelper.GetParent(d),
        FrameworkContentElement fce => fce.Parent,
        _ => LogicalTreeHelper.GetParent(d),
    };

    /// <summary>
    /// Off when Windows says so, or when the user flipped 个性化 → 动画. A forced false
    /// always wins; a forced true still respects the OS off-switch.
    /// </summary>
    public static bool Animated =>
        SystemParameters.ClientAreaAnimation && (_forced ?? true);

    /// <summary>
    /// Windows reduced motion but the user did not switch animation off: travel is dropped,
    /// comprehension-grade cross-fades stay (Apple: gentler, not nothing).
    /// </summary>
    static bool Reduced => !SystemParameters.ClientAreaAnimation && (_forced ?? true);

    public static void SetAnimate(bool on) => _forced = on;

    /// <summary>1 = default. Above 1 stretches durations (slower), below 1 shortens them.</summary>
    public static void SetSpeed(double speed) => _speed = Math.Clamp(speed, 0.5, 2.0);

    static long _busyUntil;

    /// <summary>
    /// True while a page swap is animating. Background refreshes (process table, disk
    /// scans) check this and skip a beat instead of dropping frames inside the motion.
    /// </summary>
    public static bool Busy => Environment.TickCount64 < _busyUntil;

    public static void BusyFor(int ms) => _busyUntil = Environment.TickCount64 + Math.Max(0, ms);

    // WPF has no spring easing. A steep exponential PowerEase is the closest stand-in for a
    // critically damped settle: the same immediate departure from rest, the same long tail
    // into the target, and no overshoot. The cubic built-ins start too gently to read as
    // "direct" — that is the difference between an interface that responds and one that
    // merely finishes.
    static readonly PowerEase Settle = new() { Power = 4.6, EasingMode = EasingMode.EaseOut };
    static readonly PowerEase Exit = new() { Power = 2.4, EasingMode = EasingMode.EaseIn };
    static readonly PowerEase FadeEase = new() { Power = 2.4, EasingMode = EasingMode.EaseOut };

    public static readonly DependencyProperty EnterProperty = DependencyProperty.RegisterAttached(
        "Enter", typeof(string), typeof(Fx), new PropertyMetadata(null, OnEnterChanged));

    static readonly DependencyProperty EnteredProperty = DependencyProperty.RegisterAttached(
        "Entered", typeof(bool), typeof(Fx), new PropertyMetadata(false));

    public static void SetEnter(DependencyObject d, string value) => d.SetValue(EnterProperty, value);

    public static string? GetEnter(DependencyObject d) => (string?)d.GetValue(EnterProperty);

    /// <summary>
    /// <c>lift</c>: card rises, shadow deepens, and the face gently tracks the pointer
    /// via a shared lerp loop (not per-move storyboards — those feel hard and stutter).
    /// Put it on an inner element — the outer one may already own an Enter transform.
    /// </summary>
    public static readonly DependencyProperty HoverProperty = DependencyProperty.RegisterAttached(
        "Hover", typeof(string), typeof(Fx), new PropertyMetadata(null, OnHoverChanged));

    public static void SetHover(DependencyObject d, string value) => d.SetValue(HoverProperty, value);

    public static string? GetHover(DependencyObject d) => (string?)d.GetValue(HoverProperty);

    static void OnHoverChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        fe.MouseEnter -= HoverIn;
        fe.MouseLeave -= HoverOut;
        fe.MouseMove -= HoverMove;
        if (e.NewValue is not string { Length: > 0 }) return;
        fe.MouseEnter += HoverIn;
        fe.MouseLeave += HoverOut;
        fe.MouseMove += HoverMove;
    }

    /// <summary>
    /// While a wheel scroll is traveling, cards sliding under a still pointer fire a storm of
    /// MouseEnter/Leave. Suppress lift so the store grid does not look like it is refreshing.
    /// </summary>
    static long _hoverQuietUntil;

    // Magnetic follow: pointer only writes a target; one Rendering tick lerps every live track
    // (same recipe as soft web magnets — offset = Δcenter × strength, then lerp ≈ 0.14).
    const double FollowLerp = 0.14;
    const double CardFollowStrength = 0.055;
    const double CardFollowMax = 3.5;
    const double CardFollowLift = -2.0;
    const double BtnFollowStrength = 0.10;
    const double BtnFollowMax = 2.0;

    sealed class FollowTrack
    {
        public required FrameworkElement Fe;
        public required TranslateTransform Shift;
        public double X, Y, Tx, Ty;
        public double Lift;
        public bool XOnly;
        public bool Active;
    }

    static readonly ConditionalWeakTable<FrameworkElement, FollowTrack> FollowByFe = new();
    static readonly List<FollowTrack> FollowLive = [];
    static bool _followTicking;

    static void HoverIn(object sender, MouseEventArgs e)
    {
        if (Environment.TickCount64 < _hoverQuietUntil) return;
        var fe = (FrameworkElement)sender;
        HoverShadow(fe, lifted: true);
        AimFollow(fe, e.GetPosition(fe), CardFollowLift, CardFollowStrength, CardFollowMax, xOnly: false);
    }

    static void HoverMove(object sender, MouseEventArgs e)
    {
        if (Environment.TickCount64 < _hoverQuietUntil) return;
        if (!Animated) return;
        var fe = (FrameworkElement)sender;
        AimFollow(fe, e.GetPosition(fe), CardFollowLift, CardFollowStrength, CardFollowMax, xOnly: false);
    }

    static void HoverOut(object sender, MouseEventArgs e)
    {
        if (Environment.TickCount64 < _hoverQuietUntil) return;
        var fe = (FrameworkElement)sender;
        HoverShadow(fe, lifted: false);
        ReleaseFollow(fe);
    }

    static void QuiesceHoverUnder(DependencyObject? start)
    {
        _hoverQuietUntil = Environment.TickCount64 + MsValue(420);
        for (var d = start; d is not null; d = ParentOf(d))
        {
            if (d is FrameworkElement fe && GetHover(fe) is { Length: > 0 })
            {
                HoverShadow(fe, lifted: false);
                ReleaseFollow(fe);
            }
        }
    }

    static void ResumeHoverUnder(DependencyObject? start)
    {
        for (var d = start; d is not null; d = ParentOf(d))
        {
            if (d is not FrameworkElement fe || GetHover(fe) is not { Length: > 0 }) continue;
            if (!fe.IsMouseOver) return;
            HoverShadow(fe, lifted: true);
            AimFollow(fe, Mouse.GetPosition(fe), CardFollowLift, CardFollowStrength, CardFollowMax, xOnly: false);
            return;
        }
    }

    static void AimFollow(FrameworkElement fe, Point local, double lift, double strength, double maxPull, bool xOnly)
    {
        if (!Animated) return;
        var shift = EnsureShift(fe);
        if (shift is null) return;

        var track = GetFollow(fe, shift, lift, xOnly);
        var cx = Math.Max(fe.ActualWidth, 1) * 0.5;
        var cy = Math.Max(fe.ActualHeight, 1) * 0.5;
        var dx = Math.Clamp((local.X - cx) * strength, -maxPull, maxPull);
        var dy = Math.Clamp((local.Y - cy) * strength, -maxPull, maxPull);
        track.Active = true;
        track.Lift = lift;
        track.Tx = dx;
        track.Ty = xOnly ? 0 : dy + lift;
        KickFollowTick();
    }

    static void ReleaseFollow(FrameworkElement fe)
    {
        if (!FollowByFe.TryGetValue(fe, out var track)) return;
        track.Active = false;
        track.Tx = 0;
        track.Ty = 0;
        KickFollowTick();
    }

    static FollowTrack GetFollow(FrameworkElement fe, TranslateTransform shift, double lift, bool xOnly)
    {
        if (FollowByFe.TryGetValue(fe, out var track))
        {
            track.Shift = shift;
            track.XOnly = xOnly;
            return track;
        }

        track = new FollowTrack
        {
            Fe = fe,
            Shift = shift,
            Lift = lift,
            XOnly = xOnly,
            X = shift.X,
            Y = xOnly ? 0 : shift.Y,
        };
        FollowByFe.Add(fe, track);
        FollowLive.Add(track);
        return track;
    }

    static void KickFollowTick()
    {
        if (_followTicking) return;
        _followTicking = true;
        CompositionTarget.Rendering += OnFollowFrame;
    }

    static void OnFollowFrame(object? sender, EventArgs e)
    {
        var alive = false;
        for (var i = FollowLive.Count - 1; i >= 0; i--)
        {
            var t = FollowLive[i];
            if (!t.Fe.IsLoaded || t.Fe.RenderTransform is not TranslateTransform)
            {
                FollowLive.RemoveAt(i);
                FollowByFe.Remove(t.Fe);
                continue;
            }

            t.X += (t.Tx - t.X) * FollowLerp;
            if (!t.XOnly) t.Y += (t.Ty - t.Y) * FollowLerp;

            // Kill any leftover storyboard on the axes we own so lerp is not overwritten.
            t.Shift.BeginAnimation(TranslateTransform.XProperty, null);
            t.Shift.X = t.X;
            if (!t.XOnly)
            {
                t.Shift.BeginAnimation(TranslateTransform.YProperty, null);
                t.Shift.Y = t.Y;
            }

            var settled = Math.Abs(t.Tx - t.X) < 0.04
                          && (t.XOnly || Math.Abs(t.Ty - t.Y) < 0.04);
            if (t.Active || !settled)
            {
                alive = true;
                continue;
            }

            t.Shift.BeginAnimation(TranslateTransform.XProperty, null);
            t.Shift.X = 0;
            if (!t.XOnly)
            {
                t.Shift.BeginAnimation(TranslateTransform.YProperty, null);
                t.Shift.Y = 0;
            }
            FollowLive.RemoveAt(i);
            FollowByFe.Remove(t.Fe);
        }

        if (alive || FollowLive.Count > 0) return;
        CompositionTarget.Rendering -= OnFollowFrame;
        _followTicking = false;
    }

    static TranslateTransform? EnsureShift(FrameworkElement fe)
    {
        if (fe.RenderTransform is TranslateTransform live && !live.IsFrozen) return live;
        var shift = new TranslateTransform();
        fe.RenderTransform = shift;
        return shift;
    }

    static readonly ConditionalWeakTable<System.Windows.Media.Effects.DropShadowEffect, ShadowRest> ShadowRests = new();

    sealed class ShadowRest
    {
        public double Blur, Depth, Alpha;
    }

    static void HoverShadow(FrameworkElement fe, bool lifted)
    {
        if (!Animated) return;
        if (fe.Effect is not System.Windows.Media.Effects.DropShadowEffect sh) return;
        if (sh.IsFrozen)
        {
            sh = (System.Windows.Media.Effects.DropShadowEffect)sh.Clone();
            fe.Effect = sh;
        }
        // 逐帧改模糊半径会把这块拉回软件渲染。阴影只在抬起/放下时改一次，位移仍走变换。
        if (!ShadowRests.TryGetValue(sh, out var rest))
        {
            rest = new ShadowRest { Blur = sh.BlurRadius, Depth = sh.ShadowDepth, Alpha = sh.Opacity };
            ShadowRests.Add(sh, rest);
        }
        sh.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.BlurRadiusProperty, null);
        sh.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.ShadowDepthProperty, null);
        sh.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, null);
        sh.BlurRadius = lifted ? rest.Blur + 8 : rest.Blur;
        sh.ShadowDepth = lifted ? rest.Depth + 2 : rest.Depth;
        sh.Opacity = lifted ? Math.Min(1, rest.Alpha + 0.07) : rest.Alpha;
    }

    static void OnButtonFollowMove(object sender, MouseEventArgs e)
    {
        if (!Animated || Environment.TickCount64 < _hoverQuietUntil) return;
        if (sender is not Button { IsEnabled: true, IsPressed: false, IsMouseOver: true } btn) return;
        if (ButtonLift(btn) is not { } lift) return;

        // X only — template storyboard still owns the 1px Y lift.
        if (!FollowByFe.TryGetValue(btn, out var track))
        {
            track = new FollowTrack
            {
                Fe = btn,
                Shift = lift,
                XOnly = true,
                X = lift.X,
            };
            FollowByFe.Add(btn, track);
            FollowLive.Add(track);
        }
        else
        {
            track.Shift = lift;
            track.XOnly = true;
        }

        var local = e.GetPosition(btn);
        var cx = Math.Max(btn.ActualWidth, 1) * 0.5;
        track.Active = true;
        track.Tx = Math.Clamp((local.X - cx) * BtnFollowStrength, -BtnFollowMax, BtnFollowMax);
        track.Ty = 0;
        KickFollowTick();
    }

    static void OnButtonFollowLeave(object sender, MouseEventArgs e)
    {
        if (sender is Button btn) ReleaseFollow(btn);
    }

    static TranslateTransform? ButtonLift(Button btn)
    {
        btn.ApplyTemplate();
        return btn.Template?.FindName("lift", btn) as TranslateTransform;
    }

    /// <summary>A short press-confirm: the element settles up from slightly small. For chips.</summary>
    public static void Pop(FrameworkElement fe)
    {
        if (!Animated) return;
        fe.RenderTransformOrigin = new Point(0.5, 0.5);
        var scale = new ScaleTransform(0.9, 0.9);
        fe.RenderTransform = scale;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.9, 1, Ms(340)) { EasingFunction = Settle });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.9, 1, Ms(340)) { EasingFunction = Settle });
    }

    static bool GetEntered(DependencyObject d) => (bool)d.GetValue(EnteredProperty);

    static void SetEntered(DependencyObject d, bool value) => d.SetValue(EnteredProperty, value);

    static void OnEnterChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        // title / label: first-show only via RevealEnter / MarkEnter — not Loaded
        // (Collapsed panes already Load at startup, which would burn the one-shot).
        if (e.NewValue as string is "title" or "label")
        {
            fe.Loaded -= OnLoaded;
            return;
        }
        fe.Loaded -= OnLoaded;
        fe.Loaded += OnLoaded;
    }

    static void OnLoaded(object sender, RoutedEventArgs e)
    {
        var fe = (FrameworkElement)sender;
        fe.Loaded -= OnLoaded;
        if (!Animated)
        {
            CrossFade(fe);
            return;
        }

        var rise = GetEnter(fe) == "card" ? 10.0 : 8.0;
        // Hold the start pose before the stagger delay. BeginTime alone leaves Opacity at 1
        // until the clock starts, so the row flashes fully opaque.
        fe.Opacity = 0;
        var shift = new TranslateTransform { Y = rise };
        fe.RenderTransform = shift;

        // 30ms of stagger between rows, capped at 210ms: enough to read as a cascade,
        // short enough that the last row is not waiting a third of a second.
        var delay = TimeSpan.FromMilliseconds(Math.Min(IndexOf(fe) * MsValue(30), MsValue(210)));
        fe.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Ms(180))
        {
            BeginTime = delay,
            EasingFunction = FadeEase,
        });
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(rise, 0, Ms(300))
        {
            BeginTime = delay,
            EasingFunction = Settle,
        });
    }

    /// <summary>
    /// Claim title/label enters as already shown (no motion). Use after a parent PageIn so a
    /// later sub-page revisit does not replay them.
    /// </summary>
    public static void MarkEnter(DependencyObject root)
    {
        foreach (var fe in CollectEnter(root, "title", "label"))
            SetEntered(fe, true);
    }

    /// <summary>
    /// Cascade unrevealed heads, then field labels once the first title is halfway —
    /// same idea as the plugin store chrome across shelf switches.
    /// </summary>
    public static void RevealEnter(DependencyObject root)
    {
        var titles = CollectEnter(root, "title");
        titles.RemoveAll(GetEntered);
        var labels = CollectEnter(root, "label");
        labels.RemoveAll(GetEntered);
        if (titles.Count == 0 && labels.Count == 0) return;

        if (root is UIElement page)
        {
            if (titles.Count > 0) OrderByPosition(page, titles);
            if (labels.Count > 0) OrderByPosition(page, labels);
        }

        if (!Animated)
        {
            foreach (var fe in titles)
                SnapEnter(fe);
            foreach (var fe in labels)
                SnapEnter(fe);
            return;
        }

        const int titleFade = 200;
        const int titleRise = 300;
        const int labelFade = 160;
        const int labelRise = 220;
        // Labels start when the first title fade is halfway through.
        var labelGate = titles.Count > 0
            ? TimeSpan.FromMilliseconds(MsValue(titleFade / 2))
            : TimeSpan.Zero;

        for (var i = 0; i < titles.Count; i++)
            PlayEnter(titles[i], rise: 8, fadeMs: titleFade, riseMs: titleRise, At(i, 36, 280));

        for (var i = 0; i < labels.Count; i++)
            PlayEnter(labels[i], rise: 5, fadeMs: labelFade, riseMs: labelRise,
                labelGate + At(i, 22, 200));
    }

    static void SnapEnter(FrameworkElement fe)
    {
        SetEntered(fe, true);
        fe.BeginAnimation(UIElement.OpacityProperty, null);
        fe.Opacity = 1;
        fe.RenderTransform = Transform.Identity;
    }

    static void PlayEnter(FrameworkElement fe, double rise, int fadeMs, int riseMs, TimeSpan delay)
    {
        SetEntered(fe, true);
        fe.BeginAnimation(UIElement.OpacityProperty, null);
        if (fe.RenderTransform is TranslateTransform old)
            old.BeginAnimation(TranslateTransform.YProperty, null);
        fe.Opacity = 0;
        var shift = new TranslateTransform { Y = rise };
        fe.RenderTransform = shift;
        fe.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Ms(fadeMs))
        {
            BeginTime = delay,
            EasingFunction = FadeEase,
        });
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(rise, 0, Ms(riseMs))
        {
            BeginTime = delay,
            EasingFunction = Settle,
        });
    }

    static List<FrameworkElement> CollectEnter(DependencyObject root, params string[] kinds)
    {
        var into = new List<FrameworkElement>();
        CollectEnterWalk(root, 0, into, kinds);
        return into;
    }

    static bool IsEnterKind(string? kind, string[] kinds)
    {
        if (kind is null) return false;
        foreach (var k in kinds)
            if (k == kind) return true;
        return false;
    }

    static void CollectEnterWalk(DependencyObject node, int depth, List<FrameworkElement> into,
        string[] kinds)
    {
        if (depth > 24 || into.Count >= 48) return;
        if (node is FrameworkElement fe && IsEnterKind(GetEnter(fe), kinds)
            && fe.Visibility == Visibility.Visible)
            into.Add(fe);
        foreach (var child in Children(node))
            CollectEnterWalk(child, depth + 1, into, kinds);
    }

    /// <summary>Position of an element's container inside the list that generated it.</summary>
    static int IndexOf(DependencyObject? node)
    {
        for (var depth = 0; node is not null && depth < 64; depth++)
        {
            var owner = ItemsControl.ItemsControlFromItemContainer(node);
            if (owner is not null)
            {
                var i = owner.ItemContainerGenerator.IndexFromContainer(node);
                return i < 0 ? 0 : i;
            }
            node = VisualTreeHelper.GetParent(node);
        }
        return 0;
    }

    // ------------------------------------------------------------------ page switch

    /// <summary>
    /// Entrance choreography for a whole page. The container is visible at once — a page
    /// switch must never leave a blank rectangle — and the motion belongs to its parts.
    /// Every page picks a different shape, so switching never feels templated.
    /// </summary>
    public static void EnterPage(UIElement page, PageMotion motion)
    {
        page.BeginAnimation(UIElement.OpacityProperty, null);
        page.Opacity = 1;
        page.RenderTransform = Transform.Identity;
        if (!Animated || page is not DependencyObject root) return;

        var targets = StaggerTargets(root);
        if (motion == PageMotion.Wave) OrderByPosition(page, targets);
        for (var i = 0; i < targets.Count; i++)
            Pose(targets[i], motion, i);
    }

    /// <summary>Tagged elements (Fx.Enter) first; a page without tags gets its sections.</summary>
    static List<FrameworkElement> StaggerTargets(DependencyObject root)
    {
        var tagged = new List<FrameworkElement>();
        CollectTagged(root, 0, tagged);
        if (tagged.Count > 0) return tagged;

        var content = Descend(root, 0);
        var sections = new List<FrameworkElement>();
        if (content is null) return sections;
        VisibleChildren(content, sections);
        // A rail + one pane would stagger two elements and read as nothing. Open the wrappers
        // a couple of levels so the parts the user actually recognises are what arrives.
        for (var pass = 0; pass < 3 && sections.Count is > 0 and < 4; pass++)
            sections = Expand(sections);
        if (sections.Count > 14) sections.RemoveRange(14, sections.Count - 14);
        return sections;
    }

    static void VisibleChildren(DependencyObject node, List<FrameworkElement> into)
    {
        foreach (var child in Children(node))
        {
            if (child is FrameworkElement fe && fe.Visibility == Visibility.Visible && fe.ActualWidth > 1)
                into.Add(fe);
        }
    }

    static List<FrameworkElement> Expand(List<FrameworkElement> current)
    {
        var next = new List<FrameworkElement>();
        foreach (var fe in current)
        {
            var kids = new List<FrameworkElement>();
            VisibleChildren(fe, kids);
            if (kids.Count == 0) next.Add(fe);
            else next.AddRange(kids);
        }
        return next;
    }

    static void CollectTagged(DependencyObject node, int depth, List<FrameworkElement> into)
    {
        if (depth > 14 || into.Count >= 14) return;
        if (node is FrameworkElement fe && GetEnter(fe) is not null
            && fe.Visibility == Visibility.Visible && fe.ActualWidth > 1)
        {
            into.Add(fe);
            return;
        }
        foreach (var child in Children(node)) CollectTagged(child, depth + 1, into);
    }

    /// <summary>A wave travels across the screen, not through the tree.</summary>
    static void OrderByPosition(UIElement page, List<FrameworkElement> items)
    {
        var keyed = new List<(double Key, FrameworkElement Fe)>(items.Count);
        foreach (var fe in items)
        {
            var key = 0.0;
            try
            {
                var p = fe.TranslatePoint(new Point(0, 0), page);
                key = p.Y * 3 + p.X * 0.08;
            }
            catch { /* not measured yet: keep tree order */ }
            keyed.Add((key, fe));
        }
        keyed.Sort((a, b) => a.Key.CompareTo(b.Key));
        items.Clear();
        foreach (var (_, fe) in keyed) items.Add(fe);
    }

    /// <summary>Skip single-child wrappers (UserControl -> Border -> Grid) down to the layout root.</summary>
    static DependencyObject? Descend(DependencyObject node, int depth)
    {
        if (depth > 8) return null;
        var kids = Children(node);
        if (kids.Count > 1) return node;
        if (kids.Count == 0) return node;
        return Descend(kids[0], depth + 1);
    }

    static List<DependencyObject> Children(DependencyObject node)
    {
        var list = new List<DependencyObject>();
        var n = VisualTreeHelper.GetChildrenCount(node);
        if (n > 0)
        {
            for (var i = 0; i < n; i++) list.Add(VisualTreeHelper.GetChild(node, i));
            return list;
        }
        foreach (var child in LogicalTreeHelper.GetChildren(node))
            if (child is DependencyObject d) list.Add(d);
        return list;
    }

    /// <summary>
    /// One element's arrival. The shape of the motion — distance, scale, slide, stagger —
    /// is what makes 设置 read as one-by-one and 管理 read as a wave.
    /// </summary>
    static void Pose(FrameworkElement fe, PageMotion motion, int index)
    {
        double rise, scale, slide;
        int dur, fade;
        TimeSpan delay;
        switch (motion)
        {
            case PageMotion.Rise:
                (rise, scale, slide, dur, fade) = (14, 1, 0, 300, 180);
                delay = At(index, 36, 240);
                break;
            case PageMotion.Cards:
                (rise, scale, slide, dur, fade) = (8, 0.96, 0, 320, 180);
                delay = At(index, 30, 240);
                break;
            case PageMotion.Rows:
                (rise, scale, slide, dur, fade) = (6, 1, 0, 240, 140);
                delay = At(index, 16, 200);
                break;
            case PageMotion.Slide:
                (rise, scale, slide, dur, fade) = (4, 1, -12, 260, 170);
                delay = At(index, 24, 220);
                break;
            case PageMotion.Sequential:
                // Deliberately slow stagger: the page assembles itself, part by part.
                (rise, scale, slide, dur, fade) = (10, 1, 0, 280, 160);
                delay = At(index, 58, 520);
                break;
            case PageMotion.Wave:
                (rise, scale, slide, dur, fade) = (6, 0.97, 0, 300, 170);
                delay = At(index, 26, 300);
                break;
            case PageMotion.Combo:
                // Two beats at once: the lead block travels long and slow, the rest cascade
                // underneath it on their own offset.
                if (index == 0)
                {
                    (rise, scale, slide, dur, fade) = (20, 1, 0, 420, 220);
                    delay = TimeSpan.Zero;
                }
                else
                {
                    (rise, scale, slide, dur, fade) = (8, 0.96, 0, 300, 170);
                    delay = TimeSpan.FromMilliseconds(120 + Math.Min((index - 1) * 34, 220));
                }
                break;
            case PageMotion.Cascade:
                // Unified tab/pane enter: clear top→bottom beat, readable gap between parts.
                (rise, scale, slide, dur, fade) = (10, 1, 0, 280, 170);
                delay = At(index, 48, 560);
                break;
            default: // Drift
                (rise, scale, slide, dur, fade) = (8, 0.985, 0, 300, 180);
                delay = At(index, 26, 240);
                break;
        }

        var group = new TransformGroup();
        var s = new ScaleTransform(scale, scale);
        var t = new TranslateTransform { X = slide, Y = rise };
        group.Children.Add(s);
        group.Children.Add(t);
        fe.RenderTransform = group;
        fe.RenderTransformOrigin = new Point(0.5, 0.5);
        fe.Opacity = 0;

        fe.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Ms(fade))
        {
            BeginTime = delay,
            EasingFunction = FadeEase,
        });
        if (rise != 0)
            t.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(rise, 0, Ms(dur))
            {
                BeginTime = delay,
                EasingFunction = Settle,
            });
        if (slide != 0)
            t.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(slide, 0, Ms(dur))
            {
                BeginTime = delay,
                EasingFunction = Settle,
            });
        if (scale != 1)
        {
            s.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(scale, 1, Ms(dur))
            {
                BeginTime = delay,
                EasingFunction = Settle,
            });
            s.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(scale, 1, Ms(dur))
            {
                BeginTime = delay,
                EasingFunction = Settle,
            });
        }
    }

    static TimeSpan At(int index, int stagger, int cap) =>
        TimeSpan.FromMilliseconds(Math.Min(index * stagger, cap));

    /// <summary>
    /// Sync blank before the first paint after a Content swap. Call this the instant the
    /// page is attached so the old layout never flashes at full opacity.
    /// </summary>
    public static void PrepareEnter(UIElement page)
    {
        page.BeginAnimation(UIElement.OpacityProperty, null);
        page.Opacity = 1;
        page.RenderTransform = Transform.Identity;
        var (rail, main) = SplitRailMain(page);
        if (rail is null && main is null)
        {
            HideBundle(page);
            return;
        }
        if (rail is not null) HideRail(rail);
        if (main is not null) HideMain(main);
    }

    /// <summary>
    /// Enter after a white hold: main fades in + scales up; left rail rises from below.
    /// Same choreography for top tabs and in-page panes (pane-only = main path).
    /// <paramref name="withShell"/> — running beside a SizeTo morph: opacity only (no scale /
    /// rail stagger). Scale+stagger on top of per-frame shell layout was the main hitch.
    /// </summary>
    public static void PageIn(UIElement page, bool withShell = false)
    {
        PrepareEnter(page);
        if (!Animated) { RevealNow(page); return; }
        if (page is FrameworkElement fe) fe.UpdateLayout();

        var (rail, main) = SplitRailMain(page);
        if (withShell)
        {
            // No white hold — SizeTo already owns the beat; keep compositor light.
            if (rail is null && main is null) { PlayFadeOnly(page); return; }
            if (main is not null) PlayFadeOnly(main);
            if (rail is not null) PlayFadeOnly(rail);
            return;
        }

        // Hold full white (~wash showing through) then reveal.
        var hold = TimeSpan.FromMilliseconds(MsValue(70));

        if (rail is null && main is null)
        {
            PlayMain(page, hold);
            return;
        }
        if (main is not null) PlayMain(main, hold);
        if (rail is not null) PlayRail(rail, hold + TimeSpan.FromMilliseconds(MsValue(40)));
    }

    static void PlayFadeOnly(UIElement el)
    {
        el.BeginAnimation(UIElement.OpacityProperty, null);
        el.RenderTransform = Transform.Identity;
        el.Opacity = 0;
        el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Ms(320))
        {
            EasingFunction = FadeEase,
        });
        if (el is not FrameworkElement rail) return;
        foreach (var item in RailItems(rail))
        {
            item.BeginAnimation(UIElement.OpacityProperty, null);
            item.RenderTransform = Transform.Identity;
            item.Opacity = 1;
        }
    }

    /// <summary>
    /// Leave into white: fade the whole page out (wash behind shows). Opacity stays 0 so the
    /// swap never flashes the departing layout; PrepareEnter/PageIn revive the next page.
    /// </summary>
    public static void PageOut(UIElement page, Action? done = null)
    {
        page.BeginAnimation(UIElement.OpacityProperty, null);
        if (!Animated)
        {
            page.Opacity = 0;
            done?.Invoke();
            return;
        }

        var fade = new DoubleAnimation(page.Opacity, 0, Ms(160)) { EasingFunction = Exit };
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            fade.Completed -= handler!;
            page.BeginAnimation(UIElement.OpacityProperty, null);
            page.Opacity = 0;
            page.RenderTransform = Transform.Identity;
            done?.Invoke();
        };
        fade.Completed += handler;
        page.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    /// <summary>
    /// In-page pane enter (sidebar sub-pages). Runs on every switch — not one-shot.
    /// Longer than <see cref="PageOut"/> so arrival reads after leave, instead of a pop.
    /// </summary>
    public static void PaneIn(UIElement page)
    {
        page.BeginAnimation(UIElement.OpacityProperty, null);
        if (page.RenderTransform is TranslateTransform oldTt)
            oldTt.BeginAnimation(TranslateTransform.YProperty, null);
        if (!Animated)
        {
            page.Opacity = 1;
            page.RenderTransform = Transform.Identity;
            return;
        }

        page.Opacity = 0;
        var shift = new TranslateTransform { Y = 10 };
        page.RenderTransform = shift;
        // PageOut is 160ms; enter ~2.4× so the body does not snap in after a slow leave.
        page.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Ms(380))
        {
            EasingFunction = FadeEase,
        });
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, Ms(420))
        {
            EasingFunction = Settle,
        });
    }

    static readonly DependencyProperty ShownTokenProperty = DependencyProperty.RegisterAttached(
        "ShownToken", typeof(int), typeof(Fx), new PropertyMetadata(0));

    /// <summary>
    /// Show or hide a region with a short fade (+ 6px rise on show) instead of a raw
    /// Visibility flip. Idempotent: showing an already-shown element does not replay, and a
    /// late hide cannot collapse something that was shown again meanwhile.
    /// Opacity only — layout space is still taken/freed at the end, never animated.
    /// </summary>
    public static void SetShown(UIElement el, bool shown)
    {
        var token = (int)el.GetValue(ShownTokenProperty) + 1;
        el.SetValue(ShownTokenProperty, token);

        if (!Animated)
        {
            el.BeginAnimation(UIElement.OpacityProperty, null);
            el.Opacity = 1;
            el.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        if (shown) Appear(el);
        else Vanish(el, token);
    }

    /// <summary>Fade a hidden/collapsed element in. No replay if it is already shown.</summary>
    public static void Appear(UIElement el)
    {
        // Already on screen: do nothing. Re-fading a live log box on every AppendText
        // looked like the terminal was refreshing with each line.
        if (el.Visibility == Visibility.Visible) return;

        el.BeginAnimation(UIElement.OpacityProperty, null);
        el.Visibility = Visibility.Visible;
        if (!Animated) { el.Opacity = 1; return; }

        el.Opacity = 0;
        el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Ms(220))
        {
            EasingFunction = FadeEase,
        });

        // Only lend a transform to elements that have none; never fight Enter / Hover.
        if (el.RenderTransform is MatrixTransform { IsFrozen: true } or null
            || ReferenceEquals(el.RenderTransform, Transform.Identity))
        {
            var shift = new TranslateTransform { Y = 6 };
            el.RenderTransform = shift;
            shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(6, 0, Ms(300))
            {
                EasingFunction = Settle,
            });
        }
    }

    static void Vanish(UIElement el, int token)
    {
        if (el.Visibility != Visibility.Visible) return;
        var from = el.Opacity;
        var fade = new DoubleAnimation(from, 0, Ms(130)) { EasingFunction = Exit };
        fade.Completed += (_, _) =>
        {
            if ((int)el.GetValue(ShownTokenProperty) != token) return;
            el.BeginAnimation(UIElement.OpacityProperty, null);
            el.Opacity = 1;
            el.Visibility = Visibility.Collapsed;
        };
        el.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    /// <summary>
    /// Modal/popup window arrival: content fades in with an 8px rise (spec §4: 180ms
    /// ease-out + 8px). Call once from the window's Loaded handler.
    /// </summary>
    public static void DialogIn(Window window)
    {
        if (window.Content is not FrameworkElement root) return;
        if (!Animated) return;
        root.Opacity = 0;
        var shift = new TranslateTransform { Y = 8 };
        root.RenderTransform = shift;
        root.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Ms(200))
        {
            EasingFunction = FadeEase,
        });
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, Ms(300))
        {
            EasingFunction = Settle,
        });
    }

    /// <summary>
    /// Sub-page swap: leave → <paramref name="show"/> (toggle Visibility) → body
    /// <see cref="PaneIn"/> every time + title <see cref="RevealEnter"/> once.
    /// </summary>
    public static void SwapPane(UIElement? leaving, FrameworkElement next, Action show)
    {
        void Arrive()
        {
            show();
            next.UpdateLayout();
            PaneIn(next);
            RevealEnter(next);
        }

        if (Animated && leaving is not null && !ReferenceEquals(leaving, next))
            PageOut(leaving, Arrive);
        else
            Arrive();
    }

    static void RevealNow(UIElement page)
    {
        var (rail, main) = SplitRailMain(page);
        if (rail is null && main is null) { ShowBundle(page); return; }
        if (rail is not null) ShowRailNow(rail);
        if (main is not null) ShowBundle(main);
    }

    static void HideBundle(UIElement el)
    {
        el.BeginAnimation(UIElement.OpacityProperty, null);
        el.Opacity = 0;
        el.RenderTransform = Transform.Identity;
    }

    static void ShowBundle(UIElement el)
    {
        el.BeginAnimation(UIElement.OpacityProperty, null);
        el.Opacity = 1;
        el.RenderTransform = Transform.Identity;
    }

    static void ShowRailNow(FrameworkElement rail)
    {
        ShowBundle(rail);
        foreach (var item in RailItems(rail))
            ShowBundle(item);
    }

    static void HideMain(FrameworkElement main)
    {
        main.BeginAnimation(UIElement.OpacityProperty, null);
        main.Opacity = 0;
        var scale = new ScaleTransform(0.96, 0.96);
        main.RenderTransform = scale;
        main.RenderTransformOrigin = new Point(0.5, 0.45);
    }

    static void HideRail(FrameworkElement rail)
    {
        rail.BeginAnimation(UIElement.OpacityProperty, null);
        rail.Opacity = 0;
        rail.RenderTransform = Transform.Identity;
        foreach (var item in RailItems(rail))
            HideRailItem(item);
    }

    static void HideRailItem(FrameworkElement item)
    {
        item.BeginAnimation(UIElement.OpacityProperty, null);
        item.Opacity = 0;
        item.RenderTransform = new TranslateTransform { Y = 16 };
        item.RenderTransformOrigin = new Point(0.5, 1);
    }

    static void PlayMain(UIElement main, TimeSpan begin)
    {
        if (main is FrameworkElement fe)
        {
            HideMain(fe);
            var scale = (ScaleTransform)fe.RenderTransform;
            fe.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Ms(320))
            {
                BeginTime = begin,
                EasingFunction = FadeEase,
            });
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, Ms(420))
            {
                BeginTime = begin,
                EasingFunction = Settle,
            });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, Ms(420))
            {
                BeginTime = begin,
                EasingFunction = Settle,
            });
            return;
        }
        HideBundle(main);
        main.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Ms(320))
        {
            BeginTime = begin,
            EasingFunction = FadeEase,
        });
    }

    static void PlayRail(FrameworkElement rail, TimeSpan begin)
    {
        var items = RailItems(rail);
        if (items.Count == 0)
        {
            // No parts to cascade — whole rail rises as one.
            rail.Opacity = 0;
            rail.RenderTransform = new TranslateTransform { Y = 28 };
            var shift = (TranslateTransform)rail.RenderTransform;
            rail.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Ms(280))
            {
                BeginTime = begin,
                EasingFunction = FadeEase,
            });
            shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(28, 0, Ms(380))
            {
                BeginTime = begin,
                EasingFunction = Settle,
            });
            return;
        }

        // Shell arrives with the first item so the white hold stays clean.
        rail.Opacity = 0;
        rail.RenderTransform = Transform.Identity;
        rail.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Ms(200))
        {
            BeginTime = begin,
            EasingFunction = FadeEase,
        });

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            HideRailItem(item);
            var delay = begin + At(i, 52, 560);
            var shift = (TranslateTransform)item.RenderTransform;
            item.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Ms(240))
            {
                BeginTime = delay,
                EasingFunction = FadeEase,
            });
            shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(16, 0, Ms(320))
            {
                BeginTime = delay,
                EasingFunction = Settle,
            });
        }
    }

    /// <summary>Left-rail parts, top→bottom. List rows, nav buttons, then tagged / sections.</summary>
    static List<FrameworkElement> RailItems(FrameworkElement rail)
    {
        var items = new List<FrameworkElement>();
        CollectRailParts(rail, 0, items);
        if (items.Count == 0)
            items = StaggerTargets(rail);
        OrderByPosition(rail, items);
        if (items.Count > 16) items.RemoveRange(16, items.Count - 16);
        return items;
    }

    static void CollectRailParts(DependencyObject node, int depth, List<FrameworkElement> into)
    {
        if (depth > 10 || into.Count >= 16) return;

        if (node is ItemsControl ic)
        {
            // Header / chrome sitting above the list (manage "实例" title, etc.).
            if (VisualTreeHelper.GetParent(ic) is Panel host)
            {
                foreach (var sibling in Children(host))
                {
                    if (ReferenceEquals(sibling, ic)) break;
                    if (sibling is FrameworkElement fe && fe.Visibility == Visibility.Visible
                        && fe.ActualHeight > 1 && into.Count < 16)
                        into.Add(fe);
                }
            }
            for (var i = 0; i < ic.Items.Count && into.Count < 16; i++)
            {
                if (ic.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement row
                    && row.Visibility == Visibility.Visible)
                    into.Add(row);
            }
            return;
        }

        if (node is Panel panel && panel is not System.Windows.Controls.Primitives.UniformGrid)
        {
            var kids = new List<FrameworkElement>();
            foreach (var child in Children(panel))
            {
                if (child is FrameworkElement fe && fe.Visibility == Visibility.Visible)
                    kids.Add(fe);
            }
            // Flat nav stack (settings / download): each child is one beat.
            if (kids.Count >= 2 && kids.All(k => k is not ItemsControl && k is not Grid))
            {
                foreach (var k in kids)
                {
                    if (into.Count >= 16) break;
                    if (k.ActualHeight > 1 || k.ActualWidth > 1) into.Add(k);
                }
                return;
            }
            foreach (var k in kids) CollectRailParts(k, depth + 1, into);
            return;
        }

        if (node is Border { Child: DependencyObject inner })
        {
            CollectRailParts(inner, depth + 1, into);
            return;
        }

        foreach (var child in Children(node))
            CollectRailParts(child, depth + 1, into);
    }

    /// <summary>
    /// Left column = rail, everything else in the root two-column grid = main.
    /// Pane-only bundles (settings layer, download shelf) have no split → main path.
    /// </summary>
    static (FrameworkElement? Rail, FrameworkElement? Main) SplitRailMain(UIElement page)
    {
        if (page is not DependencyObject root) return (null, null);
        var grid = FindColumnGrid(root, 0);
        if (grid is null) return (null, null);

        FrameworkElement? rail = null;
        FrameworkElement? main = null;
        foreach (var child in Children(grid))
        {
            if (child is not FrameworkElement fe || fe.Visibility != Visibility.Visible) continue;
            var col = Grid.GetColumn(fe);
            if (col <= 0)
            {
                // Skip zero-width rail toggles (download's thin reopen strip).
                if (fe.Width is > 0 and < 28) continue;
                rail ??= fe;
            }
            else
            {
                main ??= fe;
            }
        }
        if (rail is null || main is null) return (null, null);
        return (rail, main);
    }

    static Grid? FindColumnGrid(DependencyObject node, int depth)
    {
        if (depth > 6) return null;
        if (node is Grid g && g.ColumnDefinitions.Count >= 2 && VisibleColumnChildren(g) >= 2)
            return g;
        foreach (var child in Children(node))
        {
            var hit = FindColumnGrid(child, depth + 1);
            if (hit is not null) return hit;
        }
        return null;
    }

    static int VisibleColumnChildren(Grid g)
    {
        var n = 0;
        foreach (var child in Children(g))
            if (child is FrameworkElement { Visibility: Visibility.Visible }) n++;
        return n;
    }

    /// <summary>Reduced motion: no travel, no scale, just a short comprehension-grade fade.</summary>
    static void CrossFade(UIElement element)
    {
        element.RenderTransform = Transform.Identity;
        if (!Reduced)
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = 1;
            return;
        }
        element.Opacity = 0;
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Ms(120))
        {
            EasingFunction = FadeEase,
        });
    }

    /// <summary>
    /// One beat of a staged window resize, driven per frame with DEVICE-PIXEL snapping.
    ///
    /// Animating Width/Height with a DoubleAnimation feeds fractions of a DIP into the
    /// window every frame. At 150% scaling that lands on half pixels, and with layout
    /// rounding on, every child snaps to a different pixel each frame — that is the
    /// twitching inside the window. Snapping the bounds to whole physical pixels per frame
    /// makes the content move by a stable integer step.
    ///
    /// Null bounds are left where they are, so the caller can widen first and drop the
    /// bottom edge second — two readable edges instead of one diagonal jump.
    /// </summary>
    public static void StepBounds(Window win, double? left, double? top, double? width, double? height,
        int ms, Action? done = null)
    {
        StopBounds();
        if (!Animated)
        {
            if (left is { } l0) win.Left = l0;
            if (top is { } t0) win.Top = t0;
            if (width is { } w0) win.Width = w0;
            if (height is { } h0) win.Height = h0;
            done?.Invoke();
            return;
        }
        _bounds = new BoundsDriver(win, left, top, width, height, ms, done);
        _bounds.Start();
    }

    /// <summary>Cancel any in-flight bounds travel (a settings apply, a rebuild).</summary>
    public static void StopBounds()
    {
        _bounds?.Stop();
        _bounds = null;
    }

    static BoundsDriver? _bounds;

    /// <summary>Frame-synced, pixel-snapped window bounds driver (one at a time).</summary>
    sealed class BoundsDriver
    {
        readonly Window _win;
        readonly double? _left, _top, _width, _height;
        readonly double _l0, _t0, _w0, _h0;
        readonly int _ms;
        readonly Action? _done;
        readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        bool _running;

        public BoundsDriver(Window win, double? left, double? top, double? width, double? height,
            int ms, Action? done)
        {
            _win = win;
            _left = left;
            _top = top;
            _width = width;
            _height = height;
            _ms = Math.Max(1, ms);
            _done = done;
            _l0 = win.Left;
            _t0 = win.Top;
            _w0 = win.Width;
            _h0 = win.Height;
        }

        public void Start()
        {
            _running = true;
            CompositionTarget.Rendering += OnFrame;
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;
            CompositionTarget.Rendering -= OnFrame;
        }

        void OnFrame(object? sender, EventArgs e)
        {
            if (!_running) return;
            var t = Math.Min(1.0, _clock.Elapsed.TotalMilliseconds / _ms);
            // Same exponential settle as Settle/PowerEase(4.6).
            var k = 1 - Math.Pow(1 - t, 4.6);
            var dpi = VisualTreeHelper.GetDpi(_win);
            var x = Snap(Lerp(_l0, _left ?? _l0, k), dpi.DpiScaleX);
            var y = Snap(Lerp(_t0, _top ?? _t0, k), dpi.DpiScaleY);
            var w = Snap(Lerp(_w0, _width ?? _w0, k), dpi.DpiScaleX);
            var h = Snap(Lerp(_h0, _height ?? _h0, k), dpi.DpiScaleY);
            if (w > 1) _win.Width = w;
            if (h > 1) _win.Height = h;
            _win.Left = x;
            _win.Top = y;

            if (t < 1) return;
            Stop();
            if (_width is { } fw) _win.Width = Snap(fw, dpi.DpiScaleX);
            if (_height is { } fh) _win.Height = Snap(fh, dpi.DpiScaleY);
            if (_left is { } fl) _win.Left = Snap(fl, dpi.DpiScaleX);
            if (_top is { } ft) _win.Top = Snap(ft, dpi.DpiScaleY);
            _done?.Invoke();
        }

        static double Lerp(double from, double to, double k) => from + (to - from) * k;

        /// <summary>Whole physical pixel, expressed back in DIPs.</summary>
        static double Snap(double dip, double scale) =>
            scale <= 0 ? dip : Math.Round(dip * scale) / scale;
    }

    /// <summary>
    /// The 1px edge ring cannot stay crisp while the window is between two sizes: the line
    /// lands on fractional pixels and blinks. Fade it out for the bounds travel and back in
    /// when they land, so the edge reads as part of the motion instead of a flicker.
    /// </summary>
    public static void Edge(FrameworkElement ring, bool visible, int ms = 130)
    {
        ring.BeginAnimation(UIElement.OpacityProperty, null);
        if (!Animated)
        {
            ring.Opacity = visible ? 1 : 0;
            return;
        }
        ring.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(visible ? 1 : 0, Ms(ms))
        {
            EasingFunction = visible ? FadeEase : Exit,
        });
    }

    /// <summary>Crossfade between the two title-bar modes.</summary>
    public static void Reveal(UIElement element)
    {
        if (!Animated) { CrossFade(element); return; }
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Ms(150))
        {
            EasingFunction = Settle,
        });
    }

    // CSS-like cubic-bezier for chrome: exit accelerates away, enter decelerates in.
    static readonly KeySpline ChromeExitSpline = FreezeSpline(0.70, 0.00, 0.84, 0.00);
    static readonly KeySpline ChromeEnterSpline = FreezeSpline(0.16, 1.00, 0.30, 1.00);

    static KeySpline FreezeSpline(double x1, double y1, double x2, double y2)
    {
        var s = new KeySpline(x1, y1, x2, y2);
        s.Freeze();
        return s;
    }

    /// <summary>
    /// Whole chrome strip swap (brand+tabs ↔ back+title). Forward: leaving goes left,
    /// entering comes from the right. Back: the opposite. Enter starts only after leave
    /// has fully finished — with fade and cubic-bezier acceleration.
    /// </summary>
    public static void ChromeSwap(FrameworkElement leaving, FrameworkElement entering,
        bool forward, int ms = 340, double? travel = null)
    {
        var leaveShift = EnsureTranslate(leaving);
        var enterShift = EnsureTranslate(entering);

        leaving.UpdateLayout();
        double dist;
        if (travel is double given)
            dist = Math.Max(180, given);
        else
        {
            var span = leaving.ActualWidth > 1 ? leaving.ActualWidth
                : entering.ActualWidth > 1 ? entering.ActualWidth
                : 320;
            dist = Math.Max(180, span * 0.6);
        }
        var leaveTo = forward ? -dist : dist;
        var enterFrom = forward ? dist : -dist;

        leaveShift.BeginAnimation(TranslateTransform.XProperty, null);
        enterShift.BeginAnimation(TranslateTransform.XProperty, null);
        leaving.BeginAnimation(UIElement.OpacityProperty, null);
        entering.BeginAnimation(UIElement.OpacityProperty, null);

        void Finish()
        {
            leaving.Visibility = Visibility.Collapsed;
            leaving.IsHitTestVisible = false;
            entering.Visibility = Visibility.Visible;
            entering.IsHitTestVisible = true;
            leaveShift.BeginAnimation(TranslateTransform.XProperty, null);
            enterShift.BeginAnimation(TranslateTransform.XProperty, null);
            leaving.BeginAnimation(UIElement.OpacityProperty, null);
            entering.BeginAnimation(UIElement.OpacityProperty, null);
            leaveShift.X = 0;
            enterShift.X = 0;
            leaving.Opacity = 1;
            entering.Opacity = 1;
        }

        if (!Animated)
        {
            Finish();
            return;
        }

        var leaveMs = ms;
        var enterMs = ms;
        var gapMs = 40;
        var handedOff = false;
        var finished = false;

        leaving.IsHitTestVisible = false;
        // Clickable as soon as this strip is the destination — never trap Back behind false.
        entering.IsHitTestVisible = true;
        leaveShift.X = 0;
        leaving.Opacity = 1;
        enterShift.X = enterFrom;
        entering.Opacity = 0;
        entering.Visibility = Visibility.Visible;

        var leaveFade = BezierTo(leaving, UIElement.OpacityProperty, 1, 0, leaveMs, ChromeExitSpline);
        BezierTo(leaveShift, TranslateTransform.XProperty, 0, leaveTo, leaveMs, ChromeExitSpline);

        void Settle()
        {
            if (finished) return;
            finished = true;
            Finish();
        }

        void StartEnter()
        {
            if (handedOff) return;
            handedOff = true;

            leaving.Visibility = Visibility.Collapsed;
            leaveShift.BeginAnimation(TranslateTransform.XProperty, null);
            leaving.BeginAnimation(UIElement.OpacityProperty, null);
            leaveShift.X = 0;
            leaving.Opacity = 1;

            enterShift.X = enterFrom;
            entering.Opacity = 0;
            entering.Visibility = Visibility.Visible;
            entering.IsHitTestVisible = true;

            BezierTo(enterShift, TranslateTransform.XProperty, enterFrom, 0, enterMs, ChromeEnterSpline);
            var enterFade = BezierTo(entering, UIElement.OpacityProperty, 0, 1, enterMs, ChromeEnterSpline);
            EventHandler? done = null;
            done = (_, _) =>
            {
                enterFade.Completed -= done;
                Settle();
            };
            enterFade.Completed += done;

            // Failsafe: KeyFrames Completed is not always raised after BeginAnimation.
            var settle = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(MsValue(enterMs + 30)),
            };
            settle.Tick += (_, _) =>
            {
                settle.Stop();
                enterFade.Completed -= done;
                Settle();
            };
            settle.Start();
        }

        EventHandler? leaveDone = null;
        leaveDone = (_, _) =>
        {
            leaveFade.Completed -= leaveDone;
            StartEnter();
        };
        leaveFade.Completed += leaveDone;

        // Failsafe if leave Completed is skipped.
        var handoff = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(MsValue(leaveMs + gapMs)),
        };
        handoff.Tick += (_, _) =>
        {
            handoff.Stop();
            leaveFade.Completed -= leaveDone;
            StartEnter();
        };
        handoff.Start();
    }

    static TranslateTransform EnsureTranslate(FrameworkElement fe)
    {
        if (fe.RenderTransform is TranslateTransform live && !live.IsFrozen) return live;
        var t = new TranslateTransform();
        fe.RenderTransform = t;
        return t;
    }

    /// <summary>Cubic-bezier via <see cref="SplineDoubleKeyFrame"/> (CSS cubic-bezier equivalent).</summary>
    static DoubleAnimationUsingKeyFrames BezierTo(IAnimatable target, DependencyProperty prop,
        double from, double to, int ms, KeySpline spline)
    {
        var anim = new DoubleAnimationUsingKeyFrames();
        anim.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromPercent(0)));
        anim.KeyFrames.Add(new SplineDoubleKeyFrame(to,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(MsValue(ms))), spline));
        target.BeginAnimation(prop, anim);
        return anim;
    }

    /// <summary>
    /// Chrome title swap. Forward: old slides left out, new from the right.
    /// Back (<paramref name="forward"/> false): the opposite. Pure slide (no fade) so it reads.
    /// </summary>
    public static void TitleSlide(TextBlock title, string next, bool forward = true, int ms = 280)
    {
        next ??= "";
        if (string.Equals(title.Text, next, StringComparison.Ordinal)) return;

        if (title.RenderTransform is not TranslateTransform shift || shift.IsFrozen)
        {
            shift = new TranslateTransform();
            title.RenderTransform = shift;
        }

        // Prefer the clipping slot (Grid) as host so the travel is visible against chrome.
        var host = title.Parent as Panel;
        if (!Animated || host is null)
        {
            shift.BeginAnimation(TranslateTransform.XProperty, null);
            shift.X = 0;
            title.Opacity = 1;
            title.Text = next;
            return;
        }

        host.UpdateLayout();
        title.UpdateLayout();
        var span = host.ActualWidth > 1 ? host.ActualWidth
            : title.ActualWidth > 1 ? title.ActualWidth
            : 96;
        var dist = Math.Max(72, span * 0.85);
        var outTo = forward ? -dist : dist;
        var inFrom = forward ? dist : -dist;

        // Drop any unfinished ghost from a previous swap.
        for (var i = host.Children.Count - 1; i >= 0; i--)
        {
            if (host.Children[i] is FrameworkElement { Tag: "title-ghost" } stale)
                host.Children.Remove(stale);
        }

        var ghost = new TextBlock
        {
            Text = title.Text,
            FontFamily = title.FontFamily,
            FontSize = title.FontSize,
            FontWeight = title.FontWeight,
            FontStyle = title.FontStyle,
            Foreground = title.Foreground,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            IsHitTestVisible = false,
            Tag = "title-ghost",
        };
        var ghostShift = new TranslateTransform();
        ghost.RenderTransform = ghostShift;
        var at = host.Children.IndexOf(title);
        if (at < 0) at = 0;
        host.Children.Insert(at, ghost);

        shift.BeginAnimation(TranslateTransform.XProperty, null);
        title.BeginAnimation(UIElement.OpacityProperty, null);
        title.Opacity = 1;
        shift.X = inFrom;
        title.Text = next;
        title.UpdateLayout();

        ghostShift.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(0, outTo, Ms(ms)) { EasingFunction = Exit });

        var slideIn = new DoubleAnimation(inFrom, 0, Ms(ms)) { EasingFunction = Settle };
        EventHandler? done = null;
        done = (_, _) =>
        {
            slideIn.Completed -= done;
            host.Children.Remove(ghost);
            shift.BeginAnimation(TranslateTransform.XProperty, null);
            shift.X = 0;
        };
        slideIn.Completed += done;
        shift.BeginAnimation(TranslateTransform.XProperty, slideIn);
    }

    /// <summary>
    /// 内容在原位重组：不挪位置、不闪白，新排布从半透明轻轻落定。
    /// 整块换内容的容器用这个；要「同一批元素换位」用 Relayout 思路（见 ProjectRail）。
    /// </summary>
    public static void Morph(UIElement element)
    {
        if (!Animated) { CrossFade(element); return; }
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.4, 1, Ms(200))
        {
            EasingFunction = Settle,
        });
    }

    /// <summary>位移动画：FLIP 重组用。begin 用来做错峰。</summary>
    public static DoubleAnimation Move(double from, double to, int ms = 300, TimeSpan? begin = null)
    {
        var a = new DoubleAnimation(from, to, Ms(ms)) { EasingFunction = Settle };
        if (begin is not null) a.BeginTime = begin;
        return a;
    }

    /// <summary>透明度动画：错峰淡入用。</summary>
    public static DoubleAnimation Fade(double from, double to, int ms = 240, TimeSpan? begin = null)
    {
        var a = new DoubleAnimation(from, to, Ms(ms)) { EasingFunction = Settle };
        if (begin is not null) a.BeginTime = begin;
        return a;
    }

    /// <summary>
    /// 呼吸：透明度低点 ↔ 高点，无限循环。给「这一行在跑」的行底色用——
    /// 行尾的灯在闪，行本身也一起呼吸，一屏扫过去才知道是哪一行。
    /// 关掉动画时给静止的中点值（gentler, not nothing）。
    /// </summary>
    public static void Breathe(FrameworkElement element, double low = 0.16, double high = 0.42, int ms = 2400)
    {
        if (!Animated)
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = (low + high) / 2;
            return;
        }
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(low, high, Ms(ms))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        });
    }

    /// <summary>停掉呼吸并归零：这一行不在跑了，底色不能留。</summary>
    public static void Calm(FrameworkElement element)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = 0;
    }

    /// <summary>
    /// 舞台元素的宽度：收起/展开走动画，旁边的内容跟着连续重排——
    /// 「元素在舞台上重新组成」靠的就是这种连续性，而不是跳变。
    /// 只在人手动收放侧栏时用；换页时宽度必须瞬间到位（见 MainWindow.ShowRail）。
    /// </summary>
    public static void WidthTo(FrameworkElement element, double to, int ms = 260)
    {
        if (!Animated)
        {
            element.BeginAnimation(FrameworkElement.WidthProperty, null);
            element.Width = to;
            return;
        }
        element.BeginAnimation(FrameworkElement.WidthProperty, new DoubleAnimation(to, Ms(ms))
        {
            EasingFunction = Settle,
        });
    }

    /// <summary>
    /// Mask morph: animate a rounded clip rect. Content behind stays at final layout; only
    /// the reveal aperture moves (grow = open, shrink = close).
    /// </summary>
    public static void ClipRectTo(RectangleGeometry geo, Rect from, Rect to, int ms,
        Action? done = null)
    {
        geo.BeginAnimation(RectangleGeometry.RectProperty, null);
        geo.Rect = from;
        if (!Animated)
        {
            geo.Rect = to;
            done?.Invoke();
            return;
        }

        var a = new RectAnimation(from, to, Ms(ms)) { EasingFunction = Settle };
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            a.Completed -= handler!;
            geo.BeginAnimation(RectangleGeometry.RectProperty, null);
            geo.Rect = to;
            done?.Invoke();
        };
        a.Completed += handler;
        geo.BeginAnimation(RectangleGeometry.RectProperty, a);
    }

    /// <summary>
    /// Page-size shell morph: animate OuterShell width and/or height while the HWND stays
    /// put. Null means "leave this axis alone" so the caller can widen first, then drop the
    /// bottom edge — two readable edges from a fixed top-left. Layout reflow here is
    /// intentional (the surplus around the shell is transparent).
    /// </summary>
    public static void SizeTo(FrameworkElement element, double? width, double? height, int ms,
        Action? done = null)
    {
        if (!Animated)
        {
            if (width is { } w0)
            {
                element.BeginAnimation(FrameworkElement.WidthProperty, null);
                element.Width = w0;
            }
            if (height is { } h0)
            {
                element.BeginAnimation(FrameworkElement.HeightProperty, null);
                element.Height = h0;
            }
            done?.Invoke();
            return;
        }

        var pending = 0;
        if (width is not null) pending++;
        if (height is not null) pending++;
        if (pending == 0)
        {
            done?.Invoke();
            return;
        }

        if (width is { } tw)
        {
            var a = new DoubleAnimation(tw, Ms(ms)) { EasingFunction = Settle };
            EventHandler? onW = null;
            onW = (_, _) =>
            {
                a.Completed -= onW!;
                if (--pending == 0) done?.Invoke();
            };
            a.Completed += onW;
            element.BeginAnimation(FrameworkElement.WidthProperty, a);
        }
        if (height is { } th)
        {
            var a = new DoubleAnimation(th, Ms(ms)) { EasingFunction = Settle };
            EventHandler? onH = null;
            onH = (_, _) =>
            {
                a.Completed -= onH!;
                if (--pending == 0) done?.Invoke();
            };
            a.Completed += onH;
            element.BeginAnimation(FrameworkElement.HeightProperty, a);
        }
    }

    /// <summary>顶栏白框：平滑滑过去，不回弹。位移走渲染线程。</summary>
    public static void SpringSlide(TranslateTransform transform, double to, int ms = 380)
    {
        if (!Animated)
        {
            transform.BeginAnimation(TranslateTransform.XProperty, null);
            transform.X = to;
            return;
        }
        transform.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(to, Ms(ms)) { EasingFunction = Settle });
    }

    /// <summary>顶栏白框的横向缩放，和位移同一条渲染线程，不碰布局。</summary>
    public static void ScaleXTo(ScaleTransform scale, double to, int ms = 380)
    {
        if (!Animated)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.ScaleX = to;
            return;
        }
        scale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(to, Ms(ms)) { EasingFunction = Settle });
    }

    /// <summary>
    /// A To-only animation: WPF starts it from the live presentation value, so a second
    /// click retargets the pill mid-flight instead of jumping to a fixed origin.
    /// </summary>
    public static void Slide(TranslateTransform transform, double to, int ms = 240)
    {
        if (!Animated)
        {
            transform.BeginAnimation(TranslateTransform.XProperty, null);
            transform.X = to;
            return;
        }
        transform.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(to, Ms(ms)) { EasingFunction = Settle });
    }

    /// <summary>
    /// Shared selection thumb for a radio strip. Put on a Grid whose first <see cref="Border"/>
    /// is the thumb (with a <see cref="TranslateTransform"/>) and whose other child is a
    /// Panel of RadioButtons. Kind: <c>nav</c> (vertical, snaps), <c>pill</c>/<c>tab</c>
    /// (horizontal; the thumb slides and scales like the top-bar capsule).
    /// </summary>
    public static readonly DependencyProperty SlideSelectProperty = DependencyProperty.RegisterAttached(
        "SlideSelect", typeof(string), typeof(Fx),
        new PropertyMetadata(null, OnSlideSelectChanged));

    public static void SetSlideSelect(DependencyObject d, string? value) =>
        d.SetValue(SlideSelectProperty, value);

    public static string? GetSlideSelect(DependencyObject d) =>
        (string?)d.GetValue(SlideSelectProperty);

    /// <summary>Re-place the thumb after the strip's children change (e.g. rebuilt chips).</summary>
    public static void RefreshSlideSelect(FrameworkElement host) => ScheduleSlideSnap(host);

    static readonly DependencyProperty SlideSelectWiredProperty = DependencyProperty.RegisterAttached(
        "SlideSelectWired", typeof(bool), typeof(Fx), new PropertyMetadata(false));

    static readonly DependencyProperty SlideSelectPendingProperty = DependencyProperty.RegisterAttached(
        "SlideSelectPending", typeof(bool), typeof(Fx), new PropertyMetadata(false));

    static void OnSlideSelectChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement host) return;
        host.Loaded -= SlideSelect_Loaded;
        host.SizeChanged -= SlideSelect_SizeChanged;
        host.IsVisibleChanged -= SlideSelect_IsVisibleChanged;
        if (e.NewValue is not string { Length: > 0 }) return;
        host.Loaded += SlideSelect_Loaded;
        host.SizeChanged += SlideSelect_SizeChanged;
        host.IsVisibleChanged += SlideSelect_IsVisibleChanged;
        if (host.IsLoaded) WireSlideSelect(host);
    }

    static void SlideSelect_Loaded(object sender, RoutedEventArgs e) =>
        WireSlideSelect((FrameworkElement)sender);

    static readonly DependencyProperty SlideFollowProperty = DependencyProperty.RegisterAttached(
        "SlideFollow", typeof(bool), typeof(Fx), new PropertyMetadata(false));

    static void SlideSelect_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not FrameworkElement host) return;
        // A check changes the selected label's weight, so the strip resizes. Follow that
        // with the same slide — a snap here is the "stall, then jump".
        host.SetValue(SlideFollowProperty, true);
        ScheduleSlideSnap(host);
    }

    static void SlideSelect_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is FrameworkElement { IsVisible: true } host)
            ScheduleSlideSnap(host);
    }

    static void WireSlideSelect(FrameworkElement host)
    {
        if ((bool)host.GetValue(SlideSelectWiredProperty))
        {
            ScheduleSlideSnap(host);
            return;
        }
        host.SetValue(SlideSelectWiredProperty, true);
        host.AddHandler(RadioButton.CheckedEvent,
            new RoutedEventHandler((_, ev) =>
            {
                if (ev.Source is not RadioButton { IsChecked: true } rb) return;
                if (!IsDescendant(host, rb)) return;
                PlaceSlideSelect(host, rb, animate: true);
            }),
            handledEventsToo: true);
        HookSlideWindow(Window.GetWindow(host));
        ScheduleSlideSnap(host);
    }

    static readonly ConditionalWeakTable<Window, object> SlideWindowHooked = new();
    static readonly ConditionalWeakTable<Window, object> SlideMovePending = new();

    static void HookSlideWindow(Window? win)
    {
        if (win is null) return;
        if (!SlideWindowHooked.TryAdd(win, Dummy)) return;
        win.DpiChanged += (_, _) => ResnapAllSlides(win);
        // Dragging across monitors briefly corrupts visual transforms; resnap once it settles.
        win.LocationChanged += (_, _) =>
        {
            if (!SlideMovePending.TryAdd(win, Dummy)) return;
            win.Dispatcher.BeginInvoke(() =>
            {
                SlideMovePending.Remove(win);
                ResnapAllSlides(win);
            }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        };
    }

    static readonly object Dummy = new();

    static void ResnapAllSlides(Window win)
    {
        void Walk(DependencyObject n)
        {
            if (n is FrameworkElement fe && GetSlideSelect(fe) is { Length: > 0 })
                ScheduleSlideSnap(fe);
            for (int i = 0, c = VisualTreeHelper.GetChildrenCount(n); i < c; i++)
                Walk(VisualTreeHelper.GetChild(n, i));
        }
        Walk(win);
    }

    /// <summary>
    /// Coalesce snaps to one ContextIdle pass so mid-layout / mid-DPI values are skipped.
    /// </summary>
    static void ScheduleSlideSnap(FrameworkElement host)
    {
        if ((bool)host.GetValue(SlideSelectPendingProperty)) return;
        host.SetValue(SlideSelectPendingProperty, true);
        host.Dispatcher.BeginInvoke(() =>
        {
            host.SetValue(SlideSelectPendingProperty, false);
            var follow = (bool)host.GetValue(SlideFollowProperty);
            host.SetValue(SlideFollowProperty, false);
            SnapSlideSelect(host, follow);
        }, System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    static void SnapSlideSelect(FrameworkElement host, bool follow)
    {
        if (!host.IsVisible) return;
        var rb = FindCheckedRadio(host);
        if (rb is null) return;
        var animate = follow
            && GetSlideSelect(host) is not "nav"
            && ResolveSlideThumb(host) is { Thumb: { } thumb }
            && (bool)thumb.GetValue(PillPlacedProperty);
        PlaceSlideSelect(host, rb, animate);
    }

    static RadioButton? FindCheckedRadio(FrameworkElement host)
    {
        RadioButton? hit = null;
        void Walk(DependencyObject n)
        {
            if (hit is not null) return;
            if (n is RadioButton { IsChecked: true, IsVisible: true } rb && rb.ActualHeight > 0)
            {
                hit = rb;
                return;
            }
            for (int i = 0, nCount = VisualTreeHelper.GetChildrenCount(n); i < nCount; i++)
                Walk(VisualTreeHelper.GetChild(n, i));
        }
        Walk(host);
        return hit;
    }

    static bool IsDescendant(DependencyObject root, DependencyObject? node)
    {
        for (var d = node; d is not null; d = ParentOf(d))
            if (ReferenceEquals(d, root)) return true;
        return false;
    }

    static readonly DependencyProperty PillPlacedProperty = DependencyProperty.RegisterAttached(
        "PillPlaced", typeof(bool), typeof(Fx), new PropertyMetadata(false));

    static void PlaceSlideSelect(FrameworkElement host, RadioButton tab, bool animate)
    {
        if (ResolveSlideThumb(host) is not { } pair) return;
        var (thumb, shift, scale, kind) = pair;
        if (tab.Visibility != Visibility.Visible || !host.IsVisible) return;
        if (tab.ActualWidth < 1 || tab.ActualHeight < 1) return;
        if (!TryLayoutSlot(host, tab, out var at, out var size)) return;
        if (size.Width < 1 || size.Height < 1) return;
        if (double.IsNaN(at.X) || double.IsNaN(at.Y)) return;

        if (kind is "nav" || scale is null)
        {
            shift.BeginAnimation(TranslateTransform.XProperty, null);
            shift.BeginAnimation(TranslateTransform.YProperty, null);
            thumb.BeginAnimation(FrameworkElement.WidthProperty, null);
            thumb.BeginAnimation(FrameworkElement.HeightProperty, null);
            if (kind is "nav")
            {
                thumb.Height = size.Height;
                shift.X = 0;
                shift.Y = at.Y;
            }
            else
            {
                thumb.Width = size.Width;
                thumb.Height = size.Height;
                shift.X = at.X;
                shift.Y = at.Y;
            }
            thumb.Opacity = 1;
            return;
        }

        thumb.Height = size.Height;
        var placed = (bool)thumb.GetValue(PillPlacedProperty);
        if (!placed || !animate || !Animated)
        {
            thumb.BeginAnimation(FrameworkElement.WidthProperty, null);
            thumb.Width = size.Width;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.ScaleX = 1;
            shift.BeginAnimation(TranslateTransform.XProperty, null);
            shift.X = at.X;
            thumb.SetValue(PillPlacedProperty, true);
            thumb.Opacity = 1;
            return;
        }

        var baseW = double.IsNaN(thumb.Width) || thumb.Width < 1 ? size.Width : thumb.Width;
        var visualW = baseW * (scale.ScaleX <= 0 ? 1 : scale.ScaleX);
        thumb.BeginAnimation(FrameworkElement.WidthProperty, null);
        thumb.Width = size.Width;
        var from = size.Width <= 1 ? 1 : visualW / size.Width;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.ScaleX = from;
        ScaleXTo(scale, 1);
        SpringSlide(shift, at.X);
        thumb.Opacity = 1;
    }

    /// <summary>
    /// Layout-slot placement relative to <paramref name="host"/> — stable across window drag /
    /// DPI changes where <see cref="UIElement.TranslatePoint"/> can briefly report mixed spaces.
    /// </summary>
    public static bool TryLayoutSlot(FrameworkElement host, FrameworkElement tab, out Point at, out Size size)
    {
        at = default;
        size = default;
        var slot = LayoutInformation.GetLayoutSlot(tab);
        if (slot.Width < 1 || slot.Height < 1) return false;

        double x = slot.X, y = slot.Y;
        for (var p = VisualTreeHelper.GetParent(tab) as FrameworkElement;
             p is not null && !ReferenceEquals(p, host);
             p = VisualTreeHelper.GetParent(p) as FrameworkElement)
        {
            var ps = LayoutInformation.GetLayoutSlot(p);
            x += ps.X;
            y += ps.Y;
        }

        at = new Point(x, y);
        size = new Size(slot.Width, slot.Height);
        return true;
    }

    static (Border Thumb, TranslateTransform Shift, ScaleTransform? Scale, string Kind)? ResolveSlideThumb(FrameworkElement host)
    {
        var kind = GetSlideSelect(host) ?? "pill";
        Border? thumb = null;
        if (host is Panel panel)
        {
            foreach (var child in panel.Children)
            {
                if (child is Border b)
                {
                    thumb = b;
                    break;
                }
            }
        }
        if (thumb is null) return null;

        thumb.RenderTransformOrigin = new Point(0, 0.5);
        if (thumb.RenderTransform is TransformGroup group && !group.IsFrozen)
        {
            ScaleTransform? scale = null;
            TranslateTransform? shift = null;
            foreach (var child in group.Children)
            {
                if (child is ScaleTransform s) scale = s;
                if (child is TranslateTransform t) shift = t;
            }
            if (shift is not null) return (thumb, shift, scale, kind);
        }

        TranslateTransform live;
        if (thumb.RenderTransform is TranslateTransform plain && !plain.IsFrozen)
            live = plain;
        else
            live = new TranslateTransform();

        if (kind is "nav")
        {
            thumb.RenderTransform = live;
            return (thumb, live, null, kind);
        }

        var sx = new ScaleTransform(1, 1);
        var wrap = new TransformGroup();
        wrap.Children.Add(sx);
        wrap.Children.Add(live);
        thumb.RenderTransform = wrap;
        return (thumb, live, sx, kind);
    }

    /// <summary>Toast leave: same edge it entered, then the caller removes the card.</summary>
    public static DoubleAnimation ToastOut(TranslateTransform shift)
    {
        shift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(8, Ms(160)) { EasingFunction = Exit });
        return new DoubleAnimation(0, Ms(160)) { EasingFunction = Exit };
    }

    static Duration Ms(int value) => new(TimeSpan.FromMilliseconds(MsValue(value)));

    static int MsValue(int value) => Math.Max(1, (int)Math.Round(value * _speed));

    /// <summary>User <c>AnimSpeed</c>-scaled span for callers outside this class (boot handoff, etc.).</summary>
    public static TimeSpan Span(int ms) => TimeSpan.FromMilliseconds(MsValue(ms));

    /// <summary>Animate a determinate meter. PAD has real step ratios during launch.</summary>
    public static void MeterTo(ProgressBar bar, double to, int ms = 320)
    {
        to = Math.Clamp(to, bar.Minimum, bar.Maximum);
        if (!Animated)
        {
            bar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, null);
            bar.Value = to;
            return;
        }
        bar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty,
            new DoubleAnimation(to, Ms(ms)) { EasingFunction = Settle });
    }

    /// <summary>The launch card arriving: fade + a short lift. pop=true for overlay cards.</summary>
    public static void PulseIn(UIElement element, bool pop = false)
    {
        if (element is FrameworkElement fe)
            fe.RenderTransformOrigin = new Point(0.5, 0.5);
        if (!Animated)
        {
            CrossFade(element);
            return;
        }
        element.Opacity = 0;
        var rise = pop ? 22.0 : 12.0;
        if (pop)
        {
            // Overlay cards are centred, not anchored to a trigger — Apple exempts modals
            // from origin-awareness. 0.94 is the floor: nothing appears from nothing.
            var scale = new ScaleTransform(0.94, 0.94);
            var shift = new TranslateTransform { Y = rise };
            var group = new TransformGroup();
            group.Children.Add(scale);
            group.Children.Add(shift);
            element.RenderTransform = group;
            element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Ms(200))
            {
                EasingFunction = FadeEase,
            });
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.94, 1, Ms(320))
            {
                EasingFunction = Settle,
            });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.94, 1, Ms(320))
            {
                EasingFunction = Settle,
            });
            shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(rise, 0, Ms(360))
            {
                EasingFunction = Settle,
            });
            return;
        }
        var only = new TranslateTransform { Y = rise };
        element.RenderTransform = only;
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Ms(180))
        {
            EasingFunction = FadeEase,
        });
        only.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(rise, 0, Ms(340))
        {
            EasingFunction = Settle,
        });
    }
}

/// <summary>How a page's parts arrive. One per page, so a switch never feels templated.</summary>
public enum PageMotion
{
    /// <summary>Long rise from below — the launch page's single hero gesture.</summary>
    Rise,

    /// <summary>Scale + lift — card grids.</summary>
    Cards,

    /// <summary>Tight rise — dense form rows.</summary>
    Rows,

    /// <summary>Slide in from the left — list and tree pages.</summary>
    Slide,

    /// <summary>Gentle drift — pages built around a large canvas.</summary>
    Drift,

    /// <summary>One after another with a deliberate gap: the page assembles itself.</summary>
    Sequential,

    /// <summary>A wave across the layout, ordered by screen position rather than tree order.</summary>
    Wave,

    /// <summary>Two beats at once: the lead block travels slowly while the rest cascade under it.</summary>
    Combo,

    /// <summary>Unified tab/pane enter: parts rise top→bottom with a clear stagger.</summary>
    Cascade,
}
