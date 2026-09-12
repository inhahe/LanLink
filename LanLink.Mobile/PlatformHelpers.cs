namespace LanLink;

/// <summary>
/// Android "all files access" (MANAGE_EXTERNAL_STORAGE) gate for the in-app
/// file browser.
///
/// Under scoped storage an ordinary app cannot enumerate /storage/emulated/0 at
/// all -- <c>Directory.EnumerateFileSystemEntries</c> throws or comes back empty
/// -- so the browser would show nothing but empty folders without this.  The
/// permission is special: there is no runtime prompt for it on Android 11+, the
/// user has to flip it in system Settings, so all we can do is explain and open
/// that screen.
///
/// Google Play restricts apps declaring MANAGE_EXTERNAL_STORAGE, which does not
/// apply here -- LanLink ships as a sideloaded APK from GitHub releases.
/// </summary>
public static class StoragePermission
{
    /// <summary>True when shared storage can be enumerated right now.</summary>
    public static async Task<bool> HasAccessAsync()
    {
#if ANDROID
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
            return Android.OS.Environment.IsExternalStorageManager;

        var status = await Permissions.CheckStatusAsync<Permissions.StorageRead>();
        return status == PermissionStatus.Granted;
#else
        await Task.CompletedTask;
        return true;
#endif
    }

    /// <summary>
    /// On Android 10 and below shared storage is reachable through an ordinary
    /// runtime permission, so just ask.  On 11+ there is no runtime prompt for
    /// MANAGE_EXTERNAL_STORAGE and this always returns false -- the caller has
    /// to send the user to Settings instead.
    /// </summary>
    public static async Task<bool> TryRequestLegacyAsync()
    {
#if ANDROID
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
            return false;

        var status = await Permissions.RequestAsync<Permissions.StorageRead>();
        return status == PermissionStatus.Granted;
#else
        await Task.CompletedTask;
        return false;
#endif
    }

    /// <summary>
    /// Opens the settings screen where the user can grant storage access.  On
    /// Android 11+ that's the per-app "All files access" page; older releases
    /// have no such screen, so we open the app's details page where the ordinary
    /// storage permission lives.
    /// </summary>
    public static void OpenAllFilesAccessSettings()
    {
#if ANDROID
        string package = "package:" + Android.App.Application.Context.PackageName;

        // The version check is what keeps the API-30-only intents legal on the
        // API 21 floor this app still targets.
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            if (TryStartSettings(
                    Android.Provider.Settings.ActionManageAppAllFilesAccessPermission, package))
                return;

            // Some OEM builds reject the per-package intent; fall back to the
            // global list of apps holding all-files access.
            if (TryStartSettings(
                    Android.Provider.Settings.ActionManageAllFilesAccessPermission, null))
                return;
        }

        TryStartSettings(Android.Provider.Settings.ActionApplicationDetailsSettings, package);
#endif
    }

#if ANDROID
    private static bool TryStartSettings(string action, string? data)
    {
        try
        {
            var intent = data is null
                ? new Android.Content.Intent(action)
                : new Android.Content.Intent(action, Android.Net.Uri.Parse(data));

            var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
            if (activity is not null)
            {
                activity.StartActivity(intent);
            }
            else
            {
                intent.AddFlags(Android.Content.ActivityFlags.NewTask);
                Android.App.Application.Context.StartActivity(intent);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
#endif
}

/// <summary>
/// Starts and stops the Android foreground service that keeps LanLink reachable
/// while it is not the foreground app.  See
/// <c>Platforms/Android/LanLinkForegroundService.cs</c> for why it is needed and
/// what it deliberately does not do.
/// </summary>
public static class BackgroundKeepAlive
{
    /// <summary>
    /// Android 13+ hides the service's notification unless POST_NOTIFICATIONS is
    /// granted.  The service still runs either way, so a refusal costs only the
    /// status indicator; never block startup on it.
    /// </summary>
    public static async Task RequestNotificationPermissionAsync()
    {
#if ANDROID
        try
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(33)) return;

            var status = await Permissions.CheckStatusAsync<Permissions.PostNotifications>();
            if (status != PermissionStatus.Granted)
                await Permissions.RequestAsync<Permissions.PostNotifications>();
        }
        catch { }
#else
        await Task.CompletedTask;
#endif
    }

    public static void Start()
    {
#if ANDROID
        try
        {
            var context = Android.App.Application.Context;
            var intent  = new Android.Content.Intent(context, typeof(LanLinkForegroundService));

            if (OperatingSystem.IsAndroidVersionAtLeast(26))
                context.StartForegroundService(intent);
            else
                context.StartService(intent);
        }
        catch { /* keep-alive is an optimisation; never break startup over it */ }
#endif
    }

    public static void Stop()
    {
#if ANDROID
        try
        {
            var context = Android.App.Application.Context;
            context.StopService(new Android.Content.Intent(context, typeof(LanLinkForegroundService)));
        }
        catch { }
#endif
    }
}

/// <summary>
/// Brief non-modal feedback.  MAUI has no built-in toast (that lives in
/// CommunityToolkit.Maui) and pulling in a package for one call is not worth it,
/// so this wraps the platform toast directly.
/// </summary>
public static class Toasts
{
    public static void Show(string message)
    {
#if ANDROID
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                Android.Widget.Toast.MakeText(
                    Android.App.Application.Context,
                    message,
                    Android.Widget.ToastLength.Long)?.Show();
            }
            catch { /* toast failures are never worth surfacing */ }
        });
#endif
    }
}
