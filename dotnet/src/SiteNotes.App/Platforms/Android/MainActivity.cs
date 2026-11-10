using Android.App;
using Android.Content.PM;

namespace SiteNotes.App;

[Activity(Name = "com.sitenotes.dpr.MainActivity", Exported = true, Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode |
                           ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Android.OS.Bundle? savedInstanceState)
    {
        // "adb shell am start ... --ez tour true" runs the screenshot tour (used by CI).
        Services.Tour.Enabled = Intent?.GetBooleanExtra("tour", false) == true;
        base.OnCreate(savedInstanceState);
    }
}
