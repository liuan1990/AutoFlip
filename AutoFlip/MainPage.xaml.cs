using Microsoft.Maui.Storage;

#if ANDROID
using Android.Content;
using Android.App;
using Android.Views.Accessibility;
using Android.AccessibilityServices;
using Android.Provider;
#endif

namespace AutoFlip;

public partial class MainPage : ContentPage
{
    private const string RunningKey = "autoflip.running";
    private const string KeywordsKey = "autoflip.keywords";
    private const string DirectionKey = "autoflip.direction";
    private const string RegionKey = "autoflip.region";
    private const string IntervalKey = "autoflip.intervalHalfSeconds";
    private const string SwipeCooldownKey = "autoflip.swipeCooldownHalfSeconds";
    private const string OverlayVisibleKey = "autoflip.overlayVisible";
    private const string SwipeCountKey = "autoflip.swipeCount";
    private const string DiagnosticKey = "autoflip.diagnostic";
    private const string DefaultKeyword = "上滑继续观看";
    private string _direction = "Up";
    private string _region = "Middle";
    private IDispatcherTimer? _refreshTimer;

    public MainPage()
    {
        InitializeComponent();
        var savedKeywords = Preferences.Get(KeywordsKey, "");
        KeywordEntry.Text = string.IsNullOrWhiteSpace(savedKeywords) ? DefaultKeyword : savedKeywords;
        _direction = Preferences.Get(DirectionKey, "Up");
        _region = Preferences.Get(RegionKey, "Middle");
        IntervalSlider.Value = Preferences.Get(IntervalKey, 3) / 2d;
        SwipeCooldownSlider.Value = Preferences.Get(SwipeCooldownKey, 4) / 2d;
        OverlaySwitch.IsToggled = Preferences.Get(OverlayVisibleKey, true);
        UpdateIntervalLabel(IntervalSlider.Value);
        UpdateSwipeCooldownLabel(SwipeCooldownSlider.Value);
        UpdateDirectionButtons();
        UpdateRegionButtons();
        UpdateRunButton();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        RefreshServiceStatus();
        UpdateRunButton();
        _refreshTimer ??= Dispatcher.CreateTimer();
        _refreshTimer.Interval = TimeSpan.FromMilliseconds(700);
        _refreshTimer.Tick -= OnRefreshTick;
        _refreshTimer.Tick += OnRefreshTick;
        _refreshTimer.Start();
    }

    protected override void OnDisappearing()
    {
        _refreshTimer?.Stop();
        base.OnDisappearing();
    }

    private void OnDirectionClicked(object? sender, EventArgs e)
    {
        if (sender is Button { CommandParameter: string direction })
        {
            _direction = direction;
            Preferences.Set(DirectionKey, direction);
            UpdateDirectionButtons();
        }
    }

    private void OnIntervalChanged(object? sender, ValueChangedEventArgs e)
    {
        var interval = Math.Clamp(Math.Round(e.NewValue * 2, MidpointRounding.AwayFromZero) / 2, 0.5, 3);
        if (Math.Abs(IntervalSlider.Value - interval) > 0.001)
            IntervalSlider.Value = interval;
        UpdateIntervalLabel(interval);
        Preferences.Set(IntervalKey, (int)(interval * 2));
    }

    private void UpdateIntervalLabel(double interval) =>
        IntervalLabel.Text = $"{interval:0.#} 秒";

    private void OnSwipeCooldownChanged(object? sender, ValueChangedEventArgs e)
    {
        var cooldown = Math.Clamp(Math.Round(e.NewValue * 2, MidpointRounding.AwayFromZero) / 2, 0.5, 10);
        if (Math.Abs(SwipeCooldownSlider.Value - cooldown) > 0.001)
            SwipeCooldownSlider.Value = cooldown;
        UpdateSwipeCooldownLabel(cooldown);
        Preferences.Set(SwipeCooldownKey, (int)(cooldown * 2));
    }

    private void UpdateSwipeCooldownLabel(double cooldown) =>
        SwipeCooldownLabel.Text = $"{cooldown:0.#} 秒";

    private void OnOverlayToggled(object? sender, ToggledEventArgs e) =>
        Preferences.Set(OverlayVisibleKey, e.Value);

    private void OnRegionClicked(object? sender, EventArgs e)
    {
        if (sender is Button { CommandParameter: string region })
        {
            _region = region;
            Preferences.Set(RegionKey, region);
            UpdateRegionButtons();
        }
    }

    private void OnRefreshTick(object? sender, EventArgs e)
    {
        UpdateRunButton();
        LastMatchLabel.Text = Preferences.Get(DiagnosticKey, "等待识别");
        SwipeCountLabel.Text = Preferences.Get(SwipeCountKey, 0).ToString();
    }

    private async void OnServiceClicked(object? sender, EventArgs e)
    {
#if ANDROID
        try
        {
            Platform.CurrentActivity?.StartActivity(new Intent(Settings.ActionAccessibilitySettings));
        }
        catch (Exception)
        {
            await DisplayAlertAsync("无法打开设置", "请在系统设置的无障碍服务中启用“翻页识别”。", "知道了");
        }
#else
        await DisplayAlertAsync("Android 功能", "无障碍服务仅支持 Android。", "知道了");
#endif
    }

    private async void OnRunClicked(object? sender, EventArgs e)
    {
        var isRunning = Preferences.Get(RunningKey, false);
        if (!isRunning)
        {
            var keywords = NormalizeKeywords(KeywordEntry.Text);
            if (keywords.Length == 0)
            {
                await DisplayAlertAsync("需要关键词", "先输入至少一个要检测的关键词。", "知道了");
                return;
            }

#if ANDROID
            if (!IsAccessibilityServiceEnabled())
            {
                await DisplayAlertAsync("需要无障碍权限", "请先启用“翻页识别”无障碍服务。", "前往设置");
                OnServiceClicked(sender, e);
                return;
            }
#endif
            Preferences.Set(KeywordsKey, string.Join(",", keywords));
            Preferences.Set(RunningKey, true);
        }
        else
        {
            Preferences.Set(RunningKey, false);
        }

        UpdateRunButton();
        RefreshServiceStatus();
    }

    private void UpdateDirectionButtons()
    {
        SetButtonSelection(UpButton, _direction == "Up");
        SetButtonSelection(DownButton, _direction == "Down");
        SetButtonSelection(LeftButton, _direction == "Left");
        SetButtonSelection(RightButton, _direction == "Right");
    }

    private void UpdateRegionButtons()
    {
        SetButtonSelection(TopRegionButton, _region == "Top");
        SetButtonSelection(MiddleRegionButton, _region == "Middle");
        SetButtonSelection(BottomRegionButton, _region == "Bottom");
    }

    private static void SetButtonSelection(Button button, bool selected)
    {
        button.BackgroundColor = selected ? Color.FromArgb("#16866A") : Colors.White;
        button.TextColor = selected ? Colors.White : Color.FromArgb("#40514A");
        button.BorderColor = selected ? Color.FromArgb("#16866A") : Color.FromArgb("#D3DDD8");
        button.BorderWidth = 1;
    }

    private void UpdateRunButton()
    {
        var running = Preferences.Get(RunningKey, false);
        RunButton.Text = running ? "暂停识别" : "开始识别";
        RunButton.BackgroundColor = running ? Color.FromArgb("#B54D3D") : Color.FromArgb("#16866A");
        StatusBadge.Text = running ? "识别中" : "未启动";
        StatusBadge.TextColor = running ? Color.FromArgb("#176B56") : Color.FromArgb("#65736E");
        StatusBadge.BackgroundColor = running ? Color.FromArgb("#D9EEE6") : Color.FromArgb("#E4EAE7");
    }

    private void RefreshServiceStatus()
    {
#if ANDROID
        var enabled = IsAccessibilityServiceEnabled();
        ServiceDot.Color = enabled ? Color.FromArgb("#16866A") : Color.FromArgb("#E3A63B");
        ServiceStatus.Text = enabled
            ? "无障碍服务已启用，可在悬浮窗暂停或继续"
            : "需先启用系统无障碍服务，之后可在悬浮窗暂停或继续";
        ServiceButton.Text = enabled ? "管理无障碍服务" : "开启无障碍服务";
#endif
    }

#if ANDROID
    private static bool IsAccessibilityServiceEnabled()
    {
        var manager = (AccessibilityManager?)Platform.CurrentActivity?
            .GetSystemService(Context.AccessibilityService);
        var services = manager?.GetEnabledAccessibilityServiceList(FeedbackFlags.AllMask);
        return services?.Any(info => info.Id?.Contains("KeywordFlipService", StringComparison.Ordinal) == true) == true;
    }
#endif

    private static string[] NormalizeKeywords(string? input) =>
        (input ?? "")
            .Split(new[] { ',', '，', ';', '；', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(keyword => keyword.Trim())
            .Where(keyword => keyword.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
