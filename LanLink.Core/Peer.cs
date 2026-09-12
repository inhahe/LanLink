using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LanLink;

public enum RouteType { Lan, Direct, Relayed }

public sealed class Peer : INotifyPropertyChanged
{
    public string NodeId { get; set; } = "";

    private string _name = "";
    public string Name
    {
        get => _name;
        set { _name = value; Notify(); Notify(nameof(DisplayLabel)); }
    }

    private bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        set { _isConnected = value; Notify(); Notify(nameof(StatusLabel)); }
    }

    private RouteType _routeType;
    public RouteType RouteType
    {
        get => _routeType;
        set { _routeType = value; Notify(); Notify(nameof(RouteLabel)); Notify(nameof(StatusLabel)); Notify(nameof(DisplayLabel)); }
    }

    public string? NextHopId { get; set; }
    public string? Endpoint  { get; set; }   // ip:port for direct/LAN peers
    public int     Hops      { get; set; }
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;

    public string RouteLabel => RouteType switch
    {
        RouteType.Lan     => "LAN",
        RouteType.Direct  => "Direct",
        RouteType.Relayed => "Relay",
        _                 => ""
    };

    public string StatusLabel => _isConnected ? RouteLabel : "Offline";

    public string DisplayLabel => $"{Name}  ({RouteLabel})";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? prop = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
}

// ---------------------------------------------------------------------------
//  Log entry shown in the activity list.
//  Text is mutable so transfer-progress lines can update in place.
// ---------------------------------------------------------------------------

public enum LogLevel { Info, TextReceived, TextSent, Transfer, Error }

public enum DeliveryStatus { None, Delivered, Pending }

public sealed class LogEntry : INotifyPropertyChanged
{
    public DateTime Time  { get; init; } = DateTime.Now;
    public LogLevel Level { get; init; } = LogLevel.Info;

    private string _text = "";
    public string Text
    {
        get => _text;
        set
        {
            if (_text == value) return;
            _text = value;
            Notify();
            Notify(nameof(Display));
        }
    }

    private DeliveryStatus _delivery = DeliveryStatus.None;
    public DeliveryStatus Delivery
    {
        get => _delivery;
        set
        {
            if (_delivery == value) return;
            _delivery = value;
            Notify();
            Notify(nameof(Display));
            Notify(nameof(DeliveryIcon));
        }
    }

    /// <summary>Optional id linking this entry to a pending message for later update.</summary>
    public string? PendingId { get; set; }

    public string DeliveryIcon => _delivery switch
    {
        DeliveryStatus.Delivered => " \u2713",
        DeliveryStatus.Pending   => " \u23F3",
        _ => ""
    };

    public string Display => $"[{Time:HH:mm:ss}]  {Text}{DeliveryIcon}";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? prop = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
}
