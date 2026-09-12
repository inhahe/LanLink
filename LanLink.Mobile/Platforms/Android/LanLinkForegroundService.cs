using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net.Wifi;
using Android.OS;
using AndroidX.Core.App;

namespace LanLink;

/// <summary>
/// Keeps LanLink reachable while it is not the foreground app.
///
/// Measured on a Galaxy S21 (One UI 6 / Android 14) before this existed: with the
/// app foregrounded, <c>PC -&gt; phone:37656</c> succeeded; press HOME and it failed
/// on every attempt, then recovered the instant the app came back. The listening
/// socket was still open and the process alive (state <c>S</c>, standby bucket 10),
/// so Android was restricting a backgrounded app's networking rather than the app
/// dying. A foreground service is the supported way out of that.
///
/// **This service does not own the network.** <c>MainPage</c> still constructs and
/// owns <c>NetworkManager</c>/<c>TransferManager</c>; this type exists only to (a)
/// give the process foreground priority so its sockets keep being serviced, and
/// (b) hold a WiFi lock so the radio stays out of power-save with the screen off.
/// That is why it returns <see cref="StartCommandResult.NotSticky"/> — were Android
/// to restart the service by itself, it would post a "sharing" notification with no
/// network behind it, which is worse than being stopped. Moving ownership of the
/// network in here is the real fix and is a larger refactor.
/// </summary>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeDataSync)]
public sealed class LanLinkForegroundService : Service
{
    public const  string ChannelId      = "lanlink_sharing";
    private const int    NotificationId = 1001;

    private WifiManager.WifiLock? _wifiLock;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent,
                                                      StartCommandFlags flags,
                                                      int startId)
    {
        CreateChannel();

        var notification = BuildNotification();

        // API 29+ wants the type restated at StartForeground, and on API 34+ it
        // must match the foregroundServiceType declared on the [Service].
        if (OperatingSystem.IsAndroidVersionAtLeast(29))
            StartForeground(NotificationId, notification, ForegroundService.TypeDataSync);
        else
            StartForeground(NotificationId, notification);

        AcquireWifiLock();

        return StartCommandResult.NotSticky;
    }

    public override void OnDestroy()
    {
        ReleaseWifiLock();
        base.OnDestroy();
    }

    // ------------------------------------------------------------------ wifi lock

    private void AcquireWifiLock()
    {
        try
        {
            if (GetSystemService(WifiService) is not WifiManager wifi) return;

            // Fully qualified: the lock-mode enum lives in Android.Net, NOT in
            // Android.Net.Wifi next to WifiManager, and an unrelated WifiMode
            // (WifiOnly / WifiPreferred / CellularPreferred) also exists.
            //
            // FULL_HIGH_PERF is deprecated in favour of LOW_LATENCY, but
            // LOW_LATENCY only takes effect while the app is in the foreground —
            // precisely the case that already works. HIGH_PERF is the one that
            // keeps the radio answering inbound connections with the screen off.
            var wifiLock = wifi.CreateWifiLock(Android.Net.WifiMode.FullHighPerf, "LanLink:wifi");
            if (wifiLock is null) return;

            wifiLock.SetReferenceCounted(false);
            wifiLock.Acquire();
            _wifiLock = wifiLock;
        }
        catch
        {
            // A missing lock degrades background reachability; it must never
            // take the service down with it.
            _wifiLock = null;
        }
    }

    private void ReleaseWifiLock()
    {
        try
        {
            if (_wifiLock is { IsHeld: true })
                _wifiLock.Release();
        }
        catch { }
        _wifiLock = null;
    }

    // ------------------------------------------------------------------ notification

    private void CreateChannel()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return;
        if (GetSystemService(NotificationService) is not NotificationManager mgr) return;
        if (mgr.GetNotificationChannel(ChannelId) is not null) return;

        // Low importance: persistent, silent, no badge. It is a status indicator,
        // not an alert.
        var channel = new NotificationChannel(ChannelId, "LanLink sharing",
                                              NotificationImportance.Low)
        {
            Description = "Shown while LanLink stays reachable by your other devices."
        };
        channel.SetShowBadge(false);
        channel.EnableVibration(false);
        mgr.CreateNotificationChannel(channel);
    }

    private Notification BuildNotification()
    {
        var launch = new Intent(this, typeof(MainActivity));
        launch.SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);

        var flags = OperatingSystem.IsAndroidVersionAtLeast(23)
            ? PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent
            : PendingIntentFlags.UpdateCurrent;

        var pending = PendingIntent.GetActivity(this, 0, launch, flags);

        // Set as statements rather than a fluent chain: every AndroidX builder
        // setter is annotated as returning a nullable Builder, so chaining them
        // produces a CS8602 on each link for no benefit.
        var builder = new NotificationCompat.Builder(this, ChannelId);
        builder.SetContentTitle("LanLink");
        builder.SetContentText("Reachable by your other devices");
        builder.SetSmallIcon(Resource.Mipmap.appicon);
        builder.SetOngoing(true);
        builder.SetPriority(NotificationCompat.PriorityLow);
        builder.SetVisibility(NotificationCompat.VisibilityPublic);
        builder.SetContentIntent(pending);

        // Build() is annotated nullable but never returns null, and a foreground
        // service with no notification cannot legally start anyway.
        return builder.Build()!;
    }
}
