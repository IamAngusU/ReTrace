using System.IO;
using System.Windows;

namespace ReTrace.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--self-test", StringComparer.Ordinal))
        {
            Console.WriteLine("ReTrace desktop · native WPF · 0.1.0-alpha.4");
            Shutdown(0);
            return;
        }
        DispatcherUnhandledException += (_, error) =>
        {
            error.Handled = true;
            MessageBox.Show(error.Exception.Message, "ReTrace", MessageBoxButton.OK, MessageBoxImage.Error);
        };
        MainWindow = new MainWindow(e.Args);
        MainWindow.Show();
    }
}
