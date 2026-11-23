using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using SFHud.Controls;
using SFHud.Services;

namespace SFHud.Views;

/// <summary>Settings › Chat alerts: the server's chat format (with a live tester) and the rules to watch for.</summary>
public partial class LauncherWindow
{
    private string _chatSample = "[Admin] Commander Rex: EVENT in 10 MINUTES, everyone to the hangar!";

    private FrameworkElement ChatAlertsPanel()
    {
        if (!Edition.Has(ProFeature.ChatAlerts))
            return Ui.ProPanel(ProFeature.ChatAlerts,
                "Pick the roles and phrases to look after (e.g. staff saying \"EVENT in 10 MINUTES\"). SFHud reads server chat from console.log and shows " +
                "\"EVENT · STARTING IN 10 MIN\" on screen for a moment, with a countdown timer. Set your server's chat layout so roles and names are read exactly.");

        var pre = DataStore.Preset;
        var s = S;
        var sp = new StackPanel();
        sp.Children.Add(Ui.Switch("Chat alerts on", s.ChatAlertsEnabled, v => { s.ChatAlertsEnabled = v; DataStore.MarkDirty(); },
            "Needs console.log (Garry's Mod started with -condebug), see Server intel above. Alerts show as toasts on the overlay."));
        if (!s.IntelEnabled) sp.Children.Add(Ui.Text("Server intel (console.log) is off, so there's no chat to read. Turn it on above.", 12, "B.Amber"));

        // ---- chat format + tester
        var result = new StackPanel { Margin = new Thickness(0, 4, 0, 6) };
        void Test()
        {
            result.Children.Clear();
            ChatWatch.FormatToRegex(pre.ChatFormat, out string err);
            if (err.Length > 0) { result.Children.Add(Ui.Text(err, 12, "B.Alert")); return; }
            if (ChatWatch.Parse(_chatSample, pre.ChatFormat) is not { } c) { result.Children.Add(Ui.Text("That line doesn't fit the format, so it would be ignored.", 12, "B.Amber")); return; }
            result.Children.Add(Ui.Label($"ROLE  {(c.Role.Length > 0 ? c.Role : "—")}   ·   NAME  {(c.Name.Length > 0 ? c.Name : "—")}"));
            result.Children.Add(Ui.Text("MESSAGE  " + c.Message, 12, "B.Text"));
            var hit = pre.ChatRules.FirstOrDefault(r => ChatWatch.Matches(r, c));
            if (hit is null) result.Children.Add(Ui.Text("No rule matches this line: nothing would show.", 12, "B.Dim"));
            else
            {
                var (title, text) = ChatWatch.Render(hit, c);
                result.Children.Add(Ui.Text($"Would show:  {title}  —  {text}" + (hit.Countdown && ChatWatch.Minutes(c.Message) is int n ? $"   (+ {n}-minute countdown)" : ""), 12, "B.Ok"));
            }
        }
        var format = Ui.TextBox(pre.ChatFormat, v => { pre.ChatFormat = v; DataStore.MarkDirty(); Test(); }, live: true);
        var sample = Ui.TextBox(_chatSample, v => { _chatSample = v; Test(); }, live: true);
        Test();
        sp.Children.Add(Ui.H2("CHAT LAYOUT", 11));
        sp.Children.Add(Ui.Hint("How your server writes a chat line. Copy one line from console.log, then describe it with {role} {name} {message} ({channel} counts as a role, {any} skips text). " +
                                "Leave empty to detect the common layouts: [Role] Name: msg · (Channel) Name: msg · *DEAD* Name: msg · Name: msg."));
        sp.Children.Add(Ui.Row("Layout", format, "Examples: [{role}] {name}: {message}   ·   ({channel}) {name}: {message}   ·   {name} [{role}]: {message}"));
        sp.Children.Add(Ui.Row("Test with a line", sample));
        sp.Children.Add(result);
        sp.Children.Add(Ui.Wrap(
            Ui.Button("[ROLE] NAME: MSG", () => { pre.ChatFormat = "[{role}] {name}: {message}"; DataStore.MarkDirty(); RenderPage(); }),
            Ui.Button("(CHANNEL) NAME: MSG", () => { pre.ChatFormat = "({channel}) {name}: {message}"; DataStore.MarkDirty(); RenderPage(); }),
            Ui.Button("NAME [ROLE]: MSG", () => { pre.ChatFormat = "{name} [{role}]: {message}"; DataStore.MarkDirty(); RenderPage(); }),
            Ui.Button("AUTO-DETECT", () => { pre.ChatFormat = ""; DataStore.MarkDirty(); RenderPage(); })));

        // ---- rules
        sp.Children.Add(Ui.Spacer(6));
        sp.Children.Add(Ui.H2($"WHAT TO WATCH FOR · {pre.Name.ToUpperInvariant()}", 11));
        sp.Children.Add(Ui.Hint("First matching rule wins. Roles and names: comma-separated, any one counts. Phrases: any one counts; empty = every message from those people. " +
                                "In the title, {n} is the minutes found (\"in 10 min\"); in the text, {message} {name} {role}."));
        foreach (var rule in pre.ChatRules)
        {
            var r = rule;
            var body = new StackPanel();
            void C() { DataStore.MarkDirty(); Test(); }
            body.Children.Add(Ui.Switch("On", r.Enabled, v => { r.Enabled = v; C(); }));
            body.Children.Add(Ui.Row("Name", Ui.TextBox(r.Name, v => { r.Name = v.Trim(); C(); })));
            body.Children.Add(Ui.Row("Roles", Ui.TextBox(r.Roles, v => { r.Roles = v; C(); }), "e.g. admin, event team, CO"));
            body.Children.Add(Ui.Row("Names", Ui.TextBox(r.Names, v => { r.Names = v; C(); }), "Specific players, e.g. your CO's name"));
            body.Children.Add(Ui.Row("Phrases", Ui.TextBox(r.Phrases, v => { r.Phrases = v; C(); }), "e.g. event, starting, deploy, tryouts"));
            body.Children.Add(Ui.Row("On-screen title", Ui.TextBox(r.Title, v => { r.Title = v; C(); })));
            body.Children.Add(Ui.Row("On-screen text", Ui.TextBox(r.Text, v => { r.Text = v; C(); })));
            body.Children.Add(Ui.Switch("Start a countdown when a time is given (\"in 10 minutes\")", r.Countdown, v => { r.Countdown = v; C(); }));
            body.Children.Add(Ui.Switch("Urgent (amber, stays longer)", r.Urgent, v => { r.Urgent = v; C(); }));
            body.Children.Add(Ui.Row("Sound", SoundCombo(r.Sound, v => { r.Sound = v; DataStore.MarkDirty(); }, allowDefault: true)));
            body.Children.Add(Ui.Wrap(
                Ui.Button("MOVE UP", () => { Move(pre.ChatRules, r, -1); DataStore.MarkDirty(); RenderPage(); }),
                Ui.Button("REMOVE", () => { pre.ChatRules.Remove(r); DataStore.MarkDirty(); RenderPage(); }, "Sf.Button.Small.Danger")));
            sp.Children.Add(new Expander { Header = Ui.Text((r.Enabled ? "● " : "○ ") + r.Name, 12.5, r.Enabled ? "B.Text" : "B.Muted", wrap: false), Content = body, Margin = new Thickness(0, 0, 0, 4) });
        }
        sp.Children.Add(Ui.Wrap(
            Ui.Button("+ ADD RULE", () => { pre.ChatRules.Add(new ChatRule { Name = "New rule", Title = "HEADS UP", Phrases = "" }); DataStore.MarkDirty(); RenderPage(); }, "Sf.Button.Small.Primary"),
            Ui.Button("RESET TO DEFAULTS", () => { if (!Ui.Confirm(this, "Reset chat rules", "Replace this preset's chat rules with the defaults (Events, Staff announcements)?", "RESET")) return; pre.ChatRules = ChatRule.Defaults(); DataStore.MarkDirty(); RenderPage(); })));
        sp.Children.Add(Ui.Spacer(6));
        sp.Children.Add(Ui.Switch("Let the local AI spot upcoming events in chat", s.ChatEventsAi, v => { s.ChatEventsAi = v; DataStore.MarkDirty(); },
            "Chat no rule caught that mentions an event, training, deployment, restart or a time goes to the local AI (one line every 8 s at most, on your PC). If it's an upcoming event you get a toast, plus a countdown when it says when. Needs the Local AI installed."));
        return sp;
    }
}
