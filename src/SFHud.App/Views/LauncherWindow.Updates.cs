using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using SFHud.Controls;
using SFHud.Services;

namespace SFHud.Views;

/// <summary>
/// Updates and news from the SFHud website (admin › Updates).
/// Updates: an update bar at the top of this window whenever the site has a newer version for this edition
/// (Free and Pro alike), checked at startup and every 30 minutes. News: a pop-up, once each, never over the game.
/// </summary>
public partial class LauncherWindow
{
    private bool _announcementOpen, _updating;
    private string _updateBarHidden = "";

    private void StartAnnouncements()
    {
        Announcements.Instance.Changed += () => Dispatcher.BeginInvoke(() => { RefreshUpdateBar(); ShowNextAnnouncement(); });
        _overlay.InGameChanged += inGame => { if (!inGame) Dispatcher.BeginInvoke(ShowNextAnnouncement); };
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
        timer.Tick += (_, _) => _ = Announcements.Instance.RefreshAsync();
        timer.Start();
        Dispatcher.BeginInvoke(async () => { await Task.Delay(5_000); await Announcements.Instance.RefreshAsync(); });
    }

    /// <summary>Shows the bar for the newest version above yours, unless you closed it for that version this session.</summary>
    private void RefreshUpdateBar()
    {
        var u = Announcements.Instance.Latest;
        if (u is null || _updateBarHidden == u.Version) { if (!_updating) UpdateBar.Visibility = Visibility.Collapsed; return; }
        bool first = UpdateBar.Visibility != Visibility.Visible;
        UpdateBarTitle.Text = $"SFHud {(Edition.IsProBuild ? "Pro" : "Free")} {u.Version} is available · you have {AppInfo.Version}";
        if (!_updating) UpdateBarText.Text = u.Title.Length > 0 ? u.Title + " · updating keeps all your notes, layouts and settings." : "Updating keeps all your notes, layouts and settings.";
        UpdateBar.Visibility = Visibility.Visible;
        if (first && _overlay.InGame) _overlay.ShowToast("UPDATE", $"SFHud {u.Version} is available. Open SFHud ({Gesture("dashboard.open")}) to update.");
    }

    private void UpdateBar_Close(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        _updateBarHidden = Announcements.Instance.Latest?.Version ?? "";
        UpdateBar.Visibility = Visibility.Collapsed;
    }

    private void UpdateBar_Notes(object sender, RoutedEventArgs e)
    {
        if (Announcements.Instance.Latest is not { } u) return;
        Ui.Info(this, $"What's new in {u.Version}", (u.Title + "\n\n" + u.Body).Trim());
    }

    private async void UpdateBar_Install(object sender, RoutedEventArgs e)
    {
        if (_updating || Announcements.Instance.Latest is not { } u) return;
        _updating = true;
        UpdateBarInstall.IsEnabled = UpdateBarNotes.IsEnabled = false;
        try
        {
            Func<Task<(string, string)>>? proUrl = null;
            UpdateBarText.Text = "Downloading the update…";
            string err = await Announcements.Instance.InstallUpdateAsync(u.Version, proUrl, s => Dispatcher.BeginInvoke(() => UpdateBarText.Text = s));
            UpdateBarText.Text = err.Length > 0 ? err : "Setup is starting. It closes SFHud (saving first) and installs the update.";
        }
        finally
        {
            _updating = false;
            UpdateBarInstall.IsEnabled = UpdateBarNotes.IsEnabled = true;
        }
    }

    /// <summary>News pop-ups, once each. Update posts are shown by the update bar instead.</summary>
    private void ShowNextAnnouncement()
    {
        if (_announcementOpen || _overlay.InGame) return;
        var next = Announcements.Instance.Pending(S.SeenAnnouncements).FirstOrDefault(i => !i.IsUpdate);
        if (next is null) return;
        _announcementOpen = true;
        try
        {
            S.SeenAnnouncements.Add(next.Id);
            DataStore.MarkDirty();
            Window? owner = IsVisible && WindowState != WindowState.Minimized ? this : null;
            Ui.Info(owner, next.Title, next.Body);
        }
        finally
        {
            _announcementOpen = false;
        }
        ShowNextAnnouncement();
    }
}
