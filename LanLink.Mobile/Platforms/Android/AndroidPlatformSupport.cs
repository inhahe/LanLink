using Android.Content;
using Android.Net.Wifi;

namespace LanLink;

/// <summary>
/// Android implementation of <see cref="IPlatformSupport"/>.
///
/// This is the only platform-specific behaviour the shared network layer needs.
/// It used to live as an <c>#if ANDROID</c> block inside the mobile copy of
/// Discovery.cs; moving it behind an interface is what let that file be shared
/// with the desktop, which must build for net8.0-windows and cannot see the
/// Android bindings at all.
/// </summary>
public sealed class AndroidPlatformSupport : IPlatformSupport
{
    public IDisposable? AcquireMulticastLock(string tag)
    {
        try
        {
            if (Android.App.Application.Context.GetSystemService(Context.WifiService)
                    is not WifiManager wifi)
                return null;

            // Annotated nullable, and the null case is real: it returns null when
            // the WiFi service is unavailable (WiFi hard-off on some OEM builds).
            var multicastLock = wifi.CreateMulticastLock(tag);
            if (multicastLock is null) return null;

            multicastLock.Acquire();
            return new MulticastLockHandle(multicastLock);
        }
        catch
        {
            // Discovery runs without the lock rather than failing outright: on a
            // network that does deliver broadcasts anyway it still works.
            return null;
        }
    }

    private sealed class MulticastLockHandle : IDisposable
    {
        private WifiManager.MulticastLock? _lock;

        public MulticastLockHandle(WifiManager.MulticastLock multicastLock)
            => _lock = multicastLock;

        public void Dispose()
        {
            try
            {
                if (_lock is { IsHeld: true })
                    _lock.Release();
            }
            catch { }
            _lock = null;
        }
    }
}
