namespace LanLink;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    /// <summary>
    /// .NET 9 MAUI replaces the old `MainPage = new AppShell()` assignment with a
    /// window factory.  Besides clearing the CS0618 deprecation, this is the
    /// supported shape for an app whose activity can be recreated: MAUI asks for
    /// a Window when it needs one rather than being handed a page bound to a
    /// single activity instance.
    /// </summary>
    protected override Window CreateWindow(IActivationState? activationState)
        => new Window(new AppShell());
}
