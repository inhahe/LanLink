using Android.App;
using Android.Content.PM;
using Android.OS;

// Network permissions — declared as assembly attributes so the build system
// always emits them into the merged manifest.
[assembly: UsesPermission(Android.Manifest.Permission.Internet)]
[assembly: UsesPermission(Android.Manifest.Permission.AccessNetworkState)]
[assembly: UsesPermission(Android.Manifest.Permission.AccessWifiState)]
[assembly: UsesPermission(Android.Manifest.Permission.ChangeWifiMulticastState)]

namespace LanLink;

// LaunchMode.SingleTop is load-bearing, not decoration.  Without it, launching
// the app while it is already running (tapping the icon after backgrounding it,
// or an `am start`) creates a *second* MainActivity; MAUI's single Window is
// already bound to the first, so CreatePlatformWindow throws
// InvalidOperationException and the process dies -- taking any in-flight
// transfer and the whole activity log with it.  The stock MAUI template sets
// this; this project had dropped it.
[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true,
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation |
                           ConfigChanges.UiMode | ConfigChanges.ScreenLayout |
                           ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // Keep WiFi active while app is in foreground for reliable discovery.
        Window?.AddFlags(Android.Views.WindowManagerFlags.KeepScreenOn);
    }
}
