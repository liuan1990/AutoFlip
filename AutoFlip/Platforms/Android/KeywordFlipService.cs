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
using System.Threading;

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
    private const string DiagnosticKey = "autoflip.diagnostic";
    private const long CaptureTimeoutMs = 10000;
    private const long GestureTimeoutMs = 3000;
    private const long FailedGestureRetryMs = 1000;
    private readonly Handler _handler = new(Looper.MainLooper!);
    private readonly IExecutor _screenshotExecutor = Executors.NewSingleThreadExecutor()!;
    private ITextRecognizer? _recognizer;
    private IWindowManager? _windowManager;
    private LinearLayout? _overlay;
    private TextView? _overlayLabel;
    private WindowManagerLayoutParams? _overlayParams;
    private bool _overlayCollapsed;
    private bool _overlayOnRight;
    private bool _foregroundActive;
    private long _lastCaptureAt;
    private long _captureStartedAt;
    private long _captureSequence;
    private long _activeCaptureId;
    private long _nextSwipeAllowedAt;
    private long _gestureStartedAt;
    private long _gestureSequence;
    private long _activeGestureId;
    private bool _gestureInProgress;
    private bool _keywordActive;
    private int _consecutiveKeywordMisses;
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

        var now = SystemClock.ElapsedRealtime();
        var activeCaptureId = Interlocked.Read(ref _activeCaptureId);
        if (activeCaptureId != 0 &&
            now - Interlocked.Read(ref _captureStartedAt) >= CaptureTimeoutMs)
        {
            CompleteCapture(activeCaptureId);
            Log.Warn("KeywordFlipService", $"Capture {activeCaptureId} timed out; polling resumed");
            SetDiagnostic("识别超时，已自动恢复");
        }

        if (_gestureInProgress && now - _gestureStartedAt >= GestureTimeoutMs)
        {
            var expiredGestureId = _activeGestureId;
            _activeGestureId = 0;
            _gestureInProgress = false;
            _nextSwipeAllowedAt = now + FailedGestureRetryMs;
            Log.Warn("KeywordFlipService", $"Gesture {expiredGestureId} timed out");
            SetDiagnostic("手势结果超时，1秒后重试");
        }

        if (running && Preferences.Get(OverlayVisibleKey, true) &&
            Interlocked.Read(ref _activeCaptureId) == 0)
        {
            var intervalMs = Math.Clamp(Preferences.Get(IntervalKey, 3), 1, 6) * 500L;
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

        var captureId = Interlocked.Increment(ref _captureSequence);
        if (Interlocked.CompareExchange(ref _activeCaptureId, captureId, 0) != 0)
            return;
        Interlocked.Exchange(ref _captureStartedAt, SystemClock.ElapsedRealtime());
        try
        {
            TakeScreenshot(0, _screenshotExecutor, new ScreenCaptureCallback(this, captureId));
        }
        catch (Exception exception)
        {
            CompleteCapture(captureId);
            Log.Warn("KeywordFlipService", $"Screenshot request failed: {exception}");
            SetDiagnostic("屏幕截图失败，正在重试");
        }
    }

    private void OnScreenshot(AccessibilityService.ScreenshotResult result, long captureId)
    {
        if (!IsCurrentCapture(captureId))
        {
            result.HardwareBuffer?.Close();
            return;
        }

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
                CompleteCapture(captureId);
                SetDiagnostic("截图为空，正在重试");
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
            MaskOverlayFromRecognition(recognitionBitmap, top);
            sourceBitmap.Recycle();
            sourceBitmap = null;

            var image = InputImage.FromBitmap(recognitionBitmap, 0);
            _recognizer!.Process(image)
                .AddOnSuccessListener(new OcrSuccessListener(this, recognitionBitmap, captureId))
                .AddOnFailureListener(new OcrFailureListener(this, recognitionBitmap, captureId));
            recognitionBitmap = null;
        }
        catch (Exception exception)
        {
            Log.Warn("KeywordFlipService", $"Screenshot/OCR preparation failed: {exception}");
            sourceBitmap?.Recycle();
            recognitionBitmap?.Recycle();
            result.HardwareBuffer?.Close();
            CompleteCapture(captureId);
            SetDiagnostic("截图或文字识别启动失败，正在重试");
        }
    }

    private void OnOcrResult(Java.Lang.Object value, Bitmap bitmap, long captureId)
    {
        try
        {
            if (!IsCurrentCapture(captureId))
                return;

            if (value is not Text recognized)
            {
                SetDiagnostic("文字识别结果无效，正在重试");
                return;
            }

            var text = recognized.GetText() ?? "";
            var normalizedText = NormalizeForMatch(text);
            var match = GetKeywords()
                .FirstOrDefault(keyword => normalizedText.Contains(NormalizeForMatch(keyword), StringComparison.OrdinalIgnoreCase));

            if (match is null)
            {
                OnKeywordMiss();
                return;
            }

            if (!IsRunning())
                return;

            if (!_keywordActive)
            {
                Log.Info("KeywordFlipService", $"Keyword detected: {match}");
            }
            _keywordActive = true;
            _consecutiveKeywordMisses = 0;
            _lastMatch = match;
            Preferences.Set("autoflip.lastMatch", $"{match} · {DateTime.Now:HH:mm:ss}");
            TrySwipeForPersistentKeyword(SystemClock.ElapsedRealtime());
        }
        finally
        {
            bitmap.Recycle();
            CompleteCapture(captureId);
            _handler.Post(UpdateOverlay);
        }
    }

    private void OnOcrFailure(Bitmap bitmap, long captureId, Java.Lang.Exception? error)
    {
        bitmap.Recycle();
        if (!IsCurrentCapture(captureId))
            return;

        CompleteCapture(captureId);
        Log.Warn("KeywordFlipService", $"Text recognition failed: {error?.Message ?? "unknown error"}");
        SetDiagnostic("文字识别失败，正在重试");
    }

    private void OnKeywordMiss()
    {
        _consecutiveKeywordMisses++;
        if (_consecutiveKeywordMisses < 2)
        {
            SetDiagnostic("本帧未识别到关键词，等待下一帧确认");
            return;
        }

        if (_keywordActive)
        {
            _keywordActive = false;
            Log.Info("KeywordFlipService", "Keyword absent in two consecutive OCR frames; treating target as cleared");
            SetDiagnostic("关键词连续两帧消失，页面可能已响应");
        }
        else
        {
            SetDiagnostic("未识别到目标关键词");
        }

        _lastMatch = "";
    }

    private void TrySwipeForPersistentKeyword(long now)
    {
        if (_gestureInProgress)
        {
            SetDiagnostic("手势执行中，等待系统结果");
            return;
        }

        if (now < _nextSwipeAllowedAt)
        {
            var remainingMs = _nextSwipeAllowedAt - now;
            SetDiagnostic($"关键词仍在，冷却中 {Math.Ceiling(remainingMs / 1000d):0} 秒");
            return;
        }

        var metrics = Resources?.DisplayMetrics;
        if (metrics is null)
        {
            _nextSwipeAllowedAt = now + FailedGestureRetryMs;
            SetDiagnostic("无法读取屏幕尺寸，稍后重试");
            return;
        }

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
        {
            _nextSwipeAllowedAt = now + FailedGestureRetryMs;
            SetDiagnostic("无法生成滑动手势，1秒后重试");
            return;
        }

        var gestureId = ++_gestureSequence;
        _activeGestureId = gestureId;
        _gestureInProgress = true;
        _gestureStartedAt = now;
        var cooldownMs = Math.Clamp(Preferences.Get(SwipeCooldownKey, 4), 1, 20) * 500L;
        try
        {
            var accepted = DispatchGesture(gesture, new SwipeResultCallback(this, gestureId), _handler);
            if (accepted)
            {
                _nextSwipeAllowedAt = now + cooldownMs;
                Log.Info("KeywordFlipService", $"Gesture {gestureId} accepted; retry allowed after {cooldownMs}ms if keyword persists");
                SetDiagnostic("滑动请求已提交，等待系统确认");
            }
            else
            {
                _gestureInProgress = false;
                _activeGestureId = 0;
                _nextSwipeAllowedAt = now + FailedGestureRetryMs;
                Log.Warn("KeywordFlipService", $"Gesture {gestureId} was rejected");
                SetDiagnostic("系统未接受滑动，1秒后重试");
            }
        }
        catch (Exception exception)
        {
            _gestureInProgress = false;
            _activeGestureId = 0;
            _nextSwipeAllowedAt = now + FailedGestureRetryMs;
            Log.Warn("KeywordFlipService", $"Gesture {gestureId} dispatch threw: {exception}");
            SetDiagnostic("滑动派发异常，1秒后重试");
        }
    }

    private void OnGestureResult(long gestureId, bool completed)
    {
        if (_activeGestureId != gestureId)
            return;

        _activeGestureId = 0;
        _gestureInProgress = false;
        if (completed)
        {
            Preferences.Set(SwipeCountKey, Preferences.Get(SwipeCountKey, 0) + 1);
            Log.Info("KeywordFlipService", $"Gesture {gestureId} completed; awaiting OCR response");
            SetDiagnostic("系统确认滑动完成，等待页面响应");
        }
        else
        {
            _nextSwipeAllowedAt = SystemClock.ElapsedRealtime() + FailedGestureRetryMs;
            Log.Warn("KeywordFlipService", $"Gesture {gestureId} was cancelled");
            SetDiagnostic("滑动被系统取消，1秒后重试");
        }

        UpdateOverlay();
    }

    private void SetDiagnostic(string message) =>
        Preferences.Set(DiagnosticKey, $"{message} · {DateTime.Now:HH:mm:ss}");

    private bool IsCurrentCapture(long captureId) =>
        Interlocked.Read(ref _activeCaptureId) == captureId;

    private void CompleteCapture(long captureId)
    {
        Interlocked.CompareExchange(ref _activeCaptureId, 0, captureId);
    }

    private static string NormalizeForMatch(string value) =>
        string.Concat(value.Where(character => !char.IsWhiteSpace(character)));

    private void MaskOverlayFromRecognition(Bitmap bitmap, int regionTop)
    {
        if (_overlay is null || _overlayParams is null)
            return;

        var overlayWidth = _overlay.Width > 0 ? _overlay.Width : _overlayParams.Width;
        var overlayHeight = _overlay.Height > 0 ? _overlay.Height : _overlayParams.Height;
        if (overlayWidth <= 0 || overlayHeight <= 0)
            return;

        var left = Math.Max(0, _overlayParams.X);
        var top = _overlayParams.Y - regionTop;
        var right = Math.Min(bitmap.Width, _overlayParams.X + overlayWidth);
        var bottom = Math.Min(bitmap.Height, top + overlayHeight);
        left = Math.Min(bitmap.Width, left);
        top = Math.Max(0, top);
        if (right <= left || bottom <= top)
            return;

        using var canvas = new Android.Graphics.Canvas(bitmap);
        using var paint = new Android.Graphics.Paint { Color = Android.Graphics.Color.Black, AntiAlias = false };
        canvas.DrawRect(left, top, right, bottom, paint);
    }

    private void LogScreenshotFailure(int errorCode)
    {
        Log.Warn("KeywordFlipService", $"Screenshot failed with error {errorCode}");
        SetDiagnostic($"系统截图失败 ({errorCode})，正在重试");
    }

    private sealed class SwipeResultCallback(KeywordFlipService owner, long gestureId) : AccessibilityService.GestureResultCallback
    {
        public override void OnCompleted(GestureDescription? gestureDescription) =>
            owner.OnGestureResult(gestureId, completed: true);

        public override void OnCancelled(GestureDescription? gestureDescription) =>
            owner.OnGestureResult(gestureId, completed: false);
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
        _nextSwipeAllowedAt = 0;
        _keywordActive = false;
        _consecutiveKeywordMisses = 0;
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

    private sealed class ScreenCaptureCallback(KeywordFlipService owner, long captureId) : Java.Lang.Object, AccessibilityService.ITakeScreenshotCallback
    {
        public void OnSuccess(AccessibilityService.ScreenshotResult? screenshot)
        {
            if (screenshot is null)
            {
                if (!owner.IsCurrentCapture(captureId))
                    return;

                owner.CompleteCapture(captureId);
                owner.SetDiagnostic("系统未返回截图，正在重试");
                return;
            }
            owner.OnScreenshot(screenshot, captureId);
        }

        public void OnFailure(int errorCode)
        {
            if (!owner.IsCurrentCapture(captureId))
                return;

            owner.CompleteCapture(captureId);
            owner.LogScreenshotFailure(errorCode);
        }
    }

    private sealed class OcrSuccessListener(KeywordFlipService owner, Bitmap bitmap, long captureId) : Java.Lang.Object, IOnSuccessListener
    {
        public void OnSuccess(Java.Lang.Object? result)
        {
            if (result is not null)
                owner.OnOcrResult(result, bitmap, captureId);
            else
                owner.OnOcrFailure(bitmap, captureId, null);
        }
    }

    private sealed class OcrFailureListener(KeywordFlipService owner, Bitmap bitmap, long captureId) : Java.Lang.Object, IOnFailureListener
    {
        public void OnFailure(Java.Lang.Exception? error) => owner.OnOcrFailure(bitmap, captureId, error);
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
