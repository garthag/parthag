using Android.App;
using Android.Content.PM;
using Android.OS;

namespace ConstructDpr.Mobile;

[Activity(Name = "org.constructdpr.dpr.MainActivity", Theme = "@style/Maui.SplashTheme", MainLauncher = true,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
#if SCREENSHOTS
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        // Test builds only (never in the released .apk): the build pipeline starts the app with a test server and account.
        if (Intent?.GetStringExtra("cdpr_auto_server") is { Length: > 0 } server)
            ScreenshotMode.Options = new ScreenshotMode.Settings(server, Intent.GetStringExtra("cdpr_auto_email") ?? "", Intent.GetStringExtra("cdpr_auto_password") ?? "",
                GetExternalFilesDir(null)?.AbsolutePath ?? FilesDir!.AbsolutePath);
        base.OnCreate(savedInstanceState);
    }
#endif
}
