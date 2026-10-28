using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Threading;
using TheCardPlace.Play;

namespace TheCardPlace.App;

/// <summary>
/// Speaks through UI Automation notifications, which JAWS, NVDA and Narrator all read from a
/// native window. (WPF's LiveSetting does nothing on its own: WPF never raises the event.)
///
/// Two timing rules from the baseline: a message is raised after the dispatcher goes idle, so
/// one that goes with a focus change (a card played, the next card focused) follows the new
/// focus rather than being cut off by it; and while the window is not in front, messages are
/// held and spoken when it is next activated, because a screen reader may ignore a background
/// window's notifications. Interrupting messages use ImportantMostRecent; the rest use All,
/// which queues, because the MostRecent kinds make NVDA stop what it is reading.
/// </summary>
public sealed class UiaSpeaker : ISpeaker
{
    private readonly Window _window;
    private readonly List<(string Text, bool Interrupt, string Id)> _held = [];
    private readonly List<(string Text, bool Interrupt, string Id)> _pending = [];
    private bool _flushScheduled;

    public UiaSpeaker(Window window)
    {
        _window = window;
        _window.Activated += (_, _) => Flush();
    }

    public void Speak(string text, bool interrupt, string activityId)
    {
        _pending.Add((text, interrupt, activityId));
        ScheduleFlush();
    }

    private void ScheduleFlush()
    {
        if (_flushScheduled) return;
        _flushScheduled = true;
        _window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            _flushScheduled = false;
            var batch = Coalesce(_pending);
            _pending.Clear();
            // The app is in front if any of its windows is: a dialog over the table counts, so
            // "Copied" in About is heard in About, not after it closes.
            if (ActiveWindow() is not { } active)
            {
                _held.AddRange(batch);
                return;
            }
            foreach (var (t, i, id) in batch) Raise(active, t, i, id);
        });
    }

    /// <summary>
    /// Messages that arrive together go out as few notifications as possible: queued ones next to
    /// each other become one. A test run with NVDA lost one of three notifications raised in the
    /// same instant; one notification cannot lose its middle.
    /// </summary>
    private static List<(string Text, bool Interrupt, string Id)> Coalesce(List<(string Text, bool Interrupt, string Id)> items)
    {
        var result = new List<(string Text, bool Interrupt, string Id)>();
        foreach (var item in items)
        {
            if (!item.Interrupt && result.Count > 0 && !result[^1].Interrupt)
                result[^1] = (result[^1].Text + " " + item.Text, false, item.Id);
            else
                result.Add(item);
        }
        return result;
    }

    private static Window? ActiveWindow() =>
        Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);

    /// <summary>
    /// What was held while the app was in the background, queued behind whatever is read on
    /// return, and sent with anything else spoken in the same moment as one notification.
    /// </summary>
    private void Flush()
    {
        if (_held.Count == 0) return;
        var held = _held.Select(h => (h.Text, false, h.Id)).ToList();
        _held.Clear();
        _pending.InsertRange(0, held);
        ScheduleFlush();
    }

    /// <summary>
    /// Drops what is held or waiting: the table it was about has gone (a new game, back to the
    /// games), and hearing its last plays after the new deal would only confuse.
    /// </summary>
    public void Clear()
    {
        _held.Clear();
        _pending.Clear();
    }

    private static void Raise(Window window, string text, bool interrupt, string activityId)
    {
        try
        {
            var peer = UIElementAutomationPeer.FromElement(window) ?? UIElementAutomationPeer.CreatePeerForElement(window);
            peer?.RaiseNotificationEvent(
                AutomationNotificationKind.Other,
                interrupt ? AutomationNotificationProcessing.ImportantMostRecent : AutomationNotificationProcessing.All,
                text,
                activityId);
        }
        catch (Exception ex)
        {
            Diagnostics.Log("announce failed: " + ex.Message);
        }
    }
}

/// <summary>
/// Where the settings and the log live: %APPDATA%\TheCardPlace. In a Store package Windows
/// redirects that folder into the package's own, so the app never needs to know which it is.
/// Nothing is ever written beside the program.
/// </summary>
public static class SettingsStore
{
    public static string Folder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TheCardPlace");

    private static string FilePath => Path.Combine(Folder, "settings.json");

    /// <summary>Overrides the folder, for the UI tests: TCP_SETTINGS_DIR.</summary>
    private static string EffectivePath =>
        Environment.GetEnvironmentVariable("TCP_SETTINGS_DIR") is { Length: > 0 } dir ? Path.Combine(dir, "settings.json") : FilePath;

    public static AppSettings Load()
    {
        try
        {
            return File.Exists(EffectivePath) ? AppSettings.FromJson(File.ReadAllText(EffectivePath)) : new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Log("settings unreadable: " + ex.Message);
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            var path = EffectivePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, settings.ToJson());
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Log("settings not saved: " + ex.Message);
        }
    }
}

/// <summary>A small rotated log file, for crash reports. Never throws.</summary>
public static class Diagnostics
{
    private static readonly object Gate = new();
    public static string LogPath => Path.Combine(SettingsStore.Folder, "TheCardPlace.log");

    public static void Log(string line)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(SettingsStore.Folder);
                var info = new FileInfo(LogPath);
                if (info.Exists && info.Length > 512 * 1024)
                    File.Move(LogPath, LogPath + ".old", overwrite: true);
                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never be the reason the app stops.
        }
    }
}

/// <summary>Whether this copy runs from an MSIX package (the Microsoft Store), which decides where updates come from.</summary>
public static partial class PackageInfo
{
    [LibraryImport("kernel32.dll")]
    private static partial int GetCurrentPackageFullName(ref int length, IntPtr name);

    public static bool IsPackaged
    {
        get
        {
            try
            {
                var len = 0;
                // APPMODEL_ERROR_NO_PACKAGE (15700) for a copy that is not in a package.
                return GetCurrentPackageFullName(ref len, IntPtr.Zero) != 15700;
            }
            catch
            {
                return false;
            }
        }
    }

    public static string Version
    {
        get
        {
            var v = typeof(PackageInfo).Assembly.GetName().Version;
            return v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }
}
