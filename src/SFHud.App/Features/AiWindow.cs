using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SFHud.Controls;
using SFHud.Interop;
using SFHud.Services;

namespace SFHud.Views;

/// <summary>AI helpers shared by the in-game AI window and the dashboard's AI page. Nothing they return is applied without the user clicking.</summary>
public static class AiTools
{
    private static string Clip(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    public static string PadText(NotePad pad, int max = 3500) => Clip(string.Join("\n\n", pad.Notes.Select(n => NoteMarkup.PlainText(n.Text))), max);

    public static Task<string?> SummarizeAsync(string text) => LocalAi.Instance.ChatAsync(
        "You summarize Garry's Mod roleplay mission briefings for a player mid-game. Reply with at most 6 short lines, each starting with '- '. " +
        "Keep orders, objectives, locations, times, callsigns and radio channels. Use **bold** for the single most important order. No intro sentence.",
        Clip(text, 3500));

    public static async Task<List<string>> SuggestNotesAsync(string context)
    {
        string? r = await LocalAi.Instance.ChatAsync(
            "From the context, suggest up to 5 short quick notes a roleplay player would want on screen (orders, reminders, tasks). " +
            "Reply JSON only: {\"notes\": [\"short note\", ...]}. Tasks start with \"- [ ] \". Use only facts from the context.",
            Clip(context, 3000), 220, json: true);
        return Strings(LocalAi.ParseJsonLoose(r)?["notes"]).Take(5).ToList();
    }

    public static readonly string[] Labels = { "ORDERS", "INTEL", "TASK", "INFO", "RULES", "PERSONAL", "TRAINING", "EVENT" };

    public static async Task<Dictionary<int, string>> CategorizeAsync(NotePad pad)
    {
        var notes = pad.Notes.Take(25).ToList();
        string list = string.Join("\n", notes.Select((n, i) => $"{i}: {Clip(NoteMarkup.PlainText(n.Text).Replace('\n', ' '), 160)}"));
        string? r = await LocalAi.Instance.ChatAsync(
            $"Give each numbered note one label from: {string.Join(", ", Labels)}. Reply JSON only: {{\"labels\": [{{\"i\": 0, \"label\": \"ORDERS\"}}, ...]}}.",
            list, 400, json: true);
        var result = new Dictionary<int, string>();
        if (LocalAi.ParseJsonLoose(r)?["labels"] is JsonArray arr)
            foreach (var x in arr.OfType<JsonObject>())
                if (int.TryParse(x["i"]?.ToString(), out int i) && i >= 0 && i < notes.Count && x["label"]?.ToString().ToUpperInvariant() is { } l && Labels.Contains(l))
                    result[i] = l;
        return result;
    }

    public static Task<string?> OrganizeAsync(NotePad pad) => LocalAi.Instance.ChatAsync(
        "Reorganize these notes into one tidy note for a roleplay player. Group by topic with '## ' headings, keep every fact, " +
        "turn to-dos into '- [ ] ' tasks, other points into '- ' bullets. No commentary.",
        PadText(pad), 600);

    public static Task<string?> AskAsync(string question, GameTarget target)
    {
        var lvl = DefconService.Instance.Active;
        var intel = IntelService.Instance;
        string context =
            $"Game: {target.Name}. Server preset: {DataStore.Preset.Name}.\nDEFCON: {(lvl is null ? "unknown" : lvl.Label + " — " + lvl.Description)}.\n" +
            $"Server: {intel.Server} {intel.Map}\n\nCurrent pad \"{Pads.Current.Title}\":\n{PadText(Pads.Current, 2000)}\n\nRecent server chat:\n{intel.RecentContext(25)}";
        return LocalAi.Instance.ChatAsync(
            "You are SFHud, a compact in-game assistant for a Garry's Mod roleplay player. Answer in at most 4 short lines. " +
            "Use only the notes and chat provided. If they don't cover the question, say so plainly.",
            context + "\n\nQuestion: " + question);
    }

    private static IEnumerable<string> Strings(JsonNode? n) =>
        n is JsonArray a ? a.Select(x => x?.ToString().Trim() ?? "").Where(s => s.Length > 0 && s != "null") : Enumerable.Empty<string>();
}

/// <summary>Compact Local AI menu that opens over the game (Ctrl+Alt+A by default).</summary>
public sealed class AiWindow : Window
{
    private readonly GameTarget _target;
    private readonly TextBlock _status = Ui.Label("");
    private readonly System.Windows.Shapes.Ellipse _dot = new() { Width = 8, Height = 8, Margin = new Thickness(0, 0, 8, 0) };
    private readonly TextBox _ask = new() { ToolTip = "Ask about your notes, the briefing or recent chat. Enter sends." };
    private readonly TextBox _input = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 60, MaxHeight = 160, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly StackPanel _result = new();
    private readonly StackPanel _settings = new() { Visibility = Visibility.Collapsed };
    private string _lastAnswer = "";

    public AiWindow(GameTarget target)
    {
        _target = target;
        Title = "SFHud Local AI";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        Width = 520;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Loaded += (_, _) => SFHud.Interop.GameScreen.CenterOnGame(this);
        SetResourceReference(FontFamilyProperty, "F.Body");
        SetResourceReference(ForegroundProperty, "B.Text");

        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10), Background = Brushes.Transparent };
        var close = Ui.IconButton("", "Close (Esc)", CloseToGame);
        var gear = Ui.IconButton("", "Model and response settings", () => { _settings.Visibility = _settings.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; BuildSettings(); });
        DockPanel.SetDock(close, Dock.Right); head.Children.Add(close);
        DockPanel.SetDock(gear, Dock.Right); head.Children.Add(gear);
        head.Children.Add(_dot);
        var t = Ui.H2("LOCAL AI", 13);
        t.Margin = new Thickness(0, 0, 10, 0);
        head.Children.Add(t);
        head.Children.Add(_status);
        head.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch (InvalidOperationException) { } };

        _input.SetResourceReference(Control.FontFamilyProperty, "F.Body");
        var actions = new WrapPanel { Margin = new Thickness(0, 6, 0, 4) };
        actions.Children.Add(Ui.Button("SUMMARIZE BRIEFING", () => Run(Summarize), tip: "Summarize the text above, or the current pad if it's empty."));
        actions.Children.Add(Ui.Button("SUGGEST NOTES", () => Run(Suggest), tip: "Quick-note ideas from the briefing / chat. You pick which to add."));
        actions.Children.Add(Ui.Button("LABEL NOTES", () => Run(Categorize), tip: "Proposes a label (ORDERS, INTEL, TASK…) for each note in the current pad."));
        actions.Children.Add(Ui.Button("ORGANIZE PAD", () => Run(Organize), tip: "Writes a tidy, grouped version as a new note. Your notes stay as they are."));
        actions.Children.Add(Ui.Button("DEFCON CHECK", () => Run(Defcon), tip: "Looks for the DEFCON level in the text above or your enabled sources. Only suggests; you confirm."));

        var askRow = new DockPanel();
        var askBtn = Ui.Button("ASK", () => Run(Ask), "Sf.Button.Small.Primary");
        askBtn.Margin = new Thickness(6, 0, 0, 0);
        DockPanel.SetDock(askBtn, Dock.Right);
        askRow.Children.Add(askBtn);
        askRow.Children.Add(_ask);
        _ask.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Run(Ask); e.Handled = true; } };

        var sp = new StackPanel();
        sp.Children.Add(head);
        sp.Children.Add(Ui.Label($"TEXT TO WORK ON  ·  EMPTY = CURRENT PAD \"{Pads.Current.Title.ToUpperInvariant()}\""));
        sp.Children.Add(_input);
        sp.Children.Add(actions);
        sp.Children.Add(askRow);
        sp.Children.Add(Ui.Spacer(8));
        sp.Children.Add(_result);
        sp.Children.Add(_settings);

        var frame = new HudFrame { Content = sp, Chamfer = 14, ShowTab = true, Glow = true, Padding = new Thickness(18, 14, 16, 14), Margin = new Thickness(10) };
        frame.SetResourceReference(HudFrame.FillProperty, "B.Bg");
        frame.SetResourceReference(HudFrame.StrokeProperty, "B.Accent");
        Content = frame;

        LocalAi.Instance.PropertyChanged += OnAi;
        Closed += (_, _) => LocalAi.Instance.PropertyChanged -= OnAi;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { CloseToGame(); e.Handled = true; } };
        Loaded += (_, _) => { Activate(); _ask.Focus(); LocalAi.Instance.Preload(); RefreshStatus(); if (!LocalAi.Instance.CanRun) Show("The AI isn't installed yet. Open SFHud › Local AI to install it (837 MB with its runtime, runs offline), or point it at LM Studio / Ollama in the settings (gear)."); };
    }

    private void OnAi(object? s, System.ComponentModel.PropertyChangedEventArgs e) => Dispatcher.BeginInvoke(RefreshStatus);

    private void RefreshStatus()
    {
        var ai = LocalAi.Instance;
        _status.Text = ai.StatusText;
        _dot.Fill = ThemeManager.Brush(ai.State switch { AiState.Ready or AiState.Busy => "B.Ok", AiState.Error => "B.Alert", AiState.Stopped => "B.Accent", _ => "B.Amber" });
    }

    private void CloseToGame()
    {
        Close();
        GameLocator.FocusTarget(_target);
    }

    private string Source() => _input.Text.Trim().Length > 0 ? _input.Text.Trim() : AiTools.PadText(Pads.Current);

    private async void Run(Func<Task> f)
    {
        if (!LocalAi.Instance.CanRun) { Show("Install the AI first (SFHud › AI), or set an external endpoint in the gear menu."); return; }
        Show(LocalAi.Instance.IsRunning ? "Working…" : "Loading the model (a few seconds the first time)…");
        try { await f(); }
        catch (Exception ex) { Show("Failed: " + ex.Message); }
        RefreshStatus();
    }

    private void Show(string text, IEnumerable<UIElement>? extra = null)
    {
        _result.Children.Clear();
        var box = new Border { Padding = new Thickness(12, 9, 12, 10), CornerRadius = new CornerRadius(6) };
        box.SetResourceReference(Border.BackgroundProperty, "B.Accent.Fill");
        var inner = new StackPanel();
        inner.Children.Add(NoteMarkup.Render(text, new MarkupOptions { FontSize = 13, Foreground = ThemeManager.Brush("B.Text"), Accent = ThemeManager.Brush("B.Accent"), Dim = ThemeManager.Brush("B.Dim"), FontFamily = (FontFamily)Application.Current.Resources["F.Body"] }));
        if (extra is not null) foreach (var x in extra) inner.Children.Add(x);
        box.Child = inner;
        _result.Children.Add(box);
    }

    private UIElement AnswerButtons(string text) => Ui.HStack(
        Ui.Button("ADD AS NOTE", () => { Pads.Add(Pads.Current, text); Show($"Added to \"{Pads.Current.Title}\"."); }, "Sf.Button.Small.Primary"),
        Ui.Button("COPY", () => Clipboard.SetText(text)));

    private async Task Summarize()
    {
        string? r = await AiTools.SummarizeAsync(Source());
        if (r is null) { Show("No answer · " + LocalAi.Instance.LastError); return; }
        _lastAnswer = "## Briefing summary\n" + r;
        Show(_lastAnswer, new[] { Ui.Spacer(6), AnswerButtons(_lastAnswer) });
    }

    private async Task Suggest()
    {
        string ctx = Source() + "\n\nRecent chat:\n" + IntelService.Instance.RecentContext(20);
        var list = await AiTools.SuggestNotesAsync(ctx);
        if (list.Count == 0) { Show("No suggestions · " + LocalAi.Instance.LastError); return; }
        var rows = new List<UIElement> { Ui.Spacer(4) };
        foreach (var s in list)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            var add = Ui.Button("ADD", () => { }, "Sf.Button.Small");
            string text = s;
            add.Click += (_, _) => { Pads.Add(Pads.Current, text); add.IsEnabled = false; add.Content = "ADDED"; };
            DockPanel.SetDock(add, Dock.Right);
            row.Children.Add(add);
            row.Children.Add(Ui.Text(s, 12.5));
            rows.Add(row);
        }
        rows.Add(Ui.Button("ADD ALL", () => { foreach (var s in Enumerable.Reverse(list)) Pads.Add(Pads.Current, s); Show($"Added {list.Count} notes to \"{Pads.Current.Title}\"."); }, "Sf.Button.Small.Primary"));
        Show($"Suggestions for \"{Pads.Current.Title}\":", rows);
    }

    private async Task Categorize()
    {
        var pad = Pads.Current;
        if (pad.Notes.Count == 0) { Show("This pad is empty."); return; }
        var labels = await AiTools.CategorizeAsync(pad);
        if (labels.Count == 0) { Show("Couldn't label these notes · " + LocalAi.Instance.LastError); return; }
        var notes = pad.Notes.Take(25).ToList();
        string preview = string.Join("\n", labels.OrderBy(k => k.Key).Select(k => $"- **{k.Value}** · {ClipText(notes[k.Key].FirstLine, 60)}"));
        Show(preview, new UIElement[]
        {
            Ui.Spacer(6),
            Ui.Button($"APPLY {labels.Count} LABELS", () => { foreach (var (i, l) in labels) notes[i].Label = l; DataStore.NotifyNotes(); Show("Labels applied. Change any of them from the note's menu."); }, "Sf.Button.Small.Primary"),
        });
    }

    private async Task Organize()
    {
        string? r = await AiTools.OrganizeAsync(Pads.Current);
        if (r is null) { Show("No answer · " + LocalAi.Instance.LastError); return; }
        _lastAnswer = r;
        Show(r, new[] { Ui.Spacer(6), AnswerButtons(r) });
    }

    private async Task Defcon()
    {
        string res = _input.Text.Trim().Length > 0 ? await DefconService.Instance.AnalyzeTextAsync(_input.Text) : await DefconService.Instance.ScanNowAsync();
        var p = DefconService.Instance.Pending;
        if (p is null) { Show(res); return; }
        Show($"{res}\n\n**{p.Level.Label}** · {p.Source} · {p.ConfidenceText} sure{(p.Evidence.Length > 0 ? "\n> " + p.Evidence : "")}", new UIElement[]
        {
            Ui.Spacer(6),
            Ui.HStack(Ui.Button("ACCEPT", () => { DefconService.Instance.Accept(); Show($"DEFCON set to {p.Level.Label}."); }, "Sf.Button.Small.Primary"),
                      Ui.Button("DISMISS", () => { DefconService.Instance.Dismiss(); Show("Dismissed."); })),
        });
    }

    private async Task Ask()
    {
        string q = _ask.Text.Trim();
        if (q.Length == 0) return;
        string? r = await AiTools.AskAsync(q, _target);
        if (r is null) { Show("No answer · " + LocalAi.Instance.LastError); return; }
        Show(r, new[] { Ui.Spacer(6), AnswerButtons(r) });
    }

    private void BuildSettings()
    {
        _settings.Children.Clear();
        if (_settings.Visibility != Visibility.Visible) return;
        _settings.Children.Add(AiSettings.Build(this));
    }

    private static string ClipText(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}

/// <summary>Model / response settings block (AI window and dashboard).</summary>
public static class AiSettings
{
    public static FrameworkElement Build(Window? owner, bool showLoad = true)
    {
        var s = DataStore.Settings;
        var ai = LocalAi.Instance;
        var sp = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        sp.Children.Add(Ui.H2("MODEL", 11));
        sp.Children.Add(Ui.Hint(ai.UsingExternal ? $"Using your server at {s.AiEndpoint}." : ai.IsInstalled ? "Built-in: Qwen3.5-0.8B on llama.cpp (CPU, offline)." : "Built-in model not installed. Install it on the SFHud window's AI page."));
        var run = Ui.Button(ai.IsRunning ? "UNLOAD" : "LOAD NOW", async () => { if (ai.IsRunning || ai.State == AiState.Starting) ai.Unload(); else await ai.LoadAsync(); });
        run.IsEnabled = ai.CanRun;
        if (!showLoad) run.Visibility = Visibility.Collapsed;
        sp.Children.Add(run);
        sp.Children.Add(Ui.Row("External endpoint", Ui.TextBox(s.AiEndpoint, v => { if (v.Trim() == s.AiEndpoint) return; ai.Stop(); s.AiEndpoint = v.Trim(); ai.RefreshInstalled(); DataStore.MarkDirty(); }),
            "Optional: an OpenAI-compatible server on this PC, e.g. http://127.0.0.1:1234 (LM Studio) or http://127.0.0.1:11434 (Ollama). Loopback only."));
        sp.Children.Add(Ui.Row("External model", Ui.TextBox(s.AiExternalModel, v => { s.AiExternalModel = v.Trim(); DataStore.MarkDirty(); }), "Model name on that server (empty = its default)."));
        sp.Children.Add(Ui.H2("RESPONSES", 11));
        sp.Children.Add(Ui.Slider("Max answer length", 60, 1000, s.AiMaxTokens, v => $"{v:0} tokens", v => { s.AiMaxTokens = (int)v; DataStore.MarkDirty(); }, "Longer answers take longer on CPU.", 10));
        sp.Children.Add(Ui.Slider("Creativity", 0, 1.2, s.AiTemperature, v => $"{v:0.00}", v => { s.AiTemperature = Math.Round(v, 2); DataStore.MarkDirty(); }, "Low = factual and repeatable. Structured tasks always use a low value.", 0.05));
        sp.Children.Add(Ui.Slider("CPU threads", 0, Environment.ProcessorCount, s.AiThreads, v => v < 1 ? $"AUTO ({LocalAi.AutoThreads})" : $"{v:0} / {Environment.ProcessorCount}", v => { s.AiThreads = (int)v; DataStore.MarkDirty(); }, "Applies the next time the model loads. AUTO is the fastest setting for this model; more threads only take frames from the game.", 1));
        sp.Children.Add(Ui.Slider("Unload after idle", 1, 60, s.AiIdleMinutes, v => $"{v:0} min", v => { s.AiIdleMinutes = (int)v; DataStore.MarkDirty(); }, "Frees about 1 GB of RAM when you're not using it.", 1));
        sp.Children.Add(Ui.Switch("Summarize server chat in the background", s.IntelAiEnabled, v => { s.IntelAiEnabled = v; DataStore.MarkDirty(); }, "Off by default to keep CPU free while playing. At most once every 30 s when on."));
        return sp;
    }
}
