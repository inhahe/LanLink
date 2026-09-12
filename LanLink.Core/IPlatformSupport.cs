namespace LanLink;

/// <summary>
/// The one piece of platform behaviour the shared layer cannot express itself.
///
/// Android filters broadcast and multicast packets away from user space to save
/// battery unless a <c>WifiManager.MulticastLock</c> is held, so discovery
/// silently receives nothing without it.  Desktop needs no equivalent.  Rather
/// than put an <c>#if ANDROID</c> block in shared code — which would drag the
/// Android bindings into a library that must also build for net8.0-windows —
/// the app supplies an implementation.
/// </summary>
public interface IPlatformSupport
{
    /// <summary>
    /// Acquire a lock allowing broadcast/multicast reception.  Dispose to
    /// release.  Return null when the platform needs nothing, or when the lock
    /// is unavailable (WiFi hard-off on some OEM builds) — discovery then simply
    /// runs without it rather than failing.
    /// </summary>
    IDisposable? AcquireMulticastLock(string tag);
}
