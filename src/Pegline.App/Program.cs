using Velopack;

namespace Pegline.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Install, update and uninstall hooks run here and exit before any window exists.
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ => Platform.LoginItem.Set(false, force: true))
            .Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
