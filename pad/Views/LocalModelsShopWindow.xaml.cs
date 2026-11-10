using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Pad.Core;

namespace Pad.Views;

/// <summary>
/// 本地模型（双栏）：左边模型名单，右边详情 + 版本 + 拉取。
/// 名单来自 ollama.com，不预置任何模型名。
/// </summary>
public partial class LocalModelsShopWindow : Window
{
    CancellationTokenSource? _pull;
    CancellationTokenSource? _tags;
    IReadOnlyList<Ollama.LibraryModel> _library = [];
    IReadOnlyList<Ollama.Model> _installed = [];

    public LocalModelsShopWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadAsync();
        Closed += (_, _) =>
        {
            _pull?.Cancel();
            _tags?.Cancel();
        };
    }

    string Base => Ollama.Normalize(AppState.Current.Settings.Other.OllamaUrl);

    string CurrentTag =>
        (TagRow.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as string) ?? "latest";

    async Task LoadAsync()
    {
        BarStatus.Text = "正在检测…";
        var probe = await Ollama.ProbeAsync(Base, CancellationToken.None);
        _installed = probe.Models;
        Dot.Fill = new SolidColorBrush(probe.Up ? Color.FromRgb(0x4F, 0xBF, 0x9A) : Color.FromRgb(0xB0, 0x8A, 0x5A));
        BarStatus.Text = probe.Up
            ? $"运行中{(probe.Version.Length > 0 ? " · v" + probe.Version : "")} · 已装 {probe.Models.Count}"
            : "未运行";

        if (_library.Count == 0)
        {
            try { _library = await Ollama.LibraryAsync(CancellationToken.None); }
            catch { _library = []; }
        }
        IReadOnlyList<Ollama.LibraryModel> list = _library.Count > 0
            ? _library
            : probe.Models.Select(m => new Ollama.LibraryModel(m.Name, "已装")).ToList();

        // 名单没拉到时要说清楚「这是本机已装的」，别让人以为官方只有这几个。
        var offline = _library.Count == 0;
        ListCap.Text = offline ? "本机已装 · 名单没拉到" : "可选模型";
        ListCap.ToolTip = offline ? "连不上 ollama.com/library，只列本机已经装好的模型" : null;
        Sub.Text = offline ? "本机已装" : "来自 ollama.com/library";
        ModelList.ItemsSource = list;

        var preferred = (AppState.Current.Settings.Other.OllamaModel ?? "").Split(':')[0];
        var at = list.ToList().FindIndex(m => string.Equals(m.Name, preferred, StringComparison.OrdinalIgnoreCase));
        ModelList.SelectedIndex = at >= 0 ? at : (list.Count > 0 ? 0 : -1);
    }

    void ModelList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModelList.SelectedItem is not Ollama.LibraryModel item) return;
        ModelTitle.Text = item.Name;
        Desc.Text = item.Summary.Length > 0 ? item.Summary : "ollama.com 上没有写简介。";
        var installed = _installed.FirstOrDefault(m => m.Name.StartsWith(item.Name + ":", StringComparison.OrdinalIgnoreCase));
        Meta.Text = installed is null
            ? "未安装"
            : $"已装 {installed.Name} · {installed.SizeText}";
        _ = LoadTagsAsync(item.Name);
    }

    async Task LoadTagsAsync(string model)
    {
        _tags?.Cancel();
        _tags?.Dispose();
        _tags = new CancellationTokenSource();
        var ct = _tags.Token;

        FillTags(["latest"]);
        try
        {
            var tags = await Ollama.TagsAsync(model, ct);
            if (ct.IsCancellationRequested || tags.Count == 0) return;
            FillTags(tags);
        }
        catch { /* 拿不到版本就只留 latest */ }
    }

    void FillTags(IReadOnlyList<string> tags)
    {
        TagRow.Children.Clear();
        foreach (var tag in tags)
        {
            TagRow.Children.Add(new RadioButton
            {
                Style = (Style)FindResource("FilterTab"),
                Content = tag,
                Tag = tag,
                GroupName = "ollamaTag",
                Margin = new Thickness(0, 0, 6, 6),
                MinWidth = 46,
                IsChecked = tag == "latest",
            });
        }
    }

    async void Pull_Click(object sender, RoutedEventArgs e)
    {
        if (_pull is not null) return;
        if (ModelList.SelectedItem is not Ollama.LibraryModel item) return;
        var tag = CurrentTag;
        var spec = tag == "latest" ? item.Name : item.Name + ":" + tag;

        var other = AppState.Current.Settings.Other;
        other.OllamaModel = item.Name;
        try { AppState.Current.SaveSettings(); } catch { /* 存不下不影响拉取 */ }

        _pull = new CancellationTokenSource();
        PullBtn.IsEnabled = false;
        CancelBtn.Visibility = Visibility.Visible;
        Bar.Visibility = Visibility.Visible;
        Bar.Value = 0;
        PullText.Text = "准备中…";
        try
        {
            var progress = new Progress<(string Status, long Done, long Total)>(p =>
            {
                PullText.Text = Ollama.ProgressText(p.Status, p.Done, p.Total);
                if (p.Total > 0) Bar.Value = Math.Clamp(p.Done * 100.0 / p.Total, 0, 100);
            });
            await Ollama.PullAsync(Base, spec, progress, _pull.Token);
            PullText.Text = $"{spec} 已就绪";
            Bar.Value = 100;
            await LoadAsync();
            (Owner as MainWindow)?.Toast($"本地模型 {spec} 已就绪");
        }
        catch (OperationCanceledException)
        {
            PullText.Text = "已取消";
        }
        catch (PadError ex)
        {
            PullText.Text = PadError.Describe(ex);
        }
        catch (Exception ex)
        {
            PullText.Text = "拉取失败：" + ex.Message;
        }
        finally
        {
            _pull.Dispose();
            _pull = null;
            PullBtn.IsEnabled = true;
            CancelBtn.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>取消拉取：已下载的层会留在 Ollama 里，下次接着下。</summary>
    void Cancel_Click(object sender, RoutedEventArgs e) => _pull?.Cancel();

    void Bar_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
