using System.Runtime.InteropServices;
using System.Windows;

namespace TheCardPlace.App.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        var source = PackageInfo.IsPackaged ? "Installed from the Microsoft Store, which keeps it up to date." : "Running outside the Microsoft Store.";
        InfoText.Text =
            $"Version {PackageInfo.Version}\n" +
            $"{source}\n" +
            $"Windows {Environment.OSVersion.Version}, {RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()}\n\n" +
            "Hearts, Euchre, Spades, Cribbage and Sheephead, played against the computer, with every card, " +
            "play and score in words for JAWS, NVDA and Narrator.\n\n" +
            "By Kelly Ford, The Idea Place. Nothing is sent anywhere; the games run entirely on this PC.";
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(InfoText.Text);
            ((MainWindow)Owner).Announcer.Say("Copied.", Play.AnnouncementCategory.Result);
        }
        catch (COMException)
        {
            ((MainWindow)Owner).Announcer.Error("Could not copy. Another program is using the clipboard.");
        }
    }
}
