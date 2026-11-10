using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;

using System.Windows.Media;
using System.Windows.Threading;
using Pad.Core;

namespace Pad.Views;

public partial class DownloadView : UserControl, IRefreshable
{
    readonly MainWindow _win;
    readonly AppState _state = AppState.Current;
    bool _pickingTarget;
    /// <summary>商店「装到」里用户明确选的 profile；空 = 跟当前/上次用的那个。</summary>
    string? _pickProfile;
    MarketRow? _detail;
    MarketPage? _lastPage;

    /// <summary>每页多少个按窗口高度自适应（近似 17px/个），不再固定 20。</summary>
    int StorePageSize => Math.Clamp((int)(_win.ActualHeight / 17), 10, 100);

    int _storePageSizeUsed = 20;
    int _storeColumns = 3;
    bool _colSync;
    bool _shelfPillReady, _periodPillReady, _colPillReady;
    int _shelfPillGen, _periodPillGen, _colPillGen;

    bool _layoutSync;

    void StoreLayoutBtn_Checked(object sender, RoutedEventArgs e)
    {
        if (_layoutSync) return;
        _colSync = true;
        foreach (var rb in ColRow.Children.OfType<RadioButton>())
            rb.IsChecked = (rb.Tag as string) == _storeColumns.ToString();
        _colSync = false;
        StoreLayoutPop.IsOpen = true;
    }

    void StoreLayoutBtn_Unchecked(object sender, RoutedEventArgs e)
    {
        if (_layoutSync) return;
        StoreLayoutPop.IsOpen = false;
    }

    void StoreLayoutPop_Opened(object? sender, EventArgs e)
    {
        // Keep IsChecked so IcoToggle shows the selected wash while the menu is open.
        _layoutSync = true;
        StoreLayoutBtn.IsChecked = true;
        _layoutSync = false;
        // Outside-click closes the popup; disable the gear for that click or it would reopen.
        StoreLayoutBtn.IsHitTestVisible = false;
        QueueColPill(false);
    }

    void StoreLayoutPop_Closed(object? sender, EventArgs e)
    {
        _layoutSync = true;
        StoreLayoutBtn.IsChecked = false;
        _layoutSync = false;
        Dispatcher.BeginInvoke(() => StoreLayoutBtn.IsHitTestVisible = true,
            DispatcherPriority.Input);
    }

    int _colAnimGen;

    void StoreColumns_Changed(object sender, RoutedEventArgs e)
    {
        if (_colSync) return;
        if (sender is not RadioButton box || box.IsChecked != true) return;
        if (box.Tag is not string tag || !int.TryParse(tag, out var n) || n < 2) return;
        _colSync = true;
        foreach (var rb in ColRow.Children.OfType<RadioButton>())
            if (!ReferenceEquals(rb, box)) rb.IsChecked = false;
        _colSync = false;
        // 正向 FLIP：布局先不动，视觉上实时缩到新列宽，播完再提交 Columns——避免「先压扁内容再播动画」。
        var before = CaptureStoreSlots();
        _storeColumns = n;
        AnimateStoreColumnsForward(before, n);
        QueueColPill(_colPillReady);
    }

    /// <summary>换列数前的卡片位置（相对 ResultList）。容器没实现出来的行直接跳过。</summary>
    Dictionary<FrameworkElement, Rect> CaptureStoreSlots()
    {
        var map = new Dictionary<FrameworkElement, Rect>();
        if (ResultList?.ItemsSource is null) return map;
        // 打断上一次未完成的换列动画，量到真实布局槽位。
        ClearStoreSlotTransforms();
        var count = ResultList.Items.Count;
        for (var i = 0; i < count; i++)
        {
            if (ResultList.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement box) continue;
            if (box.ActualWidth <= 0 || box.ActualHeight <= 0) continue;
            map[box] = SlotOf(box);
        }
        return map;
    }

    void ClearStoreSlotTransforms()
    {
        if (ResultList is null) return;
        var count = ResultList.Items.Count;
        for (var i = 0; i < count; i++)
        {
            if (ResultList.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement box) continue;
            box.BeginAnimation(UIElement.OpacityProperty, null);
            if (box.RenderTransform is TransformGroup)
                box.RenderTransform = null;
        }
    }

    Rect SlotOf(FrameworkElement box)
    {
        var at = box.TransformToAncestor(ResultList).Transform(new Point(0, 0));
        return new Rect(at, new Size(box.ActualWidth, box.ActualHeight));
    }

    /// <summary>
    /// 正向 FLIP：先量出新列真实槽位，再在旧布局上只缩宽度、平移到新槽；高度保持 1，
    /// 避免「等比缩完高度变矮 → Commit 后弹回」的跳动。
    /// </summary>
    void AnimateStoreColumnsForward(Dictionary<FrameworkElement, Rect> before, int cols)
    {
        if (ResultList is null)
        {
            ApplyStoreColumns();
            return;
        }
        if (before.Count == 0 || !Fx.Animated)
        {
            ApplyStoreColumns();
            ResultList.UpdateLayout();
            ClearStoreSlotTransforms();
            return;
        }

        var grid = FindChild<System.Windows.Controls.Primitives.UniformGrid>(ResultList);
        var oldCols = grid?.Columns ?? _storeColumns;
        if (grid is null || ResultList.ActualWidth < 1)
        {
            ApplyStoreColumns();
            return;
        }

        // 先提交新列并量真实目标槽，再改回旧列——动画期间布局仍是旧的，结束时才真正换列。
        grid.Columns = cols;
        grid.InvalidateMeasure();
        ResultList.UpdateLayout();
        var after = new Dictionary<FrameworkElement, Rect>();
        var count = ResultList.Items.Count;
        for (var i = 0; i < count; i++)
        {
            if (ResultList.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement box) continue;
            if (box.ActualWidth <= 0 || box.ActualHeight <= 0) continue;
            after[box] = SlotOf(box);
        }
        grid.Columns = oldCols;
        grid.InvalidateMeasure();
        ResultList.UpdateLayout();

        var gen = ++_colAnimGen;
        var span = Fx.Span(340);
        var ease = EaseInOut;
        var pending = 0;

        foreach (var (box, old) in before)
        {
            if (!after.TryGetValue(box, out var now) || old.Width <= 1 || now.Width <= 1) continue;
            var sx = now.Width / old.Width;
            var dx = now.X - old.X;
            var dy = now.Y - old.Y;
            // 卡片固定高度：ScaleY 必须保持 1，否则播完 Commit 会从「视觉变矮」弹回真高度。
            if (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5 && Math.Abs(sx - 1) < 0.004) continue;

            var scale = new ScaleTransform(1, 1);
            var shift = new TranslateTransform(0, 0);
            var group = new TransformGroup();
            group.Children.Add(scale);
            group.Children.Add(shift);
            box.RenderTransformOrigin = new Point(0, 0);
            box.RenderTransform = group;

            pending++;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new System.Windows.Media.Animation.DoubleAnimation(1, sx, span) { EasingFunction = ease });
            shift.BeginAnimation(TranslateTransform.XProperty,
                new System.Windows.Media.Animation.DoubleAnimation(0, dx, span) { EasingFunction = ease });
            var slide = new System.Windows.Media.Animation.DoubleAnimation(0, dy, span) { EasingFunction = ease };
            slide.Completed += (_, _) =>
            {
                pending--;
                if (pending > 0 || gen != _colAnimGen) return;
                CommitStoreColumns();
            };
            shift.BeginAnimation(TranslateTransform.YProperty, slide);
        }

        if (pending == 0)
            CommitStoreColumns();
    }

    void CommitStoreColumns()
    {
        ApplyStoreColumns();
        if (ResultList is not null) ResultList.UpdateLayout();
        ClearStoreSlotTransforms();
    }

    void QueueShelfPill(bool animate) =>
        QueuePill(++_shelfPillGen, () => _shelfPillGen, ShelfChipHost, ShelfChipRow, ShelfPill, ShelfPillScale, ShelfPillShift, animate, v => _shelfPillReady = v, () => _shelfPillReady);

    void QueuePeriodPill(bool animate) =>
        QueuePill(++_periodPillGen, () => _periodPillGen, PeriodHost, PeriodRow, PeriodPill, PeriodPillScale, PeriodPillShift, animate, v => _periodPillReady = v, () => _periodPillReady);

    void QueueColPill(bool animate) =>
        QueuePill(++_colPillGen, () => _colPillGen, ColHost, ColRow, ColPill, ColPillScale, ColPillShift, animate, v => _colPillReady = v, () => _colPillReady);

    void QueuePill(int gen, Func<int> current, FrameworkElement host, Panel row, Border pill,
        ScaleTransform scale, TranslateTransform shift, bool animate, Action<bool> setReady, Func<bool> ready)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (gen != current() || host is null || host.ActualWidth < 1) return;
            MovePill(host, row, pill, scale, shift, animate && ready());
            setReady(true);
        }, DispatcherPriority.Loaded);
    }

    static void MovePill(FrameworkElement host, Panel row, Border pill, ScaleTransform scale,
        TranslateTransform shift, bool animate)
    {
        RadioButton? tab = null;
        foreach (var child in row.Children.OfType<RadioButton>())
            if (child.IsChecked == true) { tab = child; break; }
        if (tab is null || tab.ActualWidth < 1) return;
        var at = tab.TransformToAncestor(host).Transform(new Point(0, 0));
        var width = tab.ActualWidth;
        if (pill.Tag is Rect last && animate
            && Math.Abs(last.X - at.X) < 0.5 && Math.Abs(last.Width - width) < 0.5)
            return;
        pill.Tag = new Rect(at.X, 0, width, 1);
        pill.Height = tab.ActualHeight;
        pill.Opacity = 1;
        if (!animate || !Fx.Animated)
        {
            pill.BeginAnimation(FrameworkElement.WidthProperty, null);
            pill.Width = width;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.ScaleX = 1;
            shift.BeginAnimation(TranslateTransform.XProperty, null);
            shift.X = at.X;
            return;
        }
        var baseW = pill.Width > 1 ? pill.Width : width;
        var visualW = baseW * (scale.ScaleX > 0 ? scale.ScaleX : 1);
        pill.BeginAnimation(FrameworkElement.WidthProperty, null);
        pill.Width = width;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.ScaleX = width <= 1 ? 1 : visualW / width;
        Fx.ScaleXTo(scale, 1);
        Fx.SpringSlide(shift, at.X);
    }

    void ApplyStoreColumns()
    {
        if (ResultList is null) return;
        var grid = FindChild<System.Windows.Controls.Primitives.UniformGrid>(ResultList);
        if (grid is null) return;
        grid.Columns = _storeColumns;
        grid.InvalidateMeasure();
    }

    static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) return hit;
            var deeper = FindChild<T>(child);
            if (deeper is not null) return deeper;
        }
        return null;
    }

    static System.Windows.Media.Animation.CubicEase EaseInOut { get; } = new()
    {
        EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut,
    };

    int _periodGen;
    bool _periodShown;

    /// <summary>
    /// 日/周/月只在推荐页。高度逐渐撑开/收拢，行本身从上方渐显/向上渐隐；
    /// 返回的任务在这层动画播完时结束。
    /// </summary>
    Task ShowPeriodRow(bool show)
    {
        const double travel = -14;
        var gen = ++_periodGen;
        var done = new TaskCompletionSource();
        if (show == _periodShown && (show ? ChipRowHost.Visibility == Visibility.Visible
                                          : ChipRowHost.Visibility != Visibility.Visible))
        {
            done.TrySetResult();
            return done.Task;
        }
        _periodShown = show;

        var wasVisible = ChipRowHost.Visibility == Visibility.Visible;
        ChipRowHost.Visibility = Visibility.Visible;
        ChipRow.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var full = ChipRow.DesiredSize.Height;
        var from = wasVisible ? (ChipRowHost.ActualHeight > 0 ? ChipRowHost.ActualHeight : full) : 0;
        var to = show ? full : 0;
        if (show && !wasVisible)
        {
            ChipRow.BeginAnimation(UIElement.OpacityProperty, null);
            ChipRowShift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
            ChipRow.Opacity = 0;
            ChipRowShift.Y = travel;
        }

        void Settle()
        {
            ChipRowHost.BeginAnimation(HeightProperty, null);
            ChipRow.BeginAnimation(UIElement.OpacityProperty, null);
            ChipRowShift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
            ChipRowHost.Height = double.NaN;
            ChipRow.Opacity = show ? 1 : 0;
            ChipRowShift.Y = show ? 0 : travel;
            ChipRowHost.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (show) QueuePeriodPill(false);
            done.TrySetResult();
        }

        if (!Fx.Animated)
        {
            Settle();
            return done.Task;
        }

        var span = Fx.Span(show ? 460 : 400);
        var height = new System.Windows.Media.Animation.DoubleAnimation(from, to, span) { EasingFunction = EaseInOut };
        height.Completed += (_, _) =>
        {
            if (gen == _periodGen) Settle();
            else done.TrySetResult();
        };
        ChipRowHost.BeginAnimation(HeightProperty, height);
        ChipRow.BeginAnimation(UIElement.OpacityProperty,
            new System.Windows.Media.Animation.DoubleAnimation(show ? 1 : 0, span) { EasingFunction = EaseInOut });
        ChipRowShift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
            new System.Windows.Media.Animation.DoubleAnimation(show ? 0 : travel, span) { EasingFunction = EaseInOut });
        if (show) QueuePeriodPill(false);
        return done.Task;
    }

    static TranslateTransform LayerShift(FrameworkElement el)
    {
        if (el.RenderTransform is TranslateTransform shift && !shift.IsFrozen) return shift;
        shift = new TranslateTransform();
        el.RenderTransform = shift;
        return shift;
    }

    /// <summary>一层上浮出现，或下沉消失。lift 为假时只改透明度。</summary>
    static Task FloatLayer(FrameworkElement el, bool up, int delayMs, bool lift = true, int? ms = null)
    {
        var shift = LayerShift(el);
        shift.BeginAnimation(TranslateTransform.YProperty, null);
        el.BeginAnimation(UIElement.OpacityProperty, null);
        var fromY = lift ? (up ? 14 : shift.Y) : 0;
        var toY = lift ? (up ? 0 : 12) : 0;
        var fromO = up ? 0 : (el.Opacity < 0.02 ? 1 : el.Opacity);
        var toO = up ? 1.0 : 0;
        shift.Y = fromY;
        el.Opacity = fromO;
        if (!Fx.Animated)
        {
            shift.Y = 0;
            el.Opacity = toO;
            return Task.CompletedTask;
        }
        var dur = ms ?? (up ? 360 : 320);
        var delay = TimeSpan.FromMilliseconds(Fx.Span(delayMs).TotalMilliseconds);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var move = new System.Windows.Media.Animation.DoubleAnimation(fromY, toY, Fx.Span(dur))
        {
            BeginTime = delay,
            EasingFunction = EaseInOut,
        };
        var fade = new System.Windows.Media.Animation.DoubleAnimation(fromO, toO, Fx.Span(dur))
        {
            BeginTime = delay,
            EasingFunction = EaseInOut,
        };
        fade.Completed += (_, _) =>
        {
            shift.BeginAnimation(TranslateTransform.YProperty, null);
            el.BeginAnimation(UIElement.OpacityProperty, null);
            shift.Y = 0;
            el.Opacity = toO;
            done.TrySetResult();
        };
        if (lift) shift.BeginAnimation(TranslateTransform.YProperty, move);
        el.BeginAnimation(UIElement.OpacityProperty, fade);
        return done.Task;
    }

    /// <summary>从下往上逐层下沉。标题只渐隐，不位移。</summary>
    async Task FadeStoreBody()
    {
        var jobs = new List<Task>();
        FrameworkElement[] sinking = [PagerCard, ResultCard, GrowthHero];
        var live = sinking.Where(el => el.Visibility == Visibility.Visible && el.Opacity > 0.05).ToArray();
        for (var i = 0; i < live.Length; i++)
            jobs.Add(FloatLayer(live[i], up: false, delayMs: i * 70));
        if (StoreSectionHead.Opacity > 0.05)
            jobs.Add(FloatLayer(StoreSectionHead, up: false, delayMs: 0, lift: false));
        if (jobs.Count > 0 && Fx.Animated) await Task.WhenAll(jobs);
        ResultCard.Visibility = Visibility.Collapsed;
        GrowthHero.Visibility = Visibility.Collapsed;
        PagerCard.Visibility = Visibility.Collapsed;
        StoreSectionHead.BeginAnimation(UIElement.OpacityProperty, null);
        StoreSectionHead.Opacity = 0;
        if (StoreSectionHead.RenderTransform is TranslateTransform headShift)
        {
            headShift.BeginAnimation(TranslateTransform.YProperty, null);
            headShift.Y = 0;
        }
        Fx.SetShown(EmptyCard, false);
        Fx.SetShown(LoadCard, false);
    }

    int _busyGen;
    DateTime _busyShownAt;

    /// <summary>纯透明度渐显/渐隐（不做位移）。</summary>
    static Task FadeOpacity(FrameworkElement el, double to, int ms)
    {
        el.BeginAnimation(UIElement.OpacityProperty, null);
        var from = el.Opacity;
        if (Math.Abs(from - to) < 0.01)
        {
            el.Opacity = to;
            return Task.CompletedTask;
        }
        if (!Fx.Animated)
        {
            el.Opacity = to;
            return Task.CompletedTask;
        }
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fade = new System.Windows.Media.Animation.DoubleAnimation(from, to, Fx.Span(ms))
        {
            EasingFunction = EaseInOut,
        };
        fade.Completed += (_, _) =>
        {
            el.BeginAnimation(UIElement.OpacityProperty, null);
            el.Opacity = to;
            done.TrySetResult();
        };
        el.BeginAnimation(UIElement.OpacityProperty, fade);
        return done.Task;
    }

    /// <summary>清掉可能残留的位移，避免旧 Translate 动画被看成「往下沉」。</summary>
    void ResetStoreBusyShift()
    {
        StoreBusy.RenderTransform = null;
    }

    /// <summary>加载态缓入：只改不透明度。</summary>
    Task ShowStoreBusy(string text)
    {
        if (StoreBusyText is not null) StoreBusyText.Text = text;
        var gen = ++_busyGen;
        _busyShownAt = DateTime.UtcNow;
        StoreBusy.BeginAnimation(UIElement.OpacityProperty, null);
        ResetStoreBusyShift();
        if (!Fx.Animated)
        {
            StoreBusy.Opacity = 1;
            StoreBusy.Visibility = Visibility.Visible;
            return Task.CompletedTask;
        }
        // 已在场上就只改文案，避免切换货架时反复闪。
        if (StoreBusy.Visibility == Visibility.Visible && StoreBusy.Opacity > 0.85)
        {
            StoreBusy.Opacity = 1;
            return Task.CompletedTask;
        }
        StoreBusy.Visibility = Visibility.Visible;
        StoreBusy.Opacity = 0;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fade = new System.Windows.Media.Animation.DoubleAnimation(0, 1, Fx.Span(280))
        {
            EasingFunction = EaseInOut,
        };
        fade.Completed += (_, _) =>
        {
            if (gen != _busyGen) { done.TrySetResult(); return; }
            StoreBusy.BeginAnimation(UIElement.OpacityProperty, null);
            StoreBusy.Opacity = 1;
            done.TrySetResult();
        };
        StoreBusy.BeginAnimation(UIElement.OpacityProperty, fade);
        return done.Task;
    }

    /// <summary>加载态缓出：只改不透明度。</summary>
    Task HideStoreBusy()
    {
        var gen = ++_busyGen;
        StoreBusy.BeginAnimation(UIElement.OpacityProperty, null);
        ResetStoreBusyShift();
        if (!Fx.Animated || StoreBusy.Visibility != Visibility.Visible || StoreBusy.Opacity < 0.05)
        {
            StoreBusy.Opacity = 0;
            StoreBusy.Visibility = Visibility.Collapsed;
            return Task.CompletedTask;
        }
        var from = StoreBusy.Opacity;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fade = new System.Windows.Media.Animation.DoubleAnimation(from, 0, Fx.Span(320))
        {
            EasingFunction = EaseInOut,
        };
        fade.Completed += (_, _) =>
        {
            if (gen != _busyGen) { done.TrySetResult(); return; }
            StoreBusy.BeginAnimation(UIElement.OpacityProperty, null);
            StoreBusy.Opacity = 0;
            StoreBusy.Visibility = Visibility.Collapsed;
            done.TrySetResult();
        };
        StoreBusy.BeginAnimation(UIElement.OpacityProperty, fade);
        return done.Task;
    }

    /// <summary>转圈至少停留一会儿，避免数据太快时一闪就没。</summary>
    async Task WaitBusyDwell(int minMs = 650)
    {
        if (_busyShownAt == default) return;
        var left = Fx.Span(minMs) - (DateTime.UtcNow - _busyShownAt);
        if (left > TimeSpan.Zero) await Task.Delay(left);
    }

    int _fillGen;

    /// <summary>等这一页图标全部落定再揭幕；失败也算落定，避免永远转圈。</summary>
    static async Task WaitStoreIcons(Task? icons)
    {
        if (icons is null) return;
        try { await icons; }
        catch { /* 图标失败不算加载失败 */ }
    }

    /// <summary>
    /// 一次塞进几十张带阴影的卡片会占住界面线程。第一行同步放入，其余每帧补一行；
    /// 返回的任务在整页塞完时结束，加载态要等它。
    /// </summary>
    Task FillResults(IReadOnlyList<MarketRow> rows)
    {
        var gen = ++_fillGen;
        var per = Math.Max(1, _storeColumns);
        var shown = new System.Collections.ObjectModel.ObservableCollection<MarketRow>(rows.Take(per));
        ResultList.ItemsSource = shown;
        if (rows.Count <= per) return Task.CompletedTask;
        return Rest();

        async Task Rest()
        {
            for (var i = per; i < rows.Count; i += per)
            {
                await NextFrame();
                if (gen != _fillGen || !ReferenceEquals(ResultList.ItemsSource, shown)) return;
                foreach (var r in rows.Skip(i).Take(per)) shown.Add(r);
            }
        }
    }

    /// <summary>Completes after the next frame has been composed.</summary>
    static Task NextFrame()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler? h = null;
        h = (_, _) =>
        {
            System.Windows.Media.CompositionTarget.Rendering -= h;
            tcs.TrySetResult();
        };
        System.Windows.Media.CompositionTarget.Rendering += h;
        return tcs.Task;
    }

    /// <summary>
    /// 等卡片行 + 图标都落定后，加载态与内容纯透明度交叉渐隐/渐显——中间不空档。
    /// </summary>
    async Task ArriveStorePage(Task? icons = null, Task? fill = null)
    {
        ApplyStoreColumns();
        Fx.SetShown(LoadCard, false);
        FrameworkElement[] parts = [StoreSectionHead, ResultCard, GrowthHero, PagerCard];
        foreach (var el in parts)
        {
            el.BeginAnimation(UIElement.OpacityProperty, null);
            if (el.RenderTransform is TranslateTransform shift)
            {
                shift.BeginAnimation(TranslateTransform.YProperty, null);
                shift.Y = 0;
            }
            el.Opacity = 0;
        }

        var gen = _storeGen;
        // 真加载：行塞完、图标全取完（或确认失败）才揭幕，避免先出骨架再闪图标。
        if (fill is not null) await fill;
        if (gen != _storeGen) return;
        await WaitStoreIcons(icons);
        if (gen != _storeGen) return;
        await WaitBusyDwell(650);
        if (gen != _storeGen) return;

        // 透明内容先排一帧，交叉淡入时不再卡首次 measure。
        await NextFrame();
        if (gen != _storeGen) return;

        if (!Fx.Animated)
        {
            foreach (var el in parts)
                if (el.Visibility == Visibility.Visible) el.Opacity = 1;
            await HideStoreBusy();
            return;
        }

        // 转圈与内容同时做不透明度交叉：一边 1→0，一边 0→1，中间不会空白。
        var jobs = new List<Task> { HideStoreBusy() };
        foreach (var el in parts)
        {
            if (el.Visibility != Visibility.Visible) continue;
            jobs.Add(FadeOpacity(el, 1, 320));
        }
        await Task.WhenAll(jobs);
    }
    string _storeMode = "downloads";
    MarketRow? _heroRow;
    bool _crossShelf;
    bool _pinyin;
    int _storePeriod = 7;
    DispatcherTimer? _storeResizeTimer;

    /// <summary>失败/取消任务的原因：任务日志最后一条非空行。</summary>
    string? FailReason(JobRecord job)
    {
        if (job.Status is not ("failed" or "cancelled")) return null;
        try
        {
            var path = _state.Launcher.JobLogPath(job.Id);
            if (!File.Exists(path)) return null;
            var last = File.ReadLines(path).Where(l => l.Trim().Length > 0).LastOrDefault()?.Trim();
            return last is { Length: > 160 } ? last[..160] + "…" : last;
        }
        catch { return null; }
    }
    void HookResize()
    {
        SizeChanged += (_, _) =>
        {
            _storeResizeTimer ??= CreateStoreResizeTimer();
            _storeResizeTimer.Stop();
            _storeResizeTimer.Start();
        };
    }

    DispatcherTimer CreateStoreResizeTimer()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(240) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            ReloadStoreForResize();
        };
        return t;
    }

    void ReloadStoreForResize()
    {
        if (!IsVisible || PaneMarket.Visibility != Visibility.Visible) return;
        var now = StorePageSize;
        if (now == _storePageSizeUsed) return;
        var pageIndex = _storePageSizeUsed > 0 ? _from / _storePageSizeUsed : 0;
        _storePageSizeUsed = now;
        _from = Math.Max(0, pageIndex * now);
        _ = Load();
    }
    string _tag = "";
    PluginCatalog? _catalog;
    string _detailHome = "";
    string _chip = "全部";

    /// <summary>
    /// Empty = 发行号 pane; <c>dltasks</c> = download jobs; <c>recommend</c> = growth board;
    /// otherwise a shelf id from <see cref="Market.Shelves"/> (chip inside 插件商店).
    /// </summary>
    string _shelf = "recommend";
    bool _userPickedShelf;
    /// <summary>Left-rail destination: release | market | dltasks.</summary>
    string _nav = "release";
    int _from;
    CancellationTokenSource? _live;
    bool _chipsPainted;
    bool _chipSelecting;
    DispatcherTimer? _dlPoll;

    public DownloadView(MainWindow win)
    {
        InitializeComponent();
        _win = win;
        DataContext = _state;
        Loaded += (_, _) =>
        {
            PaintShelfChips();
            SyncStoreEdgeFade();
        };
        ShelfChipHost.SizeChanged += (_, _) => { if (ShelfPill.Opacity < 0.5) QueueShelfPill(false); };
        PeriodHost.SizeChanged += (_, _) => { if (PeriodPill.Opacity < 0.5 && ChipRowHost.Visibility == Visibility.Visible) QueuePeriodPill(false); };
        HookResize();
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is not true) StopDlPoll();
            else if (_nav == "dltasks") StartDlPoll();
        };
    }

    HttpClient Http()
    {
        var sec = Math.Clamp(_state.Settings.Download.InstallTimeoutSec, 5, 300);
        return new HttpClient { Timeout = TimeSpan.FromSeconds(sec) };
    }

    /// <summary>Switch the left rail / in-store chip. Used by --shot-page market|dl-tasks.</summary>
    public void OpenShelf(string id)
    {
        PaintShelfChips();
        if (id == "release")
        {
            NavRelease.IsChecked = true;
            return;
        }
        if (id is "dltasks" or "dl-tasks")
        {
            NavDlTasks.IsChecked = true;
            return;
        }
        // Any shelf id (or "market" / "recommend") opens 插件商店, then selects that chip.
        if (id is "market" or "store") id = "recommend";
        _shelf = id;
        if (id != "recommend") _userPickedShelf = true;
        NavMarket.IsChecked = true;
        SelectShelfChip(id);
    }

    void PaintShelfChips()
    {
        if (ShelfChipRow is null) return;
        var want = "recommend\n" + string.Join('\n',
            Market.Shelves.Select(s => $"{s.Id}|{s.Label}|{s.Query}|{s.Description}"));
        if (_chipsPainted && (ShelfChipRow.Tag as string) == want) return;
        ShelfChipRow.Children.Clear();
        AddShelfChip("recommend", "推荐", official: false);
        foreach (var s in Market.Shelves)
            AddShelfChip(s.Id, s.Label, official: s.Id == "host");
        ShelfChipRow.Tag = want;
        _chipsPainted = true;
        SelectShelfChip(string.IsNullOrEmpty(_shelf) || _shelf is "dltasks" ? "recommend" : _shelf);
        QueueShelfPill(false);
    }

    void AddShelfChip(string id, string label, bool official)
    {
        object content;
        if (official)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center,
            });
            row.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(4),
                Background = (Brush)FindResource("AccentWash"),
                Padding = new Thickness(5, 1, 5, 1),
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = "官方",
                    FontSize = 10,
                    Foreground = (Brush)FindResource("AccentText"),
                },
            });
            content = row;
        }
        else content = label;

        var btn = new RadioButton
        {
            Style = (Style)FindResource("FilterTab"),
            Content = content,
            Tag = id,
            GroupName = "shelfChip",
            Margin = new Thickness(0, 0, 8, 0),
            ToolTip = id == "recommend"
                ? "增长榜推荐"
                : official ? "官方宿主与运行时组件；部分包不适合直接安装" : null,
        };
        btn.Checked += ShelfChip_Checked;
        ShelfChipRow.Children.Add(btn);
    }

    void SelectShelfChip(string id)
    {
        if (ShelfChipRow is null) return;
        _chipSelecting = true;
        try
        {
            RadioButton? hit = null;
            foreach (var child in ShelfChipRow.Children.OfType<RadioButton>())
            {
                if ((child.Tag as string) == id) { hit = child; break; }
            }
            hit ??= ShelfChipRow.Children.OfType<RadioButton>().FirstOrDefault();
            if (hit is null) return;
            if (hit.IsChecked != true) hit.IsChecked = true;
            else if (hit.Tag is string tag) ApplyShelf(tag, reload: false);
        }
        finally { _chipSelecting = false; }
    }

    void ShelfChip_Checked(object sender, RoutedEventArgs e)
    {
        if (!_chipSelecting && _shelfPillReady)
        {
            // Start the slide on this frame; layout settles later and only retargets it.
            MovePill(ShelfChipHost, ShelfChipRow, ShelfPill, ShelfPillScale, ShelfPillShift, animate: true);
            _pillMovedAt = Environment.TickCount64;
        }
        QueueShelfPill(!_chipSelecting && _shelfPillReady);
        if (_chipSelecting || sender is not RadioButton { IsChecked: true, Tag: string id }) return;
        if (_nav != "market") return;
        _userPickedShelf = true;
        // Defer the reload below render so the first frames of the slide paint.
        Dispatcher.BeginInvoke(() => ApplyShelf(id, reload: true), DispatcherPriority.Background);
    }

    long _pillMovedAt;

    /// <summary>Hold the card rebuild until the selection slide has landed.</summary>
    async Task WaitPillLanded()
    {
        var left = (long)Fx.Span(380).TotalMilliseconds - (Environment.TickCount64 - _pillMovedAt);
        if (left > 0) await Task.Delay((int)left);
    }

    void ApplyShelf(string id, bool reload)
    {
        var changed = _shelf != id;
        _shelf = id;
        _storeMode = id == "recommend" ? "downloads" : "all";
        PaintShelfCopy();
        if (!reload) return;
        if (!changed && ResultList.ItemsSource is not null) return;
        _from = 0;
        _ = Load();
    }

    /// <summary>
    /// Edge fade must be opaque BgPage. A ContentOpacity wash still lets cards print
    /// through, so the scroll/pager boundary stays a hard cut.
    /// </summary>
    public void SyncStoreEdgeFade()
    {
        if (StoreEdgeFade is null) return;
        StoreEdgeFade.Background = (Brush)FindResource("BgPage");
    }

    public void OnShown()
    {
        PaintChrome();
        SyncStoreEdgeFade();
        if (_nav == "dltasks")
        {
            PaintDlTasks();
            StartDlPoll();
            if (PaneDlTasks is not null) Fx.MarkEnter(PaneDlTasks);
            return;
        }
        if (_nav != "market")
        {
            if (PaneRelease is not null) Fx.MarkEnter(PaneRelease);
            if (_state.Settings.Download.AutoFetchOnOpen) _ = FetchVersions();
            return;
        }
        if (PaneMarket is not null) Fx.MarkEnter(PaneMarket);
        QueueShelfPill(false);
        if (!_state.Settings.Download.AutoFetchOnOpen) return;
        _from = 0;
        _ = Load();
    }

    /// <summary>
    /// Startup warm: tool lines, npm DSH packument, and first page of every community shelf
    /// (fills <see cref="Market.Search"/> cache so chips open without a stall).
    /// </summary>
    public async Task WarmAsync()
    {
        await Dispatcher.InvokeAsync(PaintChrome);
        if (_state.Settings.Download.AutoFetchOnOpen)
        {
            try { await FetchVersions(); }
            catch { /* registry down — OnShown will retry */ }
        }

        var size = Math.Clamp((int)(_win.ActualHeight / 17), 10, 100);
        foreach (var shelf in Market.Shelves)
        {
            try
            {
                await Market.Search(shelf.Query, "", 0, CancellationToken.None, size);
            }
            catch { /* one shelf failing must not abort the rest */ }
        }
        try { MarketPinyinIndex.Prewarm(_state.Launcher); }
        catch { /* best-effort */ }
    }

    void PaintChrome()
    {
        PaintShelfChips();
        var def = _state.Settings.Launch.DefaultRelease;
        if (string.IsNullOrWhiteSpace(VersionBox.Text) && def.Length > 0)
            VersionBox.Text = def;
        _state.ReloadReleases();
        // State the tool situation before the user hits install and gets a wall of pnpm text.
        var node = Proc.Which("node");
        var pnpm = Pnpm.Locate();
        Tool(NodePip, NodeLine, node, "不在 PATH");
        Tool(PnpmPip, PnpmLine, pnpm, "不在 PATH");

        ToolHint.Text = (node, pnpm) switch
        {
            (null, null) => "DSH 要 Node 22.19+ 或 24+，安装走 pnpm。两个都装上再回来。",
            (null, _) => "DSH 要 Node 22.19+ 或 24+。",
            (_, null) => "DSH 官方用 pnpm 装插件。",
            _ => "",
        };
        if (node is null && _state.Settings.Launch.RuntimeInstall)
            ToolHint.Text += " 启动时会自动装进 runtime/node/。";
        Fx.SetShown(ToolHint, !(ToolHint.Text.Length == 0));
        PickTarget();
    }

    void Tool(Border pip, TextBlock line, string? path, string missing)
    {
        var found = path is not null;
        pip.Background = (System.Windows.Media.Brush)FindResource(found ? "Success" : "Warning");
        line.Text = path ?? missing;
        line.Foreground = (System.Windows.Media.Brush)FindResource(found ? "TextBody" : "WarningText");
    }

    void Log(string line) => Dispatcher.BeginInvoke(() =>
    {
        if (LogBox.Visibility != Visibility.Visible)
        {
            LogBox.Height = 170;
            Fx.SetShown(LogBox, true);
        }
        LogBox.AppendText(line + Environment.NewLine);
        LogBox.ScrollToEnd();
    });

    // ---- 发行号 -----------------------------------------------------------

    async void Fetch_Click(object sender, RoutedEventArgs e) => await FetchVersions();

    async Task FetchVersions()
    {
        FetchBtn.IsEnabled = false;
        try
        {
            Log("[PAD] GET " + Market.RegistryBase + "/@deepseek-ai/dsh");
            using var http = Http();
            var json = await http.GetStringAsync(Market.RegistryBase + "/@deepseek-ai/dsh");
            var catalog = DshPackument.Parse(json);

            _state.SetRemoteReleases(catalog.Versions.Select(v => new NpmRelease
            {
                Version = v.Version,
                TimeText = DshPackument.TimeText(v.Time),
                Latest = v.Latest,
            }));

            VersionBox.Items.Clear();
            foreach (var v in catalog.Versions) VersionBox.Items.Add(v.Version);
            if (catalog.Latest is not null)
            {
                VersionBox.Text = catalog.Latest;
                Log($"[PAD] {catalog.Versions.Count} 个版本，latest = {catalog.Latest}");
            }
        }
        catch (Exception ex)
        {
            Log("[PAD] 获取版本列表失败：" + ex.Message);
        }
        finally
        {
            FetchBtn.IsEnabled = true;
        }
    }

    async void InstallRemote_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: NpmRelease row }) return;
        VersionBox.Text = row.Version;
        await InstallVersion(row.Version);
    }

    async void Install_Click(object sender, RoutedEventArgs e)
    {
        var version = (VersionBox.Text ?? "").Trim();
        if (version.Length == 0)
        {
            MessageBox.Show("先填一个发行号，或点刷新获取版本列表。", "PAD",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await InstallVersion(version);
    }

    async Task InstallVersion(string version)
    {
        InstallBtn.IsEnabled = false;
        Fx.SetShown(Busy, true);
        if (InstallMeter is not null)
        {
            Fx.SetShown(InstallMeter, true);
            Fx.MeterTo(InstallMeter, 6);
        }
        var progress = new Progress<string>(Log);
        var poll = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        poll.Tick += (_, _) =>
        {
            var job = _state.Launcher.ListJobs()
                .FirstOrDefault(j => j.Kind == "install-version" && j.CanCancel);
            if (job is not null && InstallMeter is not null)
                Fx.MeterTo(InstallMeter, Math.Max(8, job.Percent));
        };
        poll.Start();
        var install = _state.Launcher.InstallRelease(version, progress, CancellationToken.None);
        try
        {
            var rec = await install;
            if (InstallMeter is not null) Fx.MeterTo(InstallMeter, 100);
            Log(rec.Verified
                ? $"[PAD] {version} 装好并通过校验：{rec.VerifyOutput}"
                : $"[PAD] {version} 装好了，但校验没通过：{rec.VerifyOutput}");
            _state.ReloadReleases();
            if (App.ShotPath is null) _win.ShowInner("versions", "版本选择");
        }
        catch (Exception ex)
        {
            Log(PadError.Describe(ex));
            _win.Toast(PadError.Describe(ex), bad: true);
        }
        finally
        {
            poll.Stop();
            InstallBtn.IsEnabled = true;
            Fx.SetShown(Busy, false);
        }
    }

    void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: DshRelease rel }) return;
        try
        {
            _state.Launcher.RemoveRelease(rel.Version);
            _state.ReloadReleases();
        }
        catch (Exception ex)
        {
            Log(PadError.Describe(ex));
        }
    }

    async void Verify_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: DshRelease rel }) return;
        var bin = _state.Launcher.DshBin(rel.Version);
        if (!System.IO.File.Exists(bin))
        {
            _win.Toast("这份发行号没有 CLI 入口，先重装。", bad: true);
            return;
        }
        try
        {
            Log($"[PAD] node {bin} --version");
            var res = await Proc.Run("node", $"\"{bin}\" --version",
                _state.Launcher.VersionDir(rel.Version), null, CancellationToken.None);
            rel.Verified = res.ExitCode == 0;
            rel.VerifyOutput = (res.Tail ?? "").Trim();
            _state.Launcher.SaveRelease(rel);
            _state.ReloadReleases();
            Log(rel.Verified
                ? $"[PAD] 校验通过：{rel.VerifyOutput}"
                : $"[PAD] 校验没通过：{rel.VerifyOutput}");
        }
        catch (Exception ex)
        {
            Log(PadError.Describe(ex));
        }
    }

    void OpenVersion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: DshRelease rel }) return;
        Reveal.Path(_state.Launcher.VersionDir(rel.Version));
    }

    // ---- 社区资源 --------------------------------------------------------

    void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PaneRelease is null || sender is not RadioButton { Tag: string tag, IsChecked: true }) return;
        if (tag is not ("release" or "market" or "dltasks")) return;

        var release = tag == "release";
        var tasks = tag == "dltasks";
        var market = tag == "market";
        FrameworkElement next = tasks && PaneDlTasks is not null ? PaneDlTasks
            : market ? PaneMarket
            : PaneRelease;
        var leaving =
            PaneDlTasks is not null && PaneDlTasks.Visibility == Visibility.Visible ? (FrameworkElement)PaneDlTasks
            : PaneRelease.Visibility == Visibility.Visible ? PaneRelease
            : PaneMarket.Visibility == Visibility.Visible ? PaneMarket
            : null;

        var paneChanged = leaving is null || !ReferenceEquals(leaving, next);
        if (!paneChanged) return;

        Fx.SwapPane(leaving, next, () =>
        {
            _nav = tag;
            PaneRelease.Visibility = release ? Visibility.Visible : Visibility.Collapsed;
            PaneMarket.Visibility = market ? Visibility.Visible : Visibility.Collapsed;
            if (PaneDlTasks is not null)
                PaneDlTasks.Visibility = tasks ? Visibility.Visible : Visibility.Collapsed;

            if (tasks)
            {
                PaintDlTasks();
                StartDlPoll();
                return;
            }

            StopDlPoll();
            if (release) return;

            // 插件商店: keep chip + search; PaneIn handles enter motion.
            ShowMarketList();
            PaintShelfChips();
            if (!_userPickedShelf || string.IsNullOrEmpty(_shelf) || _shelf == "dltasks")
            {
                _shelf = "recommend";
                _storeMode = "downloads";
            }
            SelectShelfChip(_shelf);
            PaintShelfCopy();
            PickTarget();
            if (!_userPickedShelf || ResultList.ItemsSource is null) { _from = 0; _ = Load(); }
        });
    }

    (string Id, string Label, string Query, string Description) Shelf()
    {
        if (_shelf == "recommend")
            return ("recommend", "推荐", "", "增长榜：最近下载或 Star 涨得最快的包。");
        return Market.Shelves.FirstOrDefault(s => s.Id == _shelf, Market.Shelves[0]);
    }

    void PaintShelfCopy()
    {
        var shelf = Shelf();
        StoreHeading.Text = "插件商店";
        StoreSubtitle.Text = "社区插件 · 一键安装";
        if (ShelfTitle is not null)
        {
            ShelfTitle.Text = shelf.Id == "recommend"
                ? "推荐 · 增长榜"
                : $"{shelf.Label} · {shelf.Query}";
            ShelfTitle.ToolTip = shelf.Query.Length > 0 ? shelf.Query : null;
        }
        SearchBox.Tag = shelf.Id == "recommend"
            ? "搜索会切到「全部」再查名称、作者、关键词…"
            : $"在“{shelf.Label}”中搜索名称、作者、关键词…";
    }

    void EmptyCross_Click(object sender, RoutedEventArgs e)
    {
        // Keep the typed query; jump to the full plugin shelf chip.
        _shelf = "";
        SelectShelfChip("plugin");
        ApplyShelf("plugin", reload: true);
    }

    void StartDlPoll()
    {
        StopDlPoll();
        var sec = Math.Clamp(_state.Settings.Other.RefreshSec, 1, 30);
        _dlPoll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(sec) };
        _dlPoll.Tick += (_, _) => PaintDlTasks();
        _dlPoll.Start();
    }

    void StopDlPoll()
    {
        _dlPoll?.Stop();
        _dlPoll = null;
    }

    /// <summary>没有图标的任务（插件包）后台补一次；拿到后整页重画。</summary>
    async Task FillJobIconsAsync(IReadOnlyList<JobRecord> jobs)
    {
        var changed = false;
        foreach (var job in jobs)
        {
            if (Dispatcher.HasShutdownStarted) return;
            try
            {
                var path = await IconService.FillJobAsync(_state.Launcher, job, CancellationToken.None);
                if (!string.IsNullOrWhiteSpace(path)) changed = true;
            }
            catch { /* 离线/包没图标就保持占位 */ }
        }
        if (changed) _ = Dispatcher.BeginInvoke(new Action(PaintDlTasks));
    }

    void PaintDlTasks()
    {
        if (DlRunning is null || DlDone is null) return;
        var jobs = _state.Launcher.ListJobs();
        var run = DownloadDock.Running(jobs).Select(j => new JobCard { Job = j }).ToList();
        var done = DownloadDock.Done(jobs, 40).Select(j => new JobCard { Job = j, Reason = FailReason(j), IconPath = IconService.ForJob(_state.Launcher, j) }).ToList();
        var missing = done.Where(c => string.IsNullOrWhiteSpace(c.IconPath)).Select(c => c.Job).ToList();
        DlRunning.ItemsSource = run;
        DlDone.ItemsSource = done;
        if (missing.Count > 0) _ = FillJobIconsAsync(missing);
        if (DlEmptyRun is not null)
            DlEmptyRun.Visibility = run.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (DlEmptyDone is not null)
            DlEmptyDone.Visibility = done.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: JobRecord job }) return;
        try
        {
            _state.Launcher.CancelJob(job.Id);
            _win.Toast("已取消 " + job.Title);
            PaintDlTasks();
        }
        catch (Exception ex) { _win.Toast(PadError.Describe(ex), bad: true); }
    }

    /// <summary>删除任务行：正在跑的先取消并结束进程，再从列表移除；不动已装文件。</summary>
    void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: JobRecord job }) return;
        try
        {
            _state.Launcher.DeleteJob(job.Id);
            _win.Toast("已删除 " + job.Title);
            PaintDlTasks();
        }
        catch (Exception ex) { _win.Toast(PadError.Describe(ex), bad: true); }
    }

    /// <summary>暂停/继续：worker 在复制/压缩循环里检查暂停标记（不丢进度）。</summary>
    void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: JobRecord job }) return;
        try
        {
            if (job.Paused) _state.Launcher.ResumeJob(job.Id);
            else _state.Launcher.PauseJob(job.Id);
            _win.Toast(job.Paused ? "已继续 " + job.Title : "已暂停 " + job.Title);
            PaintDlTasks();
        }
        catch (Exception ex) { _win.Toast(PadError.Describe(ex), bad: true); }
    }

    void Row_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: JobRecord job }) return;
        // 点按钮时不触发行的“打开文件夹”。
        if (e.OriginalSource is DependencyObject source && FindParent<Button>(source) is not null) return;
        Reveal.Path(FolderOf(job));
    }

    static T? FindParent<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T hit) return hit;
            node = System.Windows.Media.VisualTreeHelper.GetParent(node);
        }
        return null;
    }
    void Forget_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: JobRecord job }) return;
        _state.Launcher.ForgetJob(job.Id);
        PaintDlTasks();
    }

    void DlClear_Click(object sender, RoutedEventArgs e)
    {
        foreach (var job in DownloadDock.Done(_state.Launcher.ListJobs(), 1000))
            _state.Launcher.ForgetJob(job.Id);
        PaintDlTasks();
    }

    void DlRefresh_Click(object sender, RoutedEventArgs e) => PaintDlTasks();

    void Folder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: JobRecord job }) return;
        Reveal.Path(FolderOf(job));
        _ = _state.Launcher.ReadJobLog(job.Id);
    }

    string FolderOf(JobRecord job)
    {
        if (job.Kind == "install-version" && !string.IsNullOrWhiteSpace(job.Label))
            return _state.Launcher.VersionDir(job.Label);
        if (!string.IsNullOrWhiteSpace(job.Instance))
        {
            var inst = _state.Instances.FirstOrDefault(i => i.Id == job.Instance);
            if (inst is { Home.Length: > 0 }) return inst.Home;
            return _state.Launcher.InstanceDir(job.Instance);
        }
        return _state.Launcher.JobLogPath(job.Id);
    }

    /// <summary>对照 PCL：版本=该实例钉的发行号，装到=哪份实例。</summary>
    void PickTarget()
    {
        _pickingTarget = true;
        try
        {
            TargetInstance.SelectedItem = _state.SelectedInstance ?? _state.Instances.FirstOrDefault();
            var picked = TargetInstance.SelectedItem as Instance;
            PaintTargetRelease(picked);
            PaintTargetProfiles(picked);
        }
        finally { _pickingTarget = false; }
    }

    void TargetInstance_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_pickingTarget) return;
        if (TargetInstance.SelectedItem is not Instance inst)
        {
            PaintTargetRelease(null);
            PaintTargetProfiles(null);
            return;
        }
        if (_state.SelectedInstance?.Id != inst.Id) _state.SelectedInstance = inst;
        _pickProfile = null;                 // 换实例 → profile 重新挑
        PaintTargetRelease(inst);
        PaintTargetProfiles(inst);
    }

    /// <summary>「装到」第二颗下拉：这个实例的配置树。默认跟随当前/上次用的 profile。</summary>
    void PaintTargetProfiles(Instance? inst)
    {
        if (TargetProfile is null) return;
        _pickingTarget = true;
        try
        {
            var profiles = inst is null ? [] : _state.Launcher.Profiles(inst);
            TargetProfile.ItemsSource = profiles;
            var want = _pickProfile;
            if (want is null && inst is not null && _state.SelectedInstance?.Id == inst.Id)
                want = _state.SelectedProfile?.Name;
            want ??= inst?.LastProfile;
            var hit = profiles.FirstOrDefault(p => p.Name == want) ?? profiles.FirstOrDefault();
            TargetProfile.SelectedItem = hit;
            _pickProfile = hit?.Name;
            TargetProfile.IsEnabled = profiles.Count > 0;
            if (TargetHint is not null)
                TargetHint.Text = hit is null ? "" : $"profiles/{hit.Name}/node_modules";
        }
        finally { _pickingTarget = false; }
    }

    void TargetProfile_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_pickingTarget) return;
        if (TargetProfile.SelectedItem is DshProfile p) _pickProfile = p.Name;
    }

    void PaintTargetRelease(Instance? inst)
    {
        TargetRelease.Items.Clear();
        var ver = inst?.Dsh.Version ?? "";
        if (ver.Length == 0) return;
        TargetRelease.Items.Add(ver);
        TargetRelease.SelectedIndex = 0;
    }

    string? LaunchProfile(Instance inst)
    {
        // 用户在「装到」里明确选过 profile 就听他的（还要确认这棵树还在）。
        if (_pickProfile is { Length: > 0 } picked
            && _state.Launcher.Profiles(inst).Any(p => p.Name == picked))
            return picked;
        if (_state.SelectedInstance?.Id == inst.Id && _state.SelectedProfile is { } cur)
            return cur.Name;
        return inst.LastProfile ?? _state.Launcher.Profiles(inst).FirstOrDefault()?.Name;
    }

    void Warn(string text)
    {
        var t = (text ?? "").Trim();
        MarketErrorTitle.Text = t.Contains("超时")
            ? "npm 搜索超时"
            : t.Contains("PA033") || t.Contains("没连上")
                ? "npm 搜索没连上"
                : "搜索出错了";
        // 原始错误里 error[..]/--> / | / = help 是给命令行看的；界面只留一句人话 + 一条建议。
        var lines = t.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim().TrimStart('|').Trim())
            .Where(x => x.Length > 0)
            .Where(x => !x.StartsWith("error[") && !x.StartsWith("-->") && !x.Contains("= help"))
            .Select(x => x.Contains("A task was canceled", StringComparison.OrdinalIgnoreCase)
                ? "请求被取消（超时或网络不通）。" : x)
            .ToList();
        MarketErrorText.Text = lines.Count > 0
            ? string.Join(" ", lines.Take(2))
            : "请求被取消或超时，可能是网络 / 代理 / registry 不通。";
        MarketErrorHint.Text = "检查网络或代理；或到 设置 → 下载 改 registry / 超时，然后点重试。";
        Fx.SetShown(MarketError, true);
    }

    void Working(string? what)
    {
        MarketStatus.Text = what ?? "";
        Fx.SetShown(MarketStatus, !(what is null));
        Fx.SetShown(MarketBusy, !(what is null));
        SearchBtn.IsEnabled = what is null;
        ClearBtn.IsEnabled = what is null;
    }

    /// <summary>搜索在当前芯片范围内做；推荐榜收到搜索词时切到「全部」。</summary>
    void RunSearch()
    {
        _from = 0;
        if (_shelf == "recommend")
        {
            _shelf = "";
            SelectShelfChip("plugin");
            ApplyShelf("plugin", reload: true);
            return;
        }
        _ = Load();
    }

    void Search_Click(object sender, RoutedEventArgs e) => RunSearch();

    void Clear_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        _from = 0;
        _ = Load();
    }

    void Search_Key(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        RunSearch();
    }

    void First_Click(object sender, RoutedEventArgs e) { _from = 0; _ = Load(); }

    void Prev_Click(object sender, RoutedEventArgs e)
    {
        _from = Math.Max(0, _from - StorePageSize);
        _ = Load();
    }

    void Next_Click(object sender, RoutedEventArgs e)
    {
        _from += StorePageSize;
        _ = Load();
    }

    /// <summary>
    /// One page of one shelf. The registry is the only source; when it is unreachable
    /// the shelf says so instead of rendering as an empty list.
    /// </summary>
    int _storeGen;

    async Task Load()
    {
        if (_nav != "market" || _shelf == "dltasks") return;
        var gen = ++_storeGen;
        if (_shelf == "recommend" || _storeMode != "all")
        {
            await LoadGrowth(gen);
            return;
        }
        _live?.Cancel();
        var live = _live = new CancellationTokenSource();

        Fx.SetShown(MarketError, false);
        ResultCount.Text = "";
        Fx.SetShown(EmptyCard, false);
        if (EmptyCrossBtn is not null) Fx.SetShown(EmptyCrossBtn, false);
        Working("正在搜索");

        try
        {
            _storePageSizeUsed = StorePageSize;
            _crossShelf = false;
            _pinyin = false;
            var text = SearchBox.Text.Trim();
            var shelf = Shelf();
            var status = new Progress<string>(x => StoreSectionRight.Text = x);
            var fetch = Task.Run(() => MarketCommands.SearchShelfAsync(
                _state.Launcher, shelf.Id, shelf.Query, text, _from, _storePageSizeUsed,
                status, live.Token));
            // 先让转圈淡入，再收旧内容；两段都用透明度，避免中间空白。
            var busyIn = ShowStoreBusy("正在加载");
            await FadeStoreBody();
            await busyIn;
            if (live.IsCancellationRequested || gen != _storeGen) return;
            await ShowPeriodRow(false);
            if (live.IsCancellationRequested || gen != _storeGen) return;
            var found = await fetch;
            if (live.IsCancellationRequested || gen != _storeGen) return;
            _pinyin = found.Pinyin;
            var page = found.Page;
            var (icons, fill) = Paint(page);
            await ArriveStorePage(icons, fill);
            PrefetchNext(page);
            MarketPinyinIndex.Prewarm(_state.Launcher);
            if (App.ShotPage == "market-more")
            {
                Fx.SetShown(StoreMore, true);
                StoreMoreAngle.Angle = 180;
            }
            if (App.ShotPage == "market-detail" && page.Rows.Count > 0) QueueStoreDetailShot(page.Rows[0]);
        }
        catch (OperationCanceledException)
        {
            _ = HideStoreBusy();
            return;
        }
        catch (Exception ex)
        {
            _ = HideStoreBusy();
            ResultList.ItemsSource = null;
            ResultCard.Visibility = Visibility.Collapsed;
            Warn(PadError.Describe(ex));
        }
        finally
        {
            if (!live.IsCancellationRequested)
            {
                Fx.SetShown(LoadCard, false);
                Working(null);
            }
        }
    }

    bool _storeShotQueued;
    void QueueStoreDetailShot(MarketRow row)
    {
        if (_storeShotQueued || App.ShotPath is not { Length: > 0 } shot) return;
        _storeShotQueued = true;
        var dir = System.IO.Path.GetDirectoryName(shot) ?? "";
        var path = System.IO.Path.Combine(dir, System.IO.Path.GetFileNameWithoutExtension(shot) + "-store-detail.png");
        var inst = TargetInstance.SelectedItem as Instance;
        var plugin = new PluginRow
        {
            Profile = inst?.Name ?? "",
            Spec = row.Name,
            Version = row.Version,
            Description = row.DescriptionText,
            Repository = row.Repository,
            Keywords = row.Keywords,
            IconPath = row.IconPath,
            Latest = "",
        };
        var win = new PluginDetailWindow(
            plugin,
            install: null,
            versions: async () =>
            {
                var cat = await Market.FetchPackument(row.Name, CancellationToken.None);
                return cat.Versions.Count > 0 ? cat.Versions : cat.Files.Select(f => f.Version).ToList();
            },
            installVersion: _ => { })
        {
            ShowInTaskbar = false,
            Left = -20000,
            Top = -20000,
        };
        win.Loaded += (_, _) =>
        {
            win.Dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    var wait = Task.WhenAll(
                        Task.WhenAny(win.ShotsLoaded, Task.Delay(TimeSpan.FromSeconds(8))),
                        Task.WhenAny(win.VersionsLoaded, Task.Delay(TimeSpan.FromSeconds(8))));
                    await wait;
                    win.RenderTo(path);
                }
                finally { win.Close(); }
            }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        };
        win.Show();
    }

    bool _tagBuilding;
    void BuildTagChoices(List<MarketRow> rows)
    {
        _tagBuilding = true;
        try
        {
            var tags = rows.Select(r => r.TagText)
                .Where(t => t.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList();
            var items = new List<object> { "全部" };
            items.AddRange(tags);
            TagBox.ItemsSource = items;
            TagBox.SelectedItem = _tag.Length > 0 && tags.Contains(_tag, StringComparer.OrdinalIgnoreCase)
                ? tags.First(t => string.Equals(t, _tag, StringComparison.OrdinalIgnoreCase))
                : "全部";
        }
        finally { _tagBuilding = false; }
    }

    void Tag_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_tagBuilding) return;
        _tag = TagBox.SelectedItem as string ?? "";
        if (_tag == "全部") _tag = "";
        if (_lastPage is not null) Paint(_lastPage);
    }

    List<MarketRow> ApplyStoreControls(List<MarketRow> rows)
    {
        IEnumerable<MarketRow> q = rows;
        if (_tag.Length > 0) q = q.Where(r => string.Equals(r.TagText, _tag, StringComparison.OrdinalIgnoreCase));
        if (InstalledOnly.IsChecked == true && TargetInstance.SelectedItem is Instance inst)
        {
            var have = _state.Launcher.ListBundles(inst)
                .Select(b => b.Spec)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            q = q.Where(r => have.Contains(r.Name));
        }

        if (CompatibleOnly.IsChecked == true) q = q.Where(r => r.Compatible != false);

        var collection = (CollectionBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";
        if (collection == "featured") q = q.Where(r => MarketStats.IsFeatured(r.Name));
        else if (collection == "official") q = q.Where(r => r.Name.StartsWith("@deepseek-ai/", StringComparison.OrdinalIgnoreCase));
        else if (collection == "community") q = q.Where(r => !r.Name.StartsWith("@deepseek-ai/", StringComparison.OrdinalIgnoreCase));

        var sort = (SortBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "hot";
        q = sort switch
        {
            "date" => q.OrderByDescending(r => DateTimeOffset.TryParse(r.Date, out var t) ? t : DateTimeOffset.MinValue),
            "name" => q.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
            "stars" => q.OrderByDescending(r => r.Stars),
            "downloads" => q.OrderByDescending(r => r.WeeklyDownloads),
            _ => q.OrderByDescending(r => r.Popularity),
        };
        return q.ToList();
    }

    void StoreMore_Click(object sender, RoutedEventArgs e)
    {
        var show = StoreMore.Visibility != Visibility.Visible;
        Fx.SetShown(StoreMore, show);
        StoreMoreAngle.Angle = show ? 180 : 0;
    }

    void Sort_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_lastPage is not null) Paint(_lastPage);
    }

    void StoreFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (_lastPage is not null) Paint(_lastPage);
    }

    void MarketFav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: MarketRow row }) return;
        var on = Favorites.Toggle(_state.Launcher.Root, row.Name);
        row.Favorite = on;
        _win.Toast(on ? $"已收藏 {row.Title}" : $"取消收藏 {row.Title}");
    }

    static bool LooksLikeDsh(MarketRow row)
    {
        var text = (row.Name + " " + row.Description + " " + string.Join(" ", row.Keywords)).ToLowerInvariant();
        return text.Contains("dsh") || text.Contains("deepseek");
    }

    /// <summary>绑定这一页的卡片；返回图标任务 + 填行任务，加载态要等它们才收。</summary>
    (Task Icons, Task Fill) Paint(MarketPage page)
    {
        _from = page.From;
        _lastPage = page;
        BuildTagChoices(page.Rows);
        var shown = _pinyin ? page.Rows : ApplyStoreControls(page.Rows);
        foreach (var r in shown) r.Favorite = Favorites.IsFavorite(_state.Launcher.Root, r.Name);
        var text = SearchBox.Text.Trim();
        var best = 0;
        if (text.Length > 0 && !_pinyin)
        {
            shown = Market.RankForQuery(shown, text);
            best = shown.Count == 0 ? 0 : shown.Max(r => Market.QueryScore(r, text));
        }
        var fill = FillResults(shown);
        var icons = LoadMarketIcons(page.Rows);
        _ = EnrichStoreAsync(shown);   // 星数/下载数是次要信息，不挡加载态
        GrowthHero.Visibility = Visibility.Collapsed;
        StoreMoreBtn.Visibility = Visibility.Visible;
        StoreModeLink.Content = "‹ 返回推荐";
        var shelf = Shelf();
        StoreSectionTitle.Text = shelf.Label;
        StoreSectionSub.Text = $"{page.Total:N0} 个包";
        StoreSectionRight.Text = text.Length == 0 ? ""
            : _pinyin ? $"拼音/首字母匹配 {shown.Count} 个"
            : best == 0 ? "没有精确命中 · 按 npm 相关性显示"
            : $"搜索“{text}”";
        ScrollStoreTop();
        ResultCard.Visibility = page.Rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        var empty = page.Rows.Count == 0;
        EmptyText.Text = text.Length == 0
            ? $"“{shelf.Label}”暂时没有结果"
            : $"“{shelf.Label}”中没有结果，可换关键词或到「全部」再搜";
        Fx.SetShown(EmptyCard, empty);
        if (EmptyCrossBtn is not null)
        {
            var offerCross = empty && shelf.Id is not ("plugin" or "recommend");
            Fx.SetShown(EmptyCrossBtn, offerCross);
            EmptyCrossBtn.Content = text.Length > 0 ? "在全部插件中搜索" : "浏览全部插件";
        }

        ResultCount.Text = $"{page.Total:N0} 个插件";
        PageText.Text = $"{page.PageIndex + 1} / {page.PageCount}";
        FirstBtn.IsEnabled = page.HasPrev;
        PrevBtn.IsEnabled = page.HasPrev;
        NextBtn.IsEnabled = page.HasNext;
        var showPager = !_crossShelf && !_pinyin && page.PageCount > 1;
        PagerCard.BeginAnimation(UIElement.OpacityProperty, null);
        PagerCard.Visibility = showPager ? Visibility.Visible : Visibility.Collapsed;
        if (showPager) PagerCard.Opacity = 0;
        return (icons, fill);
    }

    /// <summary>
    /// Install into a profile of the selected instance, through DSH's own
    /// <c>dsh plugin --profile &lt;name&gt; add</c>. The registry is asked first whether the
    /// package declares <c>dsh.bundle</c>, so the result can say whether it became a
    /// profile layer or a plain dependency rather than leaving the user to guess.
    /// </summary>
    /// <summary>Next page is one click away in practice; loading it while the user reads
    /// this one makes pagination feel instant (Market keeps a short-lived page cache).</summary>
    void PrefetchNext(MarketPage page)
    {
        if (!page.HasNext || _shelf is "dltasks" or "recommend") return;
        var query = Shelf().Query;
        var text = SearchBox.Text;
        var size = page.Size;
        var next = page.From + page.Size;
        _ = Task.Run(async () =>
        {
            try { await Market.Search(query, text, next, CancellationToken.None, size); }
            catch { /* prefetch is best-effort; the real load will bundle errors */ }
        });
    }

    // ── 商店的三种模式：全部 / Star 增长 / 下载增长；增长不是单独页面。 ──

    void StoreMode_Changed(object sender, RoutedEventArgs e)
    {
        // Hidden radios kept for shot helpers; prefer shelf chips in the UI.
        if (_chipSelecting || sender is not RadioButton { IsChecked: true } rb) return;
        _storeMode = rb.Name == nameof(ModeStars) ? "stars"
            : rb.Name == nameof(ModeDownloads) ? "downloads" : "all";
        if (!IsLoaded || _nav != "market") return;
        if (_storeMode == "all" && _shelf == "recommend")
        {
            _shelf = "";
            SelectShelfChip("plugin");
            ApplyShelf("plugin", reload: true);
            return;
        }
        if (_storeMode != "all" && _shelf != "recommend")
        {
            _shelf = "";
            SelectShelfChip("recommend");
            ApplyShelf("recommend", reload: true);
            return;
        }
        _from = 0;
        _ = Load();
    }

    void StorePeriod_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { IsChecked: true } rb) return;
        _storePeriod = rb.Name == nameof(PeriodDay) ? 1 : rb.Name == nameof(PeriodMonth) ? 30 : 7;
        if (_periodPillReady)
        {
            MovePill(PeriodHost, PeriodRow, PeriodPill, PeriodPillScale, PeriodPillShift, animate: true);
            _pillMovedAt = Environment.TickCount64;
        }
        QueuePeriodPill(_periodPillReady);
        if (!IsLoaded || _shelf != "recommend") return;
        _from = 0;
        Dispatcher.BeginInvoke(() => _ = Load(), DispatcherPriority.Background);
    }

    /// <summary>供 --shot-page 用：在全部插件里搜一个词。</summary>
    public void ShowSearchShot(string text)
    {
        Dispatcher.BeginInvoke(async () =>
        {
            OpenShelf("plugin");
            SearchBox.Text = text;
            _from = 0;
            await Load();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>供 --shot-page 用：切到全部插件模式。</summary>
    public void ShowAllShot()
    {
        Dispatcher.BeginInvoke(() => OpenShelf("plugin"),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>供 --shot-page 用：打开推荐增长榜。</summary>
    public void ShowGrowthShot()
    {
        Dispatcher.BeginInvoke(() => OpenShelf("recommend"),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    void ScrollStoreTop() =>
        Dispatcher.BeginInvoke(() => MarketScroll.ScrollToVerticalOffset(0),
            System.Windows.Threading.DispatcherPriority.Loaded);

    void StoreModeLink_Click(object sender, RoutedEventArgs e)
    {
        if (_shelf == "recommend")
        {
            _shelf = "";
            SelectShelfChip("plugin");
            ApplyShelf("plugin", reload: true);
        }
        else
        {
            _shelf = "";
            SelectShelfChip("recommend");
            ApplyShelf("recommend", reload: true);
        }
    }

    void HeroInstall_Click(object sender, RoutedEventArgs e)
    {
        if (_heroRow is not null) _ = InstallFrom(_heroRow, "", null);
    }

    void HeroOpen_Click(object sender, RoutedEventArgs e)
    {
        if (_heroRow is not null) OpenStoreDetail(_heroRow);
    }

    async Task LoadGrowth(int gen)
    {
        MarketPinyinIndex.Prewarm(_state.Launcher);
        Fx.SetShown(MarketError, false);
        Fx.SetShown(EmptyCard, false);
        StoreMoreBtn.Visibility = Visibility.Collapsed;
        if (EmptyCrossBtn is not null) Fx.SetShown(EmptyCrossBtn, false);
        Working("正在算增长榜");
        Task? icons = null;
        try
        {
            var growthStatus = new Progress<string>(x => StoreSectionRight.Text = x);
            var fetch = Task.Run(() => MarketMetrics.GetAsync(_state.Launcher, false,
                growthStatus, CancellationToken.None));
            var busyIn = ShowStoreBusy("正在加载");
            await FadeStoreBody();
            await busyIn;
            if (gen != _storeGen) return;
            _ = ShowStoreBusy("正在算增长榜");
            await ShowPeriodRow(true);
            if (gen != _storeGen) return;
            var boards = await fetch;
            if (gen != _storeGen) return;
            var periodKey = _storePeriod switch { 1 => "day", 7 => "week", _ => "month" };
            var metric = _storeMode == "stars" ? "stars" : "downloads";
            var board = boards.Boards.FirstOrDefault(b => b.Key == $"{metric}-{periodKey}");
            var rows = (board?.Rows ?? []).Take(12).Select(m => new MarketRow
            {
                Name = m.Name,
                Version = m.Version,
                Publisher = m.Publisher,
                Description = m.Description,
                Repository = m.Repository,
                Homepage = m.Repository,
                Date = DateTimeOffset.UtcNow.ToString("o"),
                Rank = m.Rank,
                GrowthBadge = m.GrowthText,
                GrowthTotal = m.TotalText,
                GrowthSign = m.GrowthSign,
            }).ToList();
            foreach (var r in rows) r.Favorite = Favorites.IsFavorite(_state.Launcher.Root, r.Name);
            var fill = FillResults(rows);
            StoreSectionTitle.Text = "推荐";
            StoreModeLink.Content = "浏览全部插件 ›";
            StoreSectionSub.Text = $"{board?.Label ?? ""} · Top {rows.Count}";
            StoreSectionRight.Text = boards.FromCache ? $"缓存 · {boards.GeneratedText}" : $"更新 · {boards.GeneratedText}";
            var any = rows.Count > 0;
            ResultCard.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            Fx.SetShown(EmptyCard, !(any));
            EmptyText.Text = "暂时拉不到增长数据（网络或 GitHub 限流）";
            if (any)
            {
                ScrollStoreTop();
                var periodWord = _storePeriod switch
                {
                    1 => "今日",
                    7 => "本周",
                    _ => "本月",
                };
                var metricWord = metric == "stars" ? " Star " : "下载";
                HeroKicker.Text = $"{periodWord}{metricWord}增长最快";
                _heroRow = rows[0];
                HeroTitle.Text = _heroRow.Title;
                HeroDesc.Text = $"{_heroRow.DescriptionText} · 本期 {_heroRow.GrowthBadge}";
                HeroIcon.Spec = _heroRow.Name;
                GrowthHero.Visibility = Visibility.Visible;
                icons = ShowHeroIcon(rows);
            }
            await ArriveStorePage(icons, fill);
        }
        catch (Exception ex)
        {
            _ = HideStoreBusy();
            ResultList.ItemsSource = null;
            ResultCard.Visibility = Visibility.Collapsed;
            Fx.SetShown(EmptyCard, true);
            EmptyText.Text = PadError.Describe(ex);
        }
        finally
        {
            Fx.SetShown(LoadCard, false);
            Working(null);
        }
    }

    /// <summary>货架行逐个补“插件自己的图标”：官方 DSH 包用内置品牌图标，其余按 npm 元数据里的仓库取 logo。</summary>
    /// <summary>给卡片补星数 / 周下载 / 兼容性；慢，不挡列表。</summary>
    async Task EnrichStoreAsync(IReadOnlyList<MarketRow> rows)
    {
        if (rows.Count == 0) return;
        var kernel = (TargetInstance.SelectedItem as Instance)?.Dsh.Version
                     ?? _state.SelectedInstance?.Dsh.Version ?? "";
        try { await MarketStats.EnrichAsync(_state.Launcher, rows, kernel, null, CancellationToken.None); }
        catch { /* 取不到就少个数字 */ }
    }

    async Task ShowHeroIcon(IReadOnlyList<MarketRow> rows)
    {
        await LoadMarketIcons(rows);
        if (_heroRow is not null) HeroIcon.IconPath = _heroRow.IconPath;
    }

    async Task LoadMarketIcons(IReadOnlyList<MarketRow> rows)
    {
        // Icons are one GitHub fetch each; a semaphore keeps the page responsive while
        // letting several rows finish together instead of waiting one by one.
        using var gate = new SemaphoreSlim(6);
        await Task.WhenAll(rows.Select(async row =>
        {
            await gate.WaitAsync();
            try
            {
                // 官方 package icon 优先；没有自定义 icon 再退回仓库 logo。
                var icon = await NpmPackageIcon.EnsureAsync(_state.Launcher.Root, row.Name, row.Version,
                                CancellationToken.None)
                           ?? await BundleIcon.EnsureFromRepo(_state.Launcher.Root, row.Name,
                               row.Repository.Length > 0 ? row.Repository : row.Homepage,
                               CancellationToken.None);
                if (icon is not null) row.IconPath = icon;
            }
            catch { /* 网络/仓库不可达就用占位字形 */ }
            finally { row.IconPending = false; gate.Release(); }
        }));
    }

    async Task InstallFrom(MarketRow row, string version, FrameworkElement? busy)
    {
        if (TargetInstance.SelectedItem is not Instance inst)
        {
            _win.ShowInner("versions", "版本选择");
            return;
        }
        var profile = LaunchProfile(inst);
        if (string.IsNullOrEmpty(profile))
        {
            _win.ShowInner("instance", "版本设置");
            return;
        }

        _state.SelectedInstance = inst;
        Fx.SetShown(MarketError, false);
        if (busy is Button btn) btn.IsEnabled = false;
        _win.Toast($"开始装 {row.Name} → {inst.Name} · {profile}");
        Working($"正在装 {row.Name}");
        try
        {
            var result = await MarketCommands.InstallAsync(
                _state.Launcher, inst, profile, row.Name, version, null, CancellationToken.None,
                probeVersion: row.Version);
            if (!result.Apply.Ok)
            {
                Warn(result.Apply.Message);
                _win.Toast($"{row.Name} 没装成：{result.Apply.Message}", bad: true);
                return;
            }
            var where = $"{inst.Name} · {profile}";
            _state.Reload();
            PickTarget();
            var layerNote = result.IsBundle
                ? "已进 dsh.profile.bundles"
                : "是普通依赖，没进 dsh.profile.bundles";
            _win.Toast($"{row.Name} 装进 {where}，{layerNote} · {result.Apply.Message}");
        }
        catch (Exception ex)
        {
            Warn(PadError.Describe(ex));
            _win.Toast($"{row.Name} 没装成", bad: true);
        }
        finally
        {
            if (busy is Button b) b.IsEnabled = true;
            Working(null);
        }
    }

    void Row_OpenDetail(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: MarketRow row }) return;
        OpenStoreDetail(row);
    }

    /// <summary>App Store 式插件详情窗口：商店卡片、排行榜行共用同一个入口。</summary>
    void OpenStoreDetail(MarketRow row)
    {
        var inst = TargetInstance.SelectedItem as Instance;
        var plugin = new PluginRow
        {
            Profile = inst?.Name ?? "",
            Spec = row.Name,
            Version = row.Version,
            Description = row.DescriptionText,
            Repository = row.Repository,
            Keywords = row.Keywords,
            IconPath = row.IconPath,
            Latest = "",
        };
        // 商店详情也用 App Store 式窗口；安装走现有 InstallFrom（默认装最新版本）
        var win = new PluginDetailWindow(
            plugin,
            install: () => _ = InstallFrom(row, "", null),
            versions: async () =>
            {
                var cat = await Market.FetchPackument(row.Name, CancellationToken.None);
                return cat.Versions.Count > 0 ? cat.Versions : cat.Files.Select(f => f.Version).ToList();
            },
            installVersion: version => _ = InstallFrom(row, version, null))
        {
            Owner = _win,
        };
        win.ShowDialog();
    }

    void ShowMarketList()
    {
        if (PaneDetail is null || PaneMarketList is null) return;
        PaneDetail.Visibility = Visibility.Collapsed;
        PaneMarketList.Visibility = Visibility.Visible;
    }

    async Task OpenDetail(MarketRow row)
    {
        _detail = row;
        _detailHome = row.Homepage;
        _chip = "全部";
        _catalog = SeedCatalog(row);
        PaneMarketList.Visibility = Visibility.Collapsed;
        PaneDetail.Visibility = Visibility.Visible;
        DetailHeadTitle.Text = row.Name;
        DetailTitle.Text = row.Name;
        DetailMono.Content = row.Monogram;
        DetailBody.Text = row.DescriptionText;
        DetailTags.Text = row.Keywords.Count > 0 ? string.Join(" · ", row.Keywords) : "";
        DetailBundle.Text = "正在读 npm packument…";
        DetailMeta.Text = row.Publisher.Length > 0 ? row.Publisher : "";
        DetailHomeBtn.IsEnabled = _detailHome.Length > 0;
        PaintChips();
        PaintFiles();
        try
        {
            var cat = await Market.FetchPackument(row.Name, CancellationToken.None);
            if (_detail?.Name != row.Name) return;
            _catalog = cat;
            DetailBody.Text = cat.Description.Length > 0 ? cat.Description : row.DescriptionText;
            DetailBundle.Text = cat.IsBundle
                ? "有 dsh.bundle，会进 dsh.profile.bundles"
                : "普通依赖，没进 dsh.profile.bundles";
            if (cat.Keywords.Count > 0) DetailTags.Text = string.Join(" · ", cat.Keywords);
            if (cat.Homepage.Length > 0) _detailHome = cat.Homepage;
            DetailHomeBtn.IsEnabled = _detailHome.Length > 0;
            var latest = cat.Files.FirstOrDefault(f => f.Latest);
            var bits = new List<string>();
            if (cat.Latest.Length > 0) bits.Add("latest " + cat.Latest);
            if (latest?.TimeText is { Length: > 0 } t) bits.Add(t);
            bits.Add("npm");
            DetailMeta.Text = string.Join(" · ", bits);
            PaintChips();
            PaintFiles();
        }
        catch (Exception ex)
        {
            DetailBundle.Text = PadError.Describe(ex);
        }
    }

    static PluginCatalog SeedCatalog(MarketRow row)
    {
        var cat = new PluginCatalog { Name = row.Name, Description = row.Description, Homepage = row.Homepage };
        if (row.Version.Length == 0) return cat;
        cat.Latest = row.Version;
        cat.Versions.Add(row.Version);
        cat.Files.Add(new PluginFile
        {
            Name = row.Name,
            Version = row.Version,
            Time = row.Date,
            Latest = true,
        });
        return cat;
    }

    void PaintChips()
    {
        if (DetailChips is null) return;
        DetailChips.Children.Clear();
        var labels = new List<string> { "全部" };
        if (_catalog is { Latest.Length: > 0 }) labels.Add("latest");
        if (_catalog is not null)
            foreach (var c in _catalog.Files.Select(f => f.Chip).Where(s => s.Length > 0).Distinct())
                labels.Add(c);
        if (!labels.Contains(_chip)) _chip = "全部";
        foreach (var label in labels)
        {
            var rb = new RadioButton
            {
                Style = (Style)FindResource("FilterTab"),
                Content = label,
                GroupName = "dlChip",
                MinWidth = 56,
                Margin = new Thickness(0, 0, 8, 8),
                IsChecked = label == _chip,
            };
            rb.Checked += Chip_Checked;
            DetailChips.Children.Add(rb);
        }
        if (DetailChipHost is not null) Fx.RefreshSlideSelect(DetailChipHost);
    }

    void Chip_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Content: string label } chip) return;
        if (_chip == label) return;
        _chip = label;
        Fx.Pop(chip);
        PaintFiles();
    }

    void PaintFiles()
    {
        if (DetailFileList is null) return;
        IEnumerable<PluginFile> files = _catalog?.Files ?? [];
        files = _chip switch
        {
            "全部" => files,
            "latest" => files.Where(f => f.Latest),
            _ => files.Where(f => f.Chip == _chip),
        };
        var list = files.ToList();
        DetailFileList.ItemsSource = list;
        if (DetailFileHead is not null)
            DetailFileHead.Text = _chip switch
            {
                "全部" => "可下版本",
                "latest" => "latest",
                _ => _chip,
            };
    }

    void DetailBack_Click(object sender, RoutedEventArgs e) => ShowMarketList();

    void DetailHome_Click(object sender, RoutedEventArgs e) => OpenUrl(_detailHome);

    void DetailNpm_Click(object sender, RoutedEventArgs e)
    {
        if (_detail is null) return;
        OpenUrl("https://www.npmjs.com/package/" + _detail.Name);
    }

    void DetailCopy_Click(object sender, RoutedEventArgs e)
    {
        if (_detail is null) return;
        Clipboard.SetText(_detail.Name);
        _win.Toast($"已复制 {_detail.Name}");
    }

    void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* ignore */ }
    }

    async void File_Install(object sender, RoutedEventArgs e)
    {
        if (_detail is null) return;
        if (sender is not FrameworkElement { Tag: PluginFile file }) return;
        await InstallFrom(_detail, file.Version, sender as Button);
    }

    void File_InstallRow(object sender, MouseButtonEventArgs e) => File_Install(sender, e);
}
