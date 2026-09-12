namespace LanLink;

/// <summary>
/// The settings the shared network/transfer layer actually needs.
///
/// Settings *persistence* is genuinely platform-specific and deliberately stays
/// in each app: the desktop serialises JSON to %LOCALAPPDATA%, while mobile uses
/// MAUI <c>Preferences</c>.  Those two implementations had diverged by 114 of
/// 95/55 lines — more than any other shared file — because they are not the same
/// thing wearing different hats.  Core depends on this interface instead, and
/// each app's <c>AppSettings</c> implements it while keeping whatever extra
/// platform-only properties it needs (RunOnStartup, StartupMode, ...).
/// </summary>
public interface ILanLinkSettings
{
    /// <summary>Stable per-install identity used for routing.</summary>
    string NodeId { get; }

    /// <summary>Name shown to peers.</summary>
    string DisplayName { get; }

    /// <summary>Where received files are written.</summary>
    string DownloadFolder { get; }

    /// <summary>TCP (data) and UDP (discovery) port.</summary>
    int Port { get; }

    /// <summary>
    /// Allow inbound connections originating outside the local network.
    /// Outgoing connections the user initiates are always permitted.
    /// </summary>
    bool AcceptExternalConnections { get; }
}
