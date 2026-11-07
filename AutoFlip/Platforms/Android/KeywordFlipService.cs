using Android.AccessibilityServices;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Views.Accessibility;
using Android.Widget;
using Android.Graphics.Drawables;
using Android.Util;
using Android.Gms.Tasks;
using Xamarin.Google.MLKit.Vision.Common;
using Xamarin.Google.MLKit.Vision.Text;
using Xamarin.Google.MLKit.Vision.Text.Chinese;
using Microsoft.Maui.Storage;
using Java.Util.Concurrent;
using Java.Interop;

namespace AutoFlip;

[Service(
    Name = "cn.codex.autoflip.KeywordFlipService",
    Label = "翻页识别",
    Permission = "android.permission.BIND_ACCESSIBILITY_SERVICE",
    ForegroundServiceType = ForegroundService.TypeSpecialUse,
    Exported = true)]
[IntentFilter(new[] { "android.accessibilityservice.AccessibilityService" })]
[MetaData("android.accessibilityservice", Resource = "@xml/keyword_flip_service")]
[MetaData("android.app.PROPERTY_SPECIAL_USE_FGS_SUBTYPE", Value = "用户主动开启的屏幕关键词识别与自动翻页")]
public sealed class KeywordFlipService : AccessibilityService
{
    private const int NotificationId = 7301;
    private const string NotificationChannelId = "keyword_flip_active";
    private const string RunningKey = "autoflip.running";
    private const string KeywordsKey = "autoflip.keywords";
    private const string DirectionKey = "autoflip.direction";
    private const string RegionKey = "autoflip.region";
    private const string IntervalKey = "autoflip.intervalHalfSeconds";
    private const string SwipeCooldownKey = "autoflip.swipeCooldownHalfSeconds";
    private const string OverlayVisibleKey = "autoflip.overlayVisible";
    private const string SwipeCountKey = "autoflip.swipeCount";
    private const string SessionSwipeBaselineKey = "autoflip.sessionSwipeBaseline";
    private readonly Handler _handler = new(Looper.MainLooper!);
    private readonly IExecutor _screenshotExecutor = Executors.NewSingleThreadExecutor()!;
    private ITextRecognizer? _recognizer;
    private IWindowManager? _windowManager;
    private LinearLayout? _overlay;
    private TextView? _overlayLabel;
    private WindowManagerLayoutParams? _overlayParams;
    private bool _overlayCollapsed;
    private bool _overlayOnRight;
    private bool _captureInProgress;
    private bool _foregroundActive;
    private long _lastCaptureAt;
    private long _lastSwipeAt;
    private string _lastMatch = "";

    private readonly System.Action _poll;

    public KeywordFlipService()
    {
        _poll = Poll;
    }

    protected override void OnServiceConnected()
    {
        base.OnServiceConnected();
        _recognizer = TextRecognition.GetClient(new ChineseTextRecognizerOptions.Builder().Build());
        _windowManager = GetSystemService(WindowService)?.JavaCast<IWindowManager>();
        CreateOverlay();
        _handler.Post(_poll);
    }

    public override void OnAccessibilityEvent(AccessibilityEvent? e)
    {
        // Screen capture is timer-driven so the service works even when apps emit few events.
    }

    public override void OnInterrupt()
    {
        SetRunning(false);
        UpdateOverlay();
    }

    public override void OnDestroy()
    {
        _handler.RemoveCallbacks(_poll);
        RemoveOverlay();
        _recognizer?.Close();
        base.OnDestroy();
    }

    private void Poll()
    {
        var running = IsRunning();
        UpdateForegroundNotification(running);
        if (Preferences.Get(OverlayVisibleKey, true))
        {
            CreateOverlay();
            UpdateOverlay();
        }
        else
        {
            RemoveOverlay();
        }

        if (running && Preferences.Get(OverlayVisibleKey, true) && !_captureInProgress)
        {
            var intervalMs = Math.Clamp(Preferences.Get(IntervalKey, 3), 1, 6) * 500L;
            var now = SystemClock.ElapsedRealtime();
            if (now - _lastCaptureAt >= intervalMs)
            {
                _lastCaptureAt = now;
                CaptureScreen();
            }
        }

        _handler.PostDelayed(_poll, 400);
    }

    private void UpdateForegroundNotification(bool running)
    {
        if (running == _foregroundActive)
            return;

        if (!running)
        {
            StopForeground(StopForegroundFlags.Remove);
            _foregroundActive = false;
            return;
        }

        try
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            {
                var manager = GetSystemService(NotificationService) as NotificationManager;
                manager?.CreateNotificationChannel(new NotificationChannel(
                    NotificationChannelId,
                    "关键词翻页",
                    NotificationImportance.Low)
                {
                    Description = "关键词识别运行期间显示"
                });
            }

            var openAppIntent = new Intent(this, typeof(MainActivity));
            openAppIntent.SetFlags(ActivityFlags.ClearTop | ActivityFlags.SingleTop);
            var contentIntent = PendingIntent.GetActivity(
                this,
                0,
                openAppIntent,
                PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
            var notification = new Notification.Builder(this, NotificationChannelId)
                .SetSmallIcon(Android.Resource.Drawable.IcMediaPlay)
                .SetContentTitle("翻页识别正在运行")
                .SetContentText("屏幕关键词识别已开启，点按返回控制页")
                .SetContentIntent(contentIntent)
                .SetCategory(Notification.CategoryService)
                .SetOngoing(true)
                .SetOnlyAlertOnce(true)
                .SetShowWhen(false)
                .Build();

            if (Build.VERSION.SdkInt >= BuildVersionCodes.UpsideDownCake)
                StartForeground(NotificationId, notification, ForegroundService.TypeSpecialUse);
            else
                StartForeground(NotificationId, notification);

            _foregroundActive = true;
        }
        catch (Exception exception)
        {
            Log.Warn("KeywordFlipService", $"Foreground notification failed: {exception.Message}");
        }
    }

    private void CaptureScreen()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.R || _recognizer is null)
            return;

        _captureInProgress = true;
        try
        {
            TakeScreenshot(0, _screenshotExecutor, new ScreenCaptureCallback(this));
        }
        catch (Exception)
        {
            _captureInProgress = false;
        }
    }

    private void OnScreenshot(AccessibilityService.ScreenshotResult result)
    {
        Bitmap? sourceBitmap = null;
        Bitmap? recognitionBitmap = null;
        try
        {
            using (var hardwareBitmap = Bitmap.WrapHardwareBuffer(result.HardwareBuffer, result.ColorSpace))
            {
                if (hardwareBitmap is not null)
                    sourceBitmap = hardwareBitmap.Copy(Bitmap.Config.Argb8888!, false);
            }
            result.HardwareBuffer?.Close();

            if (sourceBitmap is null)
            {
                _captureInProgress = false;
                return;
            }

            var region = Preferences.Get(RegionKey, "Middle");
            var thirdHeight = Math.Max(1, sourceBitmap.Height / 3);
            var top = region switch
            {
                "Top" => 0,
                "Bottom" => sourceBitmap.Height - thirdHeight,
                _ => (sourceBitmap.Height - thirdHeight) / 2
            };
            recognitionBitmap = Bitmap.CreateBitmap(sourceBitmap, 0, top, sourceBitmap.Width, thirdHeight);
            sourceBitmap.Recycle();
            sourceBitmap = null;

            var image = InputImage.FromBitmap(recognitionBitmap, 0);
            _recognizer!.Process(image)
                .AddOnSuccessListener(new OcrSuccessListener(this, recognitionBitmap))
                .AddOnFailureListener(new OcrFailureListener(this, recognitionBitmap));
            recognitionBitmap = null;
        }
        catch (Exception exception)
        {
            Log.Warn("KeywordFlipService", $"Screenshot/OCR preparation failed: {exception}");
            sourceBitmap?.Recycle();
            recognitionBitmap?.Recycle();
            result.HardwareBuffer?.Close();
            _captureInProgress = false;
        }
    }

    private void OnOcrResult(Java.Lang.Object value, Bitmap bitmap)
    {
        try
        {
            if (value is not Text recognized)
                return;

            var text = recognized.GetText() ?? "";
            var match = GetKeywords()
                .FirstOrDefault(keyword => text.Contains(keyword, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                Log.Info("KeywordFlipService", $"Keyword matched while collapsed={_overlayCollapsed}");

            if (match is null)
            {
                _lastMatch = "";
                return;
            }

            if (!IsRunning())
                return;

            _lastMatch = match;
            Preferences.Set("autoflip.lastMatch", $"{match} · {DateTime.Now:HH:mm:ss}");
            var now = SystemClock.ElapsedRealtime();
            var cooldownMs = Math.Clamp(Preferences.Get(SwipeCooldownKey, 4), 1, 20) * 500L;
            if (now - _lastSwipeAt >= cooldownMs && PerformPageSwipe())
            {
                _lastSwipeAt = now;
            }
        }
        finally
        {
            bitmap.Recycle();
            _captureInProgress = false;
            _handler.Post(UpdateOverlay);
        }
    }

    private void OnOcrFailure(Bitmap bitmap)
    {
        bitmap.Recycle();
        _captureInProgress = false;
    }

    private bool PerformPageSwipe()
    {
        var metrics = Resources?.DisplayMetrics;
        if (metrics is null)
            return false;

        var width = metrics.WidthPixels;
        var height = metrics.HeightPixels;
        var direction = Preferences.Get(DirectionKey, "Up");
        var path = new Android.Graphics.Path();

        switch (direction)
        {
            case "Down":
                path.MoveTo(width * 0.5f, height * 0.32f);
                path.LineTo(width * 0.5f, height * 0.78f);
                break;
            case "Left":
                path.MoveTo(width * 0.82f, height * 0.55f);
                path.LineTo(width * 0.18f, height * 0.55f);
                break;
            case "Right":
                path.MoveTo(width * 0.18f, height * 0.55f);
                path.LineTo(width * 0.82f, height * 0.55f);
                break;
            default:
                path.MoveTo(width * 0.5f, height * 0.78f);
                path.LineTo(width * 0.5f, height * 0.32f);
                break;
        }

        var gesture = new GestureDescription.Builder()
            .AddStroke(new GestureDescription.StrokeDescription(path, 0, 320))
            .Build();
        if (gesture is null)
            return false;
        if (DispatchGesture(gesture, null, null))
        {
            Preferences.Set(SwipeCountKey, Preferences.Get(SwipeCountKey, 0) + 1);
            return true;
        }

        return false;
    }

    private void CreateOverlay()
    {
        if (_windowManager is null || _overlay is not null || !Preferences.Get(OverlayVisibleKey, true))
            return;

        var density = Resources?.DisplayMetrics?.Density ?? 1f;
        _overlayLabel = new TextView(this)
        {
            Text = "识别已暂停  ·  点按切换",
            TextSize = 13,
            Gravity = GravityFlags.Center
        };
        _overlayLabel.SetTextColor(Android.Graphics.Color.White);
        _overlayLabel.SetPadding((int)(15 * density), (int)(11 * density), (int)(15 * density), (int)(11 * density));

        _overlay = new LinearLayout(this)
        {
            Orientation = Orientation.Horizontal,
        };
        _overlay.SetGravity(GravityFlags.Center);
        _overlay.Background = new GradientDrawable();
        ((GradientDrawable)_overlay.Background).SetColor(Android.Graphics.Color.Argb(145, 25, 42, 36));
        ((GradientDrawable)_overlay.Background).SetCornerRadius(24 * density);
        _overlayLabel.SetIncludeFontPadding(false);
        _overlay.AddView(_overlayLabel);
        _overlay.SetOnTouchListener(new OverlayTouchListener(this));

        _overlayParams = new WindowManagerLayoutParams(
            (int)(220 * density),
            WindowManagerLayoutParams.WrapContent,
            WindowManagerTypes.AccessibilityOverlay,
            WindowManagerFlags.NotFocusable | WindowManagerFlags.LayoutInScreen,
            Format.Translucent)
        {
            Gravity = GravityFlags.Top | GravityFlags.Left,
            X = (int)(12 * density),
            Y = (int)(260 * density)
        };

        try
        {
            _windowManager.AddView(_overlay, _overlayParams);
        }
        catch (Exception)
        {
            _overlay = null;
        }
    }

    private void RemoveOverlay()
    {
        if (_overlay is null || _windowManager is null)
            return;
        try
        {
            _windowManager.RemoveView(_overlay);
        }
        catch (Exception)
        {
        }
        _overlay = null;
        _overlayLabel = null;
        _overlayParams = null;
        _overlayCollapsed = false;
    }

    private void UpdateOverlay()
    {
        if (_overlayLabel is null)
            return;
        if (_overlayCollapsed)
        {
            var sessionCount = Math.Max(
                0,
                Preferences.Get(SwipeCountKey, 0) - Preferences.Get(SessionSwipeBaselineKey, 0));
            _overlayLabel.Text = sessionCount.ToString();
            return;
        }
        _overlayLabel.SetTextColor(Android.Graphics.Color.White);
        var running = IsRunning();
        _overlayLabel.Text = running
            ? $"识别中 · {_lastMatch}  ·  {Preferences.Get(SwipeCountKey, 0)}次".TrimEnd(' ')
            : "识别已暂停  ·  点按切换";
        _overlayLabel.Alpha = running ? 1f : 0.75f;
    }

    private void SetOverlayCollapsed(bool collapsed)
    {
        if (_overlay is null || _overlayLabel is null || _overlayParams is null || _windowManager is null)
            return;

        var density = Resources?.DisplayMetrics?.Density ?? 1f;
        const int collapsedDiameterDp = 36;
        var width = (int)((collapsed ? collapsedDiameterDp : 220) * density);
        _overlayCollapsed = collapsed;
        _overlayParams.Width = width;
        _overlayParams.Height = collapsed ? width : WindowManagerLayoutParams.WrapContent;
        _overlayLabel.Text = collapsed
            ? Math.Max(0, Preferences.Get(SwipeCountKey, 0) - Preferences.Get(SessionSwipeBaselineKey, 0)).ToString()
            : "";
        _overlayLabel.TextSize = collapsed ? 10 : 13;
        _overlayLabel.SetTextColor(Android.Graphics.Color.White);
        if (_overlay.Background is GradientDrawable background)
            background.SetColor(Android.Graphics.Color.Argb(145, 25, 42, 36));
        _overlayLabel.Gravity = GravityFlags.Center;
        _overlayLabel.LayoutParameters = new LinearLayout.LayoutParams(
            collapsed ? width : WindowManagerLayoutParams.WrapContent,
            collapsed ? width : WindowManagerLayoutParams.WrapContent);
        _overlayLabel.SetPadding(
            collapsed ? 0 : (int)(15 * density),
            collapsed ? 0 : (int)(11 * density),
            collapsed ? 0 : (int)(15 * density),
            collapsed ? 0 : (int)(11 * density));
        var screenWidth = Resources?.DisplayMetrics?.WidthPixels ?? width;
        _overlayParams.X = _overlayOnRight ? Math.Max(0, screenWidth - width) : 0;
        try
        {
            _windowManager.UpdateViewLayout(_overlay, _overlayParams);
        }
        catch (Exception)
        {
        }
    }

    private void ToggleRunning()
    {
        SetRunning(!IsRunning());
        _lastSwipeAt = 0;
        UpdateOverlay();
    }

    private static bool IsRunning() => Preferences.Get(RunningKey, false);
    private static void SetRunning(bool running) => Preferences.Set(RunningKey, running);

    private static string[] GetKeywords() =>
        Preferences.Get(KeywordsKey, "上滑继续观看")
            .Split(new[] { ',', '，', ';', '；', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim())
            .Where(value => value.Length > 0)
            .ToArray();

    private sealed class ScreenCaptureCallback(KeywordFlipService owner) : Java.Lang.Object, AccessibilityService.ITakeScreenshotCallback
    {
        public void OnSuccess(AccessibilityService.ScreenshotResult? screenshot)
        {
            if (screenshot is null)
            {
                owner._captureInProgress = false;
                return;
            }
            owner.OnScreenshot(screenshot);
        }

        public void OnFailure(int errorCode) => owner._captureInProgress = false;
    }

    private sealed class OcrSuccessListener(KeywordFlipService owner, Bitmap bitmap) : Java.Lang.Object, IOnSuccessListener
    {
        public void OnSuccess(Java.Lang.Object? result)
        {
            if (result is not null)
                owner.OnOcrResult(result, bitmap);
            else
                owner.OnOcrFailure(bitmap);
        }
    }

    private sealed class OcrFailureListener(KeywordFlipService owner, Bitmap bitmap) : Java.Lang.Object, IOnFailureListener
    {
        public void OnFailure(Java.Lang.Exception? error) => owner.OnOcrFailure(bitmap);
    }

    private sealed class OverlayTouchListener(KeywordFlipService owner) : Java.Lang.Object, Android.Views.View.IOnTouchListener
    {
        private float _downX;
        private float _downY;
        private int _startX;
        private int _startY;
        private bool _moved;

        public bool OnTouch(Android.Views.View? view, MotionEvent? e)
        {
            if (e is null || owner._overlayParams is null || owner._overlay is null || owner._windowManager is null)
                return false;

            switch (e.Action)
            {
                case MotionEventActions.Down:
                    _downX = e.RawX;
                    _downY = e.RawY;
                    _startX = owner._overlayParams.X;
                    _startY = owner._overlayParams.Y;
                    _moved = false;
                    return true;
                case MotionEventActions.Move:
                    var dx = e.RawX - _downX;
                    var dy = e.RawY - _downY;
                    if (Math.Abs(dx) + Math.Abs(dy) > 12)
                        _moved = true;
                    if (_moved)
                    {
                        owner._overlayParams.X = _startX + (int)dx;
                        owner._overlayParams.Y = _startY + (int)dy;
                        owner._windowManager.UpdateViewLayout(owner._overlay, owner._overlayParams);
                    }
                    return true;
                case MotionEventActions.Up:
                    if (_moved)
                    {
                        var screenWidth = owner.Resources?.DisplayMetrics?.WidthPixels ?? 0;
                        owner._overlayOnRight = owner._overlayParams.X + owner._overlay.Width / 2 >= screenWidth / 2;
                        owner._overlayParams.X = owner._overlayOnRight
                            ? Math.Max(0, screenWidth - owner._overlay.Width)
                            : 0;
                        owner._windowManager.UpdateViewLayout(owner._overlay, owner._overlayParams);
                        owner.SetOverlayCollapsed(true);
                    }
                    else if (owner._overlayCollapsed)
                        owner.SetOverlayCollapsed(false);
                    else
                        owner.ToggleRunning();
                    return true;
                default:
                    return true;
            }
        }
    }
}
