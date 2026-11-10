using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Pad.Core;

namespace Pad.Views;

/// <summary>
/// 托盘 / 全局快捷键唤出的「最近会话」面板：跨实例列会话，回车直接在那个实例里打开。
/// 打开哪种 bundle 由 JumpPlanner 决定，这里不猜。
/// </summary>
public partial class RecentWindow : Window
{
    readonly IReadOnlyList<RecentSession> _all;
    bool _armed;
    bool _closing;

    /// <summary>选中某条会话（窗口会先关掉）。</summary>
    public event Action<RecentSession>? Chosen;

    public RecentWindow(IReadOnlyList<RecentSession> rows)
    {
        InitializeComponent();
        _all = rows;
        Apply(rows);
        Activated += (_, _) => _armed = true;
        // ContentRendered 在第一次布局之后；Loaded 里 ActualWidth 有时还是 0，
        // 那会让窗口只往右挪半屏（贴到屏幕外）。
        ContentRendered += (_, _) => CenterNearTop();
        Loaded += (_, _) =>
        {
            CenterNearTop();
            Query.Focus();
            Keyboard.Focus(Query);
            if (List.Items.Count > 0) List.SelectedIndex = 0;
        };
    }

    void CenterNearTop()
    {
        // 水平交给 WPF 的 CenterScreen（它会自己处理 DPI 缩放），这里只把它往下挪一点。
        try
        {
            var work = SystemParameters.WorkArea;
            Top = work.Top + work.Height * 0.16;
        }
        catch { /* 定位失败就用默认位置 */ }
    }

    void Apply(IReadOnlyList<RecentSession> rows)
    {
        List.ItemsSource = rows;
        if (List.Items.Count > 0) List.SelectedIndex = 0;
        Hint.Text = rows.Count == 0
            ? "还没有可打开的会话：先在一个实例里聊两句"
            : "↑↓ 选择 · Enter 打开 · Esc 关闭";
    }

    void Query_TextChanged(object sender, TextChangedEventArgs e)
    {
        var q = (Query.Text ?? "").Trim();
        if (q.Length == 0) { Apply(_all); return; }
        var hit = _all.Where(r =>
            r.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
            || r.InstanceName.Contains(q, StringComparison.OrdinalIgnoreCase)
            || r.Detail.Contains(q, StringComparison.OrdinalIgnoreCase)
            || r.SessionId.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        Apply(hit);
    }

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                Close();
                break;
            case Key.Enter:
                e.Handled = true;
                Take();
                break;
            case Key.Down:
                e.Handled = true;
                Move(1);
                break;
            case Key.Up:
                e.Handled = true;
                Move(-1);
                break;
        }
    }

    void Move(int delta)
    {
        if (List.Items.Count == 0) return;
        var at = Math.Clamp(List.SelectedIndex + delta, 0, List.Items.Count - 1);
        List.SelectedIndex = at;
        List.ScrollIntoView(List.Items[at]);
    }

    void List_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Take();

    void Take()
    {
        if (List.SelectedItem is not RecentSession row) return;
        Chosen?.Invoke(row);
        Close();
    }

    void Window_Deactivated(object sender, EventArgs e)
    {
        // 窗口被别的程序抢走焦点就收起；但 Show 之后还没激活过一次、或者已经开始关了，就别再关一次
        // （关窗途中再 Close 会抛 InvalidOperationException）。
        if (_armed && !_closing) Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _closing = true;
        base.OnClosing(e);
    }

}
