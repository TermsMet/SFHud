using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SFHud.Controls;
using SFHud.Services;

namespace SFHud.Views;

/// <summary>The SFHud Pro page: what Pro adds, buying it, free trial offers, and (in the Pro build) the license.</summary>
public partial class LauncherWindow
{
    private ProFeature? _proHighlight;
    private string _trialKey = "", _trialMessage = "";
    private bool _trialBusy;

    /// <summary>Title bar tag line and button show the edition (and a live trial offer).</summary>
    private void UpdateEditionBadge()
    {
        TrialOffers.NoteEdition();
        TagLine.Text = Edition.Unlocked ? "//  GAME OVERLAY & FIELD MANUAL  ·  PRO" : "//  GAME OVERLAY & FIELD MANUAL";
        TagLine.SetResourceReference(TextBlock.ForegroundProperty, Edition.Unlocked ? "B.Amber" : "B.Dim");
        bool trial = TrialOffers.Eligible && TrialOffers.Instance.Offers.Count > 0;
        // Title bar button: GET PRO / FREE TRIAL in Free, a PRO badge (opens the license card) once unlocked.
        ProButtonText.Text = Edition.Unlocked ? "PRO" : trial ? "FREE TRIAL" : Edition.IsProBuild ? "ACTIVATE PRO" : "GET PRO";
        ProButton.ToolTip = Edition.Unlocked ? "SFHud Pro is active. Click for your license." :
            trial ? $"{TrialOffers.Instance.Offers[0].Title}: {TrialOffers.Instance.Offers[0].Days} days of SFHud Pro, free." :
            Edition.IsProBuild ? "Enter your license key to unlock Pro." : $"SFHud Pro: {Edition.PriceLifetime} once or {Edition.PriceMonthly} a month. See what it adds.";
        // Pro-only pages and buttons are faded in Free; the sidebar's Local AI entry too.
        if (_nav.TryGetValue("ai", out var ai)) ai.Opacity = Edition.Has(ProFeature.LocalAi) ? 1 : 0.5;
    }

    /// <summary>Checks for trial offers now and every few hours; a new offer gets one banner.</summary>
    private void StartTrialOffers()
    {
        TrialOffers.Instance.Changed += () => Dispatcher.BeginInvoke(() =>
        {
            UpdateEditionBadge();
            if (_page == "pro") RenderPage();
            if (!TrialOffers.Eligible) return;
            foreach (var o in TrialOffers.Instance.Offers.Where(o => !S.SeenTrialOffers.Contains(o.Id)))
            {
                S.SeenTrialOffers.Add(o.Id);
                DataStore.MarkDirty();
                ShowBanner($"★  {o.Title}: {o.Days} days of SFHud Pro, free. Open the SFHud Pro page (top right) to start it.", sticky: false);
                break;
            }
        });
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromHours(4) };
        timer.Tick += (_, _) => _ = TrialOffers.Instance.RefreshAsync();
        timer.Start();
        Dispatcher.BeginInvoke(async () => { await System.Threading.Tasks.Task.Delay(8000); await TrialOffers.Instance.RefreshAsync(force: true); });
    }

    /// <summary>Opened from any PRO chip; highlights the feature that was clicked.</summary>
    private void ShowUpgrade(ProFeature? f)
    {
        _proHighlight = f;
        BringToFront();
        Navigate("pro");
        _ = TrialOffers.Instance.RefreshAsync();
    }

    private FrameworkElement ProPage()
    {
        var sp = new StackPanel();
        sp.Children.Add(PageHeader("SFHud Pro",
            Edition.Unlocked ? "Pro is active. Thank you for supporting SFHud!" :
                $"{Edition.PriceLifetime} once (lifetime) or {Edition.PriceMonthly} a month. Every Pro update included; everything in SFHud Free stays free.",
            Edition.Unlocked ? Ui.Spacer(0) : Ui.Button("SEE PLANS", () => OpenUrl(Edition.BuyUrl), "Sf.Button.Small.Primary", "Opens the store in your browser")));

        if (_proHighlight is { } hf && !Edition.Has(hf))
        {
            var why = Ui.Text($"★  {Edition.Describe(hf)} is part of SFHud Pro.", 13, "B.Amber");
            why.Margin = new Thickness(0, 0, 0, 12);
            sp.Children.Add(why);
        }

        // Comparison table, built from Edition.Table so it always matches what's gated.
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.1, GridUnitType.Star), MinWidth = 100 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 80 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.6, GridUnitType.Star), MinWidth = 120 });
        void Cell(UIElement e, int r, int c) { Grid.SetRow(e, r); Grid.SetColumn(e, c); grid.Children.Add(e); }
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Cell(Ui.Label("FEATURE"), 0, 0);
        Cell(Ui.Label("FREE"), 0, 1);
        var proHead = Ui.Label("PRO");
        proHead.SetResourceReference(TextBlock.ForegroundProperty, "B.Amber");
        Cell(proHead, 0, 2);
        int row = 1;
        foreach (var (feature, name, free, pro) in Edition.Table)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            bool hot = feature == _proHighlight;
            var bg = new Border { CornerRadius = new CornerRadius(4), Margin = new Thickness(-6, 1, -6, 1) };
            bg.SetResourceReference(Border.BackgroundProperty, hot ? "B.Amber.Fill" : row % 2 == 0 ? "B.Accent.Fill" : "B.Panel");
            Grid.SetColumnSpan(bg, 3);
            Cell(bg, row, 0);
            var n = Ui.Text(name, 12.5, hot ? "B.Amber" : "B.Text");
            n.Margin = new Thickness(0, 6, 8, 6);
            Cell(n, row, 0);
            var f = Ui.Text(free, 12, "B.Dim");
            f.Margin = new Thickness(0, 6, 8, 6);
            Cell(f, row, 1);
            var p = Ui.Text((Edition.Has(feature) ? "✓  " : "") + pro, 12, Edition.Has(feature) ? "B.Ok" : "B.Text");
            p.Margin = new Thickness(0, 6, 4, 6);
            Cell(p, row, 2);
            row++;
        }
        var compare = Card("FREE VS PRO", "SFHud Free is the full note pad, overlay, DEFCON, timers, crosshair and music app, and it's open source. Pro adds the extras below.", grid);

        var alwaysFree = Card("ALWAYS FREE", null, Ui.Hint(
            "Unlimited note pads and notes with images · every overlay element except video · customize mode · up to " +
            $"{Edition.FreePages} overlay pages per preset · DEFCON levels, keys and suggestions · timers and sounds · crosshair · " +
            "music player · overlay whitelist · 4:3 / CRT and 4K scaling · all built-in presets and themes · backups."));

        var right = Ui.Stack();
        if (TrialCard() is { } trial) right.Children.Add(trial);
        right.Children.Add(LicenseCard());
        right.Children.Add(alwaysFree);
        sp.Children.Add(Columns(380, compare, right));
        return sp;
    }

    // ---------------------------------------------------------------- free trial offers (non-Pro users only)

    private FrameworkElement? TrialCard()
    {
        if (!TrialOffers.Eligible) return null;
        if (_trialKey.Length > 0) return TrialKeyCard();
        var offers = TrialOffers.Instance.Offers;
        if (offers.Count == 0) return null;
        var body = new StackPanel();
        foreach (var o in offers)
        {
            var title = Ui.Text(o.Title, 14, "B.Amber");
            body.Children.Add(title);
            if (o.Message.Length > 0) body.Children.Add(Ui.Hint(o.Message));
            body.Children.Add(Ui.Label($"{o.Days} DAYS OF PRO · FREE · OFFER ENDS {o.EndsUtc.ToLocalTime():MMM d}"));
            var offer = o;
            body.Children.Add(Ui.Wrap(Ui.Button(_trialBusy ? "STARTING…" : "START FREE TRIAL", () => ClaimTrial(offer), "Sf.Button.Small.Primary")));
            body.Children.Add(Ui.Spacer(8));
        }
        if (_trialMessage.Length > 0) body.Children.Add(Ui.Text(_trialMessage, 12.5, "B.Amber"));
        body.Children.Add(Ui.Hint("One trial per PC. No card needed. When it ends, Pro features fade out again; nothing you made is lost."));
        return Card("FREE TRIAL", null, body);
    }

    private async void ClaimTrial(TrialOffers.Offer o)
    {
        if (_trialBusy) return;
        _trialBusy = true; _trialMessage = "";
        RenderPage();
        var (key, err) = await TrialOffers.Instance.ClaimAsync(o);
        if (key.Length == 0) { _trialMessage = err; _trialBusy = false; RenderPage(); return; }
        _trialKey = key;
        try { Clipboard.SetText(key); } catch (Exception) { }
        _trialBusy = false;
        RenderPage();
    }

    /// <summary>After claiming in Free: the key, and the Pro add-on download (setup asks for the key at the end).</summary>
    private FrameworkElement TrialKeyCard()
    {
        var keyBox = new TextBox { Text = _trialKey, IsReadOnly = true, FontSize = 15, Margin = new Thickness(0, 4, 0, 6) };
        keyBox.SetResourceReference(TextBox.FontFamilyProperty, "F.Mono");
        return Card("YOUR TRIAL KEY", "Copied to your clipboard. Keep it: setup asks for it.",
            keyBox,
            Ui.Wrap(Ui.Button(_trialBusy ? "GETTING THE DOWNLOAD…" : "DOWNLOAD THE PRO ADD-ON", async () =>
                {
                    if (_trialBusy) return;
                    _trialBusy = true; RenderPage();
                    var (url, err) = await TrialOffers.ProDownloadAsync(_trialKey);
                    _trialBusy = false;
                    _trialMessage = url.Length > 0 ? "Download started in your browser. Run it when it finishes." : err;
                    if (url.Length > 0) OpenUrl(url);
                    RenderPage();
                }, "Sf.Button.Small.Primary"),
                Ui.Button("COPY KEY", () => { try { Clipboard.SetText(_trialKey); } catch (Exception) { } })),
            _trialMessage.Length > 0 ? Ui.Text(_trialMessage, 12.5, "B.Amber") : Ui.Spacer(0),
            Ui.Hint("The Pro add-on replaces this copy's program files and keeps all your notes, layouts and settings. " +
                    "When setup finishes, paste the key and click ACTIVATE. The trial starts then."));
    }

    // ---------------------------------------------------------------- buying / license

    private FrameworkElement LicenseCard()
    {
        return Card("GET PRO", null,
            Ui.Text($"Lifetime {Edition.PriceLifetime} once  ·  or  {Edition.PriceMonthly} a month", 14, "B.Amber"),
            Ui.Hint("Pay on the SFHud website. You get a license key and the Pro add-on download right away."),
            Ui.Wrap(Ui.Button($"LIFETIME · {Edition.PriceLifetime}", () => OpenUrl(Edition.BuyUrl), "Sf.Button.Small.Primary"),
                    Ui.Button($"MONTHLY · {Edition.PriceMonthly}/MO", () => OpenUrl(Edition.BuyUrl)),
                    Ui.Button("I HAVE A KEY", () => OpenUrl(Edition.ProInfoUrl))),
            Ui.Spacer(6),
            Ui.Hint("The Pro add-on replaces this copy's program files only. Your notes, layouts, settings, sounds and backups (" +
                    @"%LOCALAPPDATA%\SFHud) stay where they are and carry straight over. Setup asks for your key when it finishes."));
    }
}
