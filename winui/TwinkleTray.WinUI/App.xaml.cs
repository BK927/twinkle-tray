using Microsoft.UI.Xaml;

namespace TwinkleTray.WinUI;

public partial class App : Application
{
    private AppController? _controller;
    public App()
    {
        InitializeComponent();
        UnhandledException += (_, args) => { Program.Log(args.Exception); };
    }
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try { _controller = new AppController(); await _controller.StartAsync(); }
        catch (Exception exception) { Program.Log(exception); Environment.ExitCode = 1; Exit(); }
    }
}
