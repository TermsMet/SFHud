using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SFHud.Controls;
using SFHud.Services;

namespace SFHud.Views;

/// <summary>SFHud's own setup / uninstall window (SFHud-Setup.exe, or SFHud.exe --install / --uninstall).</summary>
public sealed class InstallerWindow : Window
{
    private readonly StackPanel _body = new();
    private readonly bool _uninstall;

    public InstallerWindow(bool uninstall)
    {
        _uninstall = uninstall;
        Title = uninstall ? "Uninstall SFHud" : "SFHud Setup";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Width = 560;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        SetResourceReference(FontFamilyProperty, "F.Body");
        SetResourceReference(ForegroundProperty, "B.Text");
        try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/SFHud;component/Assets/sfhud.ico")); } catch (Exception) { }

        var logo = new Image { Width = 46, Height = 46, Margin = new Thickness(0, 0, 14, 0) };
        try { logo.Source = new BitmapImage(new Uri("pack://application:,,,/SFHud;component/Assets/sfhud-256.png")); } catch (Exception) { }
        var close = Ui.IconButton("", "Close", Close);
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 14), Background = Brushes.Transparent };
        DockPanel.SetDock(close, Dock.Right);
        head.Children.Add(close);
        head.Children.Add(logo);
        head.Children.Add(Ui.Stack(Ui.H1("SFHUD"), Ui.Label($"{Installer.ThisEdition.ToUpperInvariant()}  ·  VERSION {AppInfo.Version}  ·  {(uninstall ? "UNINSTALL" : "SETUP")}")));
        head.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch (InvalidOperationException) { } };

        var sp = new StackPanel();
        sp.Children.Add(head);
        sp.Children.Add(_body);
        var frame = new HudFrame { Content = sp, Chamfer = 16, ShowTab = true, Glow = true, Padding = new Thickness(26, 20, 22, 22), Margin = new Thickness(12) };
        frame.SetResourceReference(HudFrame.FillProperty, "B.Bg");
        frame.SetResourceReference(HudFrame.StrokeProperty, "B.Accent");
        Content = frame;

        if (uninstall) BuildUninstall(); else BuildInstall();
    }

    private void BuildInstall()
    {
        var existing = Installer.FindExisting();
        string dir = existing?.Dir ?? Installer.DefaultDir;
        _body.Children.Clear();
        _body.Children.Add(Ui.Text(existing is null
            ? "SFHud is a lightweight overlay and note pad companion for Garry's Mod roleplay. Setup installs it for your Windows account only, so no admin rights are needed."
            : existing.Edition != Installer.ThisEdition
                ? $"SFHud {existing.Edition} {existing.Version} is installed. Setup will replace it with SFHud {Installer.ThisEdition} {AppInfo.Version}: the program files are swapped, and your notes, layouts, settings, keybinds, sounds and backups are kept and carried over."
                : $"SFHud {existing.Version} is already installed. Setup will update it to {AppInfo.Version}. Your notes, settings, keybinds and backups are kept.", 13));
        _body.Children.Add(Ui.Spacer(14));
        _body.Children.Add(Ui.Label("INSTALL FOLDER"));
        var box = new TextBox { Text = dir, Margin = new Thickness(0, 4, 8, 0) };
        var browse = Ui.Button("BROWSE…", () =>
        {
            var d = new Microsoft.Win32.OpenFolderDialog { Title = "Choose where to install SFHud", InitialDirectory = Directory.Exists(box.Text) ? box.Text : Path.GetDirectoryName(box.Text) };
            if (d.ShowDialog() == true) box.Text = Path.GetFileName(d.FolderName).Equals("SFHud", StringComparison.OrdinalIgnoreCase) ? d.FolderName : Path.Combine(d.FolderName, "SFHud");
        });
        browse.Margin = new Thickness(0, 4, 0, 0);
        var row = new DockPanel();
        DockPanel.SetDock(browse, Dock.Right);
        row.Children.Add(browse);
        row.Children.Add(box);
        _body.Children.Add(row);
        _body.Children.Add(Ui.Hint("Your data stays in " + SettingsStore.DataDir + " (not in the install folder)."));
        bool desktop = true, start = true, launch = true;
        _body.Children.Add(Ui.Switch("Desktop shortcut", desktop, v => desktop = v));
        _body.Children.Add(Ui.Switch("Start Menu shortcut", start, v => start = v));
        _body.Children.Add(Ui.Switch("Launch SFHud when done", launch, v => launch = v));
        var status = Ui.Label("");
        var go = Ui.Button(existing is null ? "INSTALL" : "UPDATE", () => { }, "Sf.Button.Primary");
        go.Margin = new Thickness(0, 12, 10, 0);
        var cancel = Ui.Button("CANCEL", Close, "Sf.Button");
        cancel.Margin = new Thickness(0, 12, 0, 0);
        go.Click += async (_, _) =>
        {
            go.IsEnabled = cancel.IsEnabled = false;
            string target = box.Text.Trim();
            try
            {
                string exe = await Task.Run(() => Installer.Install(target, desktop, start, s => Dispatcher.Invoke(() => status.Text = s.ToUpperInvariant() + "…")));
                Done(exe, launch);
            }
            catch (Exception ex)
            {
                status.Text = "FAILED: " + ex.Message;
                status.SetResourceReference(TextBlock.ForegroundProperty, "B.Alert");
                go.IsEnabled = cancel.IsEnabled = true;
            }
        };
        _body.Children.Add(Ui.HStack(go, cancel));
        _body.Children.Add(Ui.Spacer(6));
        _body.Children.Add(status);
    }

    private void Done(string exe, bool launch)
    {
        _body.Children.Clear();
        _body.Children.Add(Ui.H2("INSTALLED", 14));
        _body.Children.Add(Ui.Spacer(6));
        _body.Children.Add(Ui.Text($"SFHud {AppInfo.Version} is installed in {Path.GetDirectoryName(exe)}.\n\nStart it from the Start Menu or desktop. Uninstall any time from Windows Settings › Apps.", 13));
        var finish = Ui.Button(launch ? "LAUNCH SFHUD" : "FINISH", () =>
        {
            if (launch) try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) }); } catch (Exception) { }
            Close();
        }, "Sf.Button.Primary");
        finish.Margin = new Thickness(0, 14, 0, 0);
        finish.HorizontalAlignment = HorizontalAlignment.Left;
        _body.Children.Add(finish);
    }

    private void BuildUninstall()
    {
        _body.Children.Clear();
        _body.Children.Add(Ui.Text("This removes SFHud.exe, its shortcuts and its Apps & Features entry.", 13));
        _body.Children.Add(Ui.Spacer(10));
        _body.Children.Add(Ui.Text($"Your notes, settings and backups in {SettingsStore.DataDir} are kept unless you tick the box below, so reinstalling brings everything back.", 12.5, "B.Dim"));
        _body.Children.Add(Ui.Spacer(10));
        bool data = false, backups = false;
        CheckBox? backupsBox = null;
        _body.Children.Add(Ui.Switch("Also delete my notes and settings", data, v => { data = v; if (backupsBox is not null) backupsBox.IsEnabled = v; }));
        backupsBox = Ui.Switch("…and delete backups too", backups, v => backups = v, "Leave off to keep a copy of everything in the backups folder.");
        backupsBox.IsEnabled = false;
        _body.Children.Add(backupsBox);
        var go = Ui.Button("UNINSTALL", () => { }, "Sf.Button.Small.Danger");
        go.Margin = new Thickness(0, 12, 10, 0);
        var cancel = Ui.Button("CANCEL", Close, "Sf.Button");
        cancel.Margin = new Thickness(0, 12, 0, 0);
        go.Click += async (_, _) =>
        {
            go.IsEnabled = cancel.IsEnabled = false;
            try
            {
                await Task.Run(() => Installer.Uninstall(data, data && backups));
                _body.Children.Clear();
                _body.Children.Add(Ui.H2("SFHUD WAS REMOVED", 14));
                _body.Children.Add(Ui.Text(data ? "SFHud and your data were removed." : $"Your data is still in {SettingsStore.DataDir}.", 13));
                var ok = Ui.Button("CLOSE", () => Application.Current.Shutdown(), "Sf.Button.Primary");
                ok.Margin = new Thickness(0, 14, 0, 0);
                ok.HorizontalAlignment = HorizontalAlignment.Left;
                _body.Children.Add(ok);
            }
            catch (Exception ex) { Ui.Info(this, "Uninstall", ex.Message); go.IsEnabled = cancel.IsEnabled = true; }
        };
        _body.Children.Add(Ui.HStack(go, cancel));
    }
}
