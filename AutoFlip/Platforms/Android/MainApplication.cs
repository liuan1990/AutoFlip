using Android.App;
using Android.Runtime;
using Microsoft.Maui.Storage;

namespace AutoFlip;

[Application]
public class MainApplication : MauiApplication
{
	public MainApplication(IntPtr handle, JniHandleOwnership ownership)
		: base(handle, ownership)
	{
	}

	public override void OnCreate()
	{
		base.OnCreate();
		Preferences.Set(
			"autoflip.sessionSwipeBaseline",
			Preferences.Get("autoflip.swipeCount", 0));
	}

	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
