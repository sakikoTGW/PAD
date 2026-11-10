using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Pad.Core;

namespace Pad.Views;

public sealed class AdoptHomeRow : INotifyPropertyChanged
{
    bool _pick = true;

    public AdoptHomeRow(PendingHome home)
    {
        Home = home;
        Title = PendingAdopt.SuggestName(home.Path);
        HomePath = home.Path;
        Profiles = home.Profiles;
        Sessions = home.Sessions;
    }

    public PendingHome Home { get; }
    public string Title { get; }
    public string HomePath { get; }
    public int Profiles { get; }
    public int Sessions { get; }

    public bool Pick
    {
        get => _pick;
        set { if (_pick == value) return; _pick = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public partial class AdoptHomesWindow : Window
{
    readonly ObservableCollection<AdoptHomeRow> _rows;

    public AdoptHomesWindow(IReadOnlyList<PendingHome> pending)
    {
        InitializeComponent();
        _rows = new ObservableCollection<AdoptHomeRow>(pending.Select(p => new AdoptHomeRow(p)));
        HomeList.ItemsSource = _rows;
    }

    /// <summary>Homes the person checked.</summary>
    public IReadOnlyList<string> Chosen { get; private set; } = [];

    /// <summary>Homes listed but not checked, or every home when they hit 跳过.</summary>
    public IReadOnlyList<string> Skipped { get; private set; } = [];

    /// <summary>True when 跳过 was the action; false when the window was closed.</summary>
    public bool RememberSkip { get; private set; }

    void Row_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject src && FindCheckBox(src) is not null) return;
        if (sender is FrameworkElement el && el.Tag is AdoptHomeRow row)
            row.Pick = !row.Pick;
    }

    static CheckBox? FindCheckBox(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is CheckBox box) return box;
            node = System.Windows.Media.VisualTreeHelper.GetParent(node);
        }
        return null;
    }

    void Adopt_Click(object sender, RoutedEventArgs e)
    {
        Chosen = _rows.Where(r => r.Pick).Select(r => r.HomePath).ToList();
        Skipped = _rows.Where(r => !r.Pick).Select(r => r.HomePath).ToList();
        RememberSkip = false;
        DialogResult = true;
    }

    void Skip_Click(object sender, RoutedEventArgs e)
    {
        Chosen = [];
        Skipped = _rows.Select(r => r.HomePath).ToList();
        RememberSkip = true;
        DialogResult = false;
    }

    /// <summary>离屏渲染成 PNG。--shot 不弹这窗，设计走查用这条，不抢前台。</summary>
    public void RenderTo(string path)
    {
        try
        {
            UpdateLayout();
            var dpi = VisualTreeHelper.GetDpi(this);
            var w = (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX);
            var h = (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY);
            var rtb = new RenderTargetBitmap(w, h, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY,
                PixelFormats.Pbgra32);
            rtb.Render(this);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(rtb));
            using var fs = File.Create(path);
            png.Save(fs);
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(path + ".txt", ex.ToString()); } catch { /* 截图失败不炸 */ }
        }
    }
}
