using System.Windows;
using System.Windows.Threading;
using TheCardPlace.Play;

namespace TheCardPlace.App;

public partial class App : Application
{
    public static AppSettings Settings { get; private set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Diagnostics.Log("unhandled: " + a.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, a) =>
        {
            Diagnostics.Log("unobserved task: " + a.Exception);
            a.SetObserved();
        };

        Settings = SettingsStore.Load();
        ApplyTheme();
        SystemParameters.StaticPropertyChanged += (_, a) =>
        {
            if (a.PropertyName == nameof(SystemParameters.HighContrast)) ApplyTheme();
        };

        base.OnStartup(e);
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SettingsStore.Save(Settings);
        base.OnExit(e);
    }

    /// <summary>The felt theme, or the person's own colours when Windows is in high contrast.</summary>
    private void ApplyTheme()
    {
        var source = SystemParameters.HighContrast ? "Themes/HighContrast.xaml" : "Themes/Felt.xaml";
        Resources.MergedDictionaries[0] = new ResourceDictionary { Source = new Uri(source, UriKind.Relative) };
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Diagnostics.Log("crash: " + e.Exception);
        MessageBox.Show(MainWindow!,
            "Something went wrong and The Card Place has to close.\n\n" + e.Exception.Message +
            "\n\nThe details are in " + Diagnostics.LogPath + ".",
            "The Card Place", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = false;
    }
}
