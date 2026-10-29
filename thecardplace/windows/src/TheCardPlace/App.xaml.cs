using System.Windows;
using System.Windows.Threading;
using TheCardPlace.Play;

namespace TheCardPlace.App;

public partial class App : Application
{
    public static AppSettings Settings { get; private set; } = new();

    /// <summary>
    /// <c>--seed N</c> on the command line deals every game from a fixed seed, so a recorded demo
    /// or a test can know every card in advance. Not offered anywhere in the app.
    /// </summary>
    public static ulong? Seed { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        var seedAt = Array.IndexOf(e.Args, "--seed");
        if (seedAt >= 0 && seedAt + 1 < e.Args.Length && ulong.TryParse(e.Args[seedAt + 1], out var seed)) Seed = seed;
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
        // Moving from one high contrast theme to another leaves HighContrast true; the colours
        // still change, and the theme reads them when it is loaded, so load it again.
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, a) =>
        {
            if (a.Category is Microsoft.Win32.UserPreferenceCategory.Color or Microsoft.Win32.UserPreferenceCategory.Accessibility)
                Dispatcher.BeginInvoke(ApplyTheme);
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
