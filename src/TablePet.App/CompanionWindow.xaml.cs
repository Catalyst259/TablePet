using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TablePet.App.Services;
using TablePet.Core;

namespace TablePet.App;

public partial class CompanionWindow : Window
{
    private readonly CompanionSession _session;
    private readonly SettingsService _settings;
    private readonly Dictionary<string, FrameworkElement> _memoryRows = new(StringComparer.Ordinal);
    private string _memorySignature = "";
    private bool _closed;
    private bool _markQueued;

    internal static string StageLabel(string stage) => stage switch
    { "familiar" => "熟悉", "close" => "亲近", "in_sync" => "默契", _ => "初见" };

    public CompanionWindow(CompanionSession session, SettingsService settings, ImageSource preview, bool upgraded = false)
    {
        InitializeComponent();
        _session = session; _settings = settings; Preview.Source = preview;
        MigrationNotice.Visibility = upgraded ? Visibility.Visible : Visibility.Collapsed;
        session.Changed += Refresh;
        settings.Changed += OnSettingsChanged;
        Loaded += (_, _) =>
        {
            var workArea = SystemParameters.WorkArea;
            MinWidth = Math.Min(420, workArea.Width);
            MinHeight = Math.Min(480, workArea.Height);
            Width = Math.Min(Width, workArea.Width); Height = Math.Min(Height, workArea.Height);
            Left = Math.Clamp(Left, workArea.Left, Math.Max(workArea.Left, workArea.Right - Width));
            Top = Math.Clamp(Top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - Height));
            QueueMarkVisible();
        };
        Closed += (_, _) =>
        {
            _closed = true; session.Changed -= Refresh; settings.Changed -= OnSettingsChanged;
        };
        Refresh();
    }

    private void OnSettingsChanged(PetSettings _) => Refresh();

    private void Refresh()
    {
        if (_closed) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(Refresh); return; }
        var state = _session.State;
        var unavailable = !_session.Store.CanWrite;
        StageName.Text = unavailable ? "记录待恢复" : StageLabel(state.Stage);
        StageDescription.Text = unavailable ? "小猫仍会陪你待着。" : state.Stage switch
        {
            "familiar" => "这个桌角，渐渐熟悉起来。", "close" => "和你待着，越来越自在。",
            "in_sync" => "熟悉的招呼，熟悉的日常。", _ => "一声招呼，一点点熟悉。"
        };
        DayCount.Text = unavailable ? "相伴记录暂不可用" : $"已相伴 {state.CompanionDays} 天";
        StageTrail.Visibility = unavailable ? Visibility.Collapsed : Visibility.Visible;
        StageProgress.Visibility = unavailable || state.Stage == "in_sync" ? Visibility.Collapsed : Visibility.Visible;
        StageProgress.Value = CompanionshipRules.ProgressToNextStage(state) * 100;
        ProgressDescription.Text = unavailable ? "原文件会保留，恢复后可以继续记录。"
            : state.Stage == "in_sync" ? "日常还在继续。" : "自然相伴，会慢慢熟悉。";
        ScoreDetail.Text = unavailable ? "读取记录后显示成长详情。" : $"熟悉度 {state.TotalPoints} · 已相伴 {state.CompanionDays} 天";
        var rank = CompanionshipRules.StageRank(state.Stage);
        if (!unavailable && rank < 3)
        {
            var next = CompanionshipRules.Stages[rank + 1];
            NextRequirement.Text = $"到“{StageLabel(next.Id)}”需要 {next.Points} 熟悉度和 {next.Days} 个相伴日。";
        }
        else NextRequirement.Text = "";
        AutomationProperties.SetHelpText(StageProgress, NextRequirement.Text);
        DisabledNotice.Visibility = !_settings.Current.CompanionshipEnabled ? Visibility.Visible : Visibility.Collapsed;
        RecordingDate.Text = state.RecordingStartedLocalDate is { } date ? $"记录开始于 {date}" : "";
        EmptyNotice.Visibility = state.Memories.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyNotice.Text = unavailable ? "相伴记录暂时读不到，你可以重试读取。" : "和小猫打个招呼，日常会慢慢记在这里。";
        RefreshStatus();

        var signature = string.Join("|", state.Memories.OrderByDescending(m => m.Sequence).Select(m => $"{m.Id}:{m.IsRead}"));
        if (signature == _memorySignature) return;
        _memorySignature = signature;
        MemoryItems.Children.Clear(); _memoryRows.Clear();
        foreach (var memory in state.Memories.OrderByDescending(m => m.Sequence))
        {
            var definition = CompanionContent.Default.FindMemory(memory.Id);
            if (definition is null) continue;
            var card = new Border
            {
                Background = (Brush)new BrushConverter().ConvertFromString(memory.IsRead ? "#FFFFFF" : "#F8ECEF")!,
                BorderBrush = (Brush)new BrushConverter().ConvertFromString("#E2D7DB")!, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12), Padding = new Thickness(14), Margin = new Thickness(0, 0, 0, 10)
            };
            var body = new StackPanel();
            body.Children.Add(new TextBlock { Text = memory.LocalDate, FontSize = 12, Foreground = (Brush)new BrushConverter().ConvertFromString("#705D68")! });
            body.Children.Add(new TextBlock
            {
                Text = definition.Title + (memory.IsRead ? "" : "  · 新"), FontSize = 16,
                FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 5, 0, 6), TextWrapping = TextWrapping.Wrap
            });
            body.Children.Add(new TextBlock { Text = definition.Description, TextWrapping = TextWrapping.Wrap, FontSize = 14 });
            var illustration = CompanionMemoryIllustrations.Default.Find(memory.Id);
            if (illustration is not null)
            {
                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var picture = new DecorativeMemoryImage
                {
                    Source = illustration, Width = 80, Height = 80, Stretch = Stretch.Uniform,
                    VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 14, 0),
                    IsHitTestVisible = false, Focusable = false
                };
                RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
                row.Children.Add(picture);
                Grid.SetColumn(body, 1);
                row.Children.Add(body);
                card.Child = row;
            }
            else card.Child = body;
            AutomationProperties.SetName(card, $"{memory.LocalDate}，{definition.Title}，{definition.Description}");
            MemoryItems.Children.Add(card); _memoryRows[memory.Id] = card;
        }
        QueueMarkVisible();
    }

    private void RefreshStatus()
    {
        var store = _session.Store;
        var message = store.LastError ?? store.RecoveryNotice;
        StatusBanner.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
        StatusText.Text = message ?? "";
        RetryButton.Content = store.CanWrite ? "重试保存" : "重试读取";
        RetryButton.Visibility = store.LastError is null ? Visibility.Collapsed : Visibility.Visible;
        RestartButton.Visibility = !store.CanWrite && !store.UnsupportedVersion ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Content_Scrolled(object sender, ScrollChangedEventArgs e) => QueueMarkVisible();

    private void QueueMarkVisible()
    {
        if (_markQueued || _closed) return;
        _markQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _markQueued = false;
            if (_closed || !IsVisible || WindowState == WindowState.Minimized) return;
            var visible = new List<string>();
            foreach (var memory in _session.State.Memories.Where(m => !m.IsRead))
            {
                if (!_memoryRows.TryGetValue(memory.Id, out var row) || row.ActualHeight <= 0) continue;
                var bounds = row.TransformToAncestor(ContentScroll).TransformBounds(new Rect(row.RenderSize));
                var intersection = Rect.Intersect(bounds, new Rect(0, 0, ContentScroll.ActualWidth, ContentScroll.ActualHeight));
                if (!intersection.IsEmpty && intersection.Height >= Math.Min(48, row.ActualHeight)) visible.Add(memory.Id);
            }
            if (visible.Count > 0) _session.MarkMemoriesRead(visible);
        }));
    }

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        RetryButton.IsEnabled = false;
        try { await _session.RetryAsync(); }
        catch (Exception error) { StatusText.Text = error.Message; }
        finally { RetryButton.IsEnabled = true; Refresh(); }
    }

    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = Path.GetDirectoryName(Path.GetFullPath(_session.Store.StatePath))!;
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception error) { StatusText.Text = "暂时无法打开文件夹：" + error.Message; }
    }

    private async void Restart_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "将建立新的成长记录。现有文件会先保留为备份，新记录中的熟悉度和相伴日从零开始。",
            "重新开始相伴记录", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        try { await _session.StartNewRecordAsync(); }
        catch (Exception error) { StatusText.Text = error.Message; }
        Refresh();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) { Close(); e.Handled = true; } }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private sealed class DecorativeMemoryImage : Image
    {
        // The card already exposes its date, title and description to assistive technology.
        protected override AutomationPeer? OnCreateAutomationPeer() => null;
    }
}
