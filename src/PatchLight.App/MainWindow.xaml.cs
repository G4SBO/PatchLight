using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using PatchLight.Core;
using PatchLight.Windows;
using Windows.Graphics;
using Windows.UI;

namespace PatchLight.App;

public sealed partial class MainWindow : Window
{
    private static readonly Color RedColor = Color.FromArgb(255, 239, 68, 68);
    private static readonly Color YellowColor = Color.FromArgb(255, 250, 204, 21);
    private static readonly Color GreenColor = Color.FromArgb(255, 34, 197, 94);
    private static readonly Color OffColor = Color.FromArgb(255, 55, 65, 81);

    private readonly AppController controller;
    private int settingsVersion;

    public bool AllowClose { get; set; }

    // Set by the app when it can update itself (not in demo mode).
    public Func<Task<AppUpdateCheck>>? CheckForAppUpdate { get; set; }
    public Func<AppRelease, Task<string?>>? InstallAppUpdate { get; set; }

    public MainWindow(AppController controller)
    {
        this.controller = controller;
        InitializeComponent();

        // Mica is Windows 11 only; Windows 10 gets acrylic, which it supports, instead of a flat fallback.
        if (!Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported())
            SystemBackdrop = new DesktopAcrylicBackdrop();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(1040 * scale), (int)(720 * scale)));

        // Closing keeps PatchLight running in the tray; "Exit" in the tray menu really quits.
        AppWindow.Closing += (sender, args) =>
        {
            if (AllowClose) return;
            args.Cancel = true;
            sender.Hide();
        };

        controller.Changed += () => DispatcherQueue.TryEnqueue(Refresh);
        Refresh();
    }

    public void ShowAndFocus()
    {
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        Activate();
    }

    private void Refresh()
    {
        if (controller.IsDemo) TitleText.Text = "PatchLight — demo (made-up updates)";

        var light = controller.Light;
        RedLamp.Fill = Brush(light == Light.Red ? RedColor : OffColor);
        YellowLamp.Fill = Brush(light == Light.Yellow ? YellowColor : OffColor);
        GreenLamp.Fill = Brush(light == Light.Green ? GreenColor : OffColor);

        HeadlineText.Text = controller.Headline;
        DetailText.Text = controller.Detail;
        CheckedText.Text = controller.CheckedText;
        Busy.IsActive = controller.IsChecking;
        CheckButton.IsEnabled = !controller.IsChecking;

        if (Pages.SelectedItem == UpdatesPage) ShowUpdates();
    }

    private async void OnCheckClick(object sender, RoutedEventArgs e) => await controller.CheckAsync();

    // canInstall: false for copies from the zip, which get a link to the download page instead.
    public void ShowAppUpdate(AppRelease release, bool canInstall)
    {
        var version = release.Version.ToString(3);
        var button = new Button { Content = canInstall ? "Update now" : "Download", Style = Style("AccentButtonStyle") };
        button.Click += async (_, _) =>
        {
            if (!canInstall || InstallAppUpdate is null)
            {
                await global::Windows.System.Launcher.LaunchUriAsync(new Uri(release.PageUrl));
                return;
            }
            button.IsEnabled = false;
            button.Content = "Downloading...";
            var error = await InstallAppUpdate(release);
            if (error is null) return; // PatchLight is closing so the installer can run.
            button.Content = "Update now";
            button.IsEnabled = true;
            Show(InfoBarSeverity.Warning, error);
        };

        UpdateBar.Title = $"PatchLight {version} is available";
        UpdateBar.Message = canInstall
            ? "Windows asks for permission, then PatchLight closes, updates and opens again. Your settings stay."
            : "This copy came from the zip, so download the new version from GitHub.";
        UpdateBar.ActionButton = button;
        UpdateBar.Content = new HyperlinkButton { Content = "What's new", NavigateUri = new Uri(release.PageUrl), Padding = new Thickness(0, 0, 0, 8) };
        UpdateBar.IsOpen = true;
    }

    private void OnPageChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        Message.IsOpen = false;
        if (sender.SelectedItem == UpdatesPage) ShowUpdates();
        else if (sender.SelectedItem == HistoryPage) _ = ShowHistoryAsync();
        else _ = ShowSettingsAsync();
    }

    // ---- Updates ----

    private void ShowUpdates()
    {
        PageContent.Children.Clear();
        if (controller.Result is null)
        {
            if (controller.Error is not null) PageContent.Children.Add(Note(controller.Error));
            return;
        }
        if (controller.Cards.Count == 0)
            PageContent.Children.Add(Note("Nothing is waiting to install. You're up to date."));
        foreach (var note in StatusSummary.WaitingNotes(controller.Result)) PageContent.Children.Add(Note(note));
        foreach (var card in controller.Cards) PageContent.Children.Add(UpdateCardView(card));
    }

    private UIElement UpdateCardView(UpdateCard card)
    {
        var body = new StackPanel { Spacing = 6 };

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        header.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = 12, Height = 12, Fill = Brush(LightColor(card.Light)), VerticalAlignment = VerticalAlignment.Center,
        });
        header.Children.Add(new TextBlock
        {
            Text = card.Badge, FontWeight = FontWeights.SemiBold, Foreground = Brush(LightColor(card.Light)),
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (card.PausedText is not null)
            header.Children.Add(new TextBlock
            {
                Text = "· " + card.PausedText, Foreground = Secondary(), VerticalAlignment = VerticalAlignment.Center,
            });
        body.Children.Add(header);

        body.Children.Add(new TextBlock { Text = card.Title, Style = Style("BodyStrongTextBlockStyle"), TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new TextBlock { Text = card.Reason, TextWrapping = TextWrapping.Wrap, Foreground = Secondary() });

        foreach (var issue in card.Issues)
        {
            var line = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
            line.Inlines.Add(new Run { Text = "• " + issue.Title + " " });
            line.Inlines.Add(new Run { Text = $"({StatusText(issue.Status)}) ", Foreground = Secondary() });
            var link = new Hyperlink { NavigateUri = new Uri(issue.Url) };
            link.Inlines.Add(new Run { Text = "Read Microsoft's note" });
            line.Inlines.Add(link);
            body.Children.Add(line);
        }

        if (card.News.Count > 0)
        {
            body.Children.Add(new TextBlock
            {
                Text = "In the news (not confirmed by Microsoft yet):", Margin = new Thickness(0, 4, 0, 0),
                TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold,
            });
            foreach (var article in card.News.Take(3))
            {
                var line = new TextBlock { TextWrapping = TextWrapping.Wrap };
                line.Inlines.Add(new Run { Text = "• " + article.Title + " " });
                var link = new Hyperlink { NavigateUri = new Uri(article.Url) };
                link.Inlines.Add(new Run { Text = article.Source });
                line.Inlines.Add(link);
                body.Children.Add(line);
            }
        }

        if (card.SetAside.Count > 0)
        {
            body.Children.Add(new TextBlock
            {
                Text = "Not counted, because these usually only affect business PCs:", Foreground = Secondary(),
                Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap,
            });
            foreach (var issue in card.SetAside) body.Children.Add(IssueLine(issue, Secondary()));
        }

        if (card.FixCount > 0)
            body.Children.Add(new TextBlock
            {
                Text = card.FixCount == 1 ? "Fixes 1 known problem." : $"Fixes {card.FixCount} known problems.",
                Foreground = Brush(GreenColor),
            });

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 6, 0, 0) };
        if (card.CanPause)
        {
            var pause = new Button
            {
                Content = $"Pause for {controller.State.PauseDays} days",
                Style = card.Light == Light.Red ? Style("AccentButtonStyle") : null,
            };
            pause.Click += async (_, _) => await RunCardAction(pause, () => controller.PauseAsync(card),
                $"Paused {card.PauseKey}. The automatic check resumes it once Microsoft fixes it, and by " +
                $"{DateTime.Now.AddDays(controller.State.PauseDays):MMM d} at the latest.");
            actions.Children.Add(pause);
        }
        if (card.CanResume)
        {
            var resume = new Button { Content = "Resume" };
            resume.Click += async (_, _) => await RunCardAction(resume, () => controller.ResumeAsync(card),
                $"Resumed {card.PauseKey}. Windows Update will install it.");
            actions.Children.Add(resume);
        }
        if (actions.Children.Count > 0) body.Children.Add(actions);

        return Card(body);
    }

    private async Task RunCardAction(Button button, Func<Task<ActionResult>> action, string success)
    {
        button.IsEnabled = false;
        var result = await action();
        button.IsEnabled = true;
        // A warning means it worked but something around it needs the person's attention.
        Show(result.Succeeded && result.Message is null ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
            result.Message ?? success);
    }

    // ---- History ----

    private async Task ShowHistoryAsync()
    {
        PageContent.Children.Clear();
        PageContent.Children.Add(Note("Reading update history..."));
        var (items, error) = await controller.HistoryAsync();
        if (Pages.SelectedItem != HistoryPage) return;

        PageContent.Children.Clear();
        PageContent.Children.Add(new TextBlock
        {
            Text = "Updates installed in the last 60 days. If something broke, the likely cause is marked.",
            TextWrapping = TextWrapping.Wrap, Foreground = Secondary(),
        });
        if (error is not null)
        {
            PageContent.Children.Add(Note(error));
            return;
        }
        if (items.Count == 0) PageContent.Children.Add(Note("No Windows updates were installed in this period."));

        foreach (var item in items)
        {
            var body = new StackPanel { Spacing = 4 };
            body.Children.Add(new TextBlock
            {
                Text = item.Update.InstalledOn.ToLocalTime().ToString("MMM d, yyyy"), Foreground = Secondary(),
                Style = Style("CaptionTextBlockStyle"),
            });
            body.Children.Add(new TextBlock { Text = item.Update.Title, TextWrapping = TextWrapping.Wrap });
            if (item.Issues.Count == 0)
            {
                body.Children.Add(new TextBlock { Text = "No known problems.", Foreground = Brush(GreenColor) });
            }
            else
            {
                var counted = controller.CountBusinessIssues ? item.Issues : item.Issues.Where(i => !i.LikelyBusinessOnly).ToList();
                var aside = item.Issues.Except(counted).ToList();
                if (counted.Count > 0)
                {
                    body.Children.Add(new TextBlock { Text = "Likely cause of:", Foreground = Brush(RedColor), FontWeight = FontWeights.SemiBold });
                    foreach (var issue in counted) body.Children.Add(IssueLine(issue, null));
                }
                if (aside.Count > 0)
                {
                    body.Children.Add(new TextBlock
                    {
                        Text = "Linked to problems that usually only affect business PCs:", Foreground = Secondary(),
                        TextWrapping = TextWrapping.Wrap,
                    });
                    foreach (var issue in aside) body.Children.Add(IssueLine(issue, Secondary()));
                }
                var removed = item.Update.Kb is { } k && controller.Result?.ActiveHolds.Any(h => h.Kb == k && h.IsWaiting) == true;
                if (removed)
                    body.Children.Add(new TextBlock
                    {
                        Text = "You removed this update. Restart your PC if you haven't yet.", Foreground = Secondary(),
                        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
                    });
                else if (counted.Count > 0 && item.Update.Kb is { } kb)
                {
                    var remove = new Button { Content = "Uninstall this update", Margin = new Thickness(0, 6, 0, 0) };
                    remove.Click += async (_, _) => await UninstallAsync(remove, kb);
                    body.Children.Add(remove);
                }
            }
            PageContent.Children.Add(Card(body));
        }
    }

    private bool uninstalling;

    private async Task UninstallAsync(Button button, string kb)
    {
        if (uninstalling)
        {
            Show(InfoBarSeverity.Informational, "Another update is being removed. Wait for it to finish.");
            return;
        }
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = $"Uninstall {kb}?",
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = "This also removes the security fixes that came with it, so only do it if this problem really affects you.\n\n" +
                    $"When Windows offers {kb} again, PatchLight pauses it until Microsoft fixes the problem (up to " +
                    $"{controller.State.PauseDays} days), then lets Windows install it. Windows might reinstall it before " +
                    "PatchLight gets the chance, so open PatchLight after restarting.\n\n" +
                    "Windows asks for permission, it can take a few minutes, and your PC needs to restart to finish.",
            },
            PrimaryButtonText = "Uninstall",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        button.IsEnabled = false;
        button.Content = "Uninstalling...";
        uninstalling = true;
        ActionResult result;
        try
        {
            result = await controller.UninstallAsync(kb);
        }
        finally
        {
            uninstalling = false;
        }
        button.Content = "Uninstall this update";
        button.IsEnabled = !result.Succeeded;
        if (!result.Succeeded)
        {
            Show(InfoBarSeverity.Warning, result.Message!);
            return;
        }
        var restart = new Button { Content = "Restart now" };
        restart.Click += (_, _) =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    Path.Combine(Environment.SystemDirectory, "shutdown.exe"), "/r /t 0") { CreateNoWindow = true, UseShellExecute = false });
            }
            catch (Exception)
            {
                Show(InfoBarSeverity.Warning, "Couldn't restart. Restart from the Start menu.");
            }
        };
        Show(result.Message is null ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
            result.Message ?? $"Removed {kb}. Restart your PC to finish. Save your work first.", restart);
    }

    private static TextBlock IssueLine(KnownIssue issue, Brush? color)
    {
        var line = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var title = new Run { Text = "• " + issue.Title + " " };
        if (color is not null) title.Foreground = color;
        line.Inlines.Add(title);
        var link = new Hyperlink { NavigateUri = new Uri(issue.Url) };
        link.Inlines.Add(new Run { Text = "Read Microsoft's note" });
        line.Inlines.Add(link);
        return line;
    }

    // ---- Settings ----

    private async Task ShowSettingsAsync()
    {
        PageContent.Children.Clear();
        // Switching tabs while Task Scheduler is still answering must not let the old page touch the new one.
        var version = ++settingsVersion;

        var startWithWindows = new ToggleSwitch { Header = "Start with Windows", IsOn = controller.StartWithWindows };
        startWithWindows.Toggled += (_, _) => controller.StartWithWindows = startWithWindows.IsOn;
        PageContent.Children.Add(Card(Section(startWithWindows,
            "PatchLight waits quietly in the tray, checks every hour and tells you when an update has known problems.")));

        var daily = new ToggleSwitch { Header = "Automatic check (every hour)", IsEnabled = false };
        var dailyText = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Secondary() };
        var dailyPanel = Section(daily,
            "Every hour, even when PatchLight is closed, Windows runs a quick check that resumes paused updates " +
            "Microsoft has fixed, ends pauses that ran out and applies your protection settings. Pausing an update " +
            "turns it on. Turning it on or off here asks for administrator permission.");
        dailyPanel.Children.Add(dailyText);
        PageContent.Children.Add(Card(dailyPanel));

        var protectionCard = ProtectionCard(async () =>
        {
            var now = await controller.GetDailyCheckAsync();
            if (version != settingsVersion) return;
            dailyShowingState = true;
            daily.IsOn = now == DailyCheckState.On;
            dailyShowingState = false;
            dailyText.Text = DailyText(now);
        });
        PageContent.Children.Add(protectionCard);

        var business = new ToggleSwitch { Header = "Count problems that usually only affect business PCs", IsOn = controller.CountBusinessIssues };
        business.Toggled += async (_, _) => await controller.SetCountBusinessIssuesAsync(business.IsOn);
        PageContent.Children.Add(Card(Section(business,
            "Some of Microsoft's known problems only hit PCs run by a company: joined to a domain, virtual desktops, " +
            "servers or IT-managed devices. On a home PC they're still listed but don't change the light. " +
            "Turn this on if this PC belongs to a company or school.")));

        var news = new ToggleSwitch { Header = "Check tech news for reported problems", IsOn = controller.State.CheckNews };
        news.Toggled += async (_, _) => await controller.SetCheckNewsAsync(news.IsOn);
        PageContent.Children.Add(Card(Section(news,
            "Problems often show up on tech news sites (Neowin, Windows Latest) days before Microsoft lists them. " +
            "PatchLight reads their public news feeds; a report can turn a safe update into \"wait\", never into \"problems\". " +
            "Nothing about you or your PC is sent.")));

        var days = new NumberBox
        {
            Header = "Pause updates for (days)", Minimum = 1, Maximum = HoldPolicy.MaxDays, Value = controller.State.PauseDays,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, Width = 220, HorizontalAlignment = HorizontalAlignment.Left,
        };
        days.ValueChanged += (_, args) =>
        {
            if (!double.IsNaN(args.NewValue)) controller.SetPauseDays((int)args.NewValue);
        };
        PageContent.Children.Add(Card(Section(days,
            $"How long a pause lasts at most. PatchLight resumes it earlier if Microsoft fixes the problem. Up to {HoldPolicy.MaxDays} days, because security updates matter.")));

        var about = new StackPanel { Spacing = 4 };
        about.Children.Add(new TextBlock { Text = $"PatchLight {AppVersion()}", Style = Style("BodyStrongTextBlockStyle") });
        about.Children.Add(new TextBlock
        {
            Text = "Free and open source. Data comes straight from Microsoft's public Windows release health pages. No account, no server, no tracking.",
            TextWrapping = TextWrapping.Wrap, Foreground = Secondary(),
        });
        about.Children.Add(new HyperlinkButton { Content = "Made by G4SBO", NavigateUri = new Uri(PatchLightHost.AuthorUrl), Padding = new Thickness(0) });
        about.Children.Add(new HyperlinkButton { Content = "Project page on GitHub", NavigateUri = new Uri(PatchLightHost.ProjectUrl), Padding = new Thickness(0) });
        if (CheckForAppUpdate is { } checkForUpdate)
        {
            var check = new Button { Content = "Check for updates", Margin = new Thickness(0, 8, 0, 0) };
            check.Click += async (_, _) =>
            {
                check.IsEnabled = false;
                var result = await checkForUpdate();
                check.IsEnabled = true;
                if (!result.Reached) Show(InfoBarSeverity.Warning, "Couldn't reach GitHub to check. Try again later.");
                else if (result.Newer is null) Show(InfoBarSeverity.Success, $"You have the latest version ({AppVersion()}).");
            };
            about.Children.Add(check);
        }
        PageContent.Children.Add(Card(about));

        var state = await controller.GetDailyCheckAsync();
        if (version != settingsVersion) return;
        daily.IsOn = state == DailyCheckState.On;
        dailyText.Text = DailyText(state);
        daily.IsEnabled = true;

        daily.Toggled += async (_, _) =>
        {
            if (dailyShowingState) return;
            daily.IsEnabled = false;
            var result = await controller.SetDailyCheckAsync(daily.IsOn);
            var now = await controller.GetDailyCheckAsync();
            dailyShowingState = true;
            daily.IsOn = now == DailyCheckState.On;
            dailyShowingState = false;
            dailyText.Text = DailyText(now);
            daily.IsEnabled = true;
            if (!result.Succeeded) Show(InfoBarSeverity.Warning, result.Message!);
        };
    }

    // Set while the page shows a state it read back, so the toggle doesn't act on its own change.
    private bool dailyShowingState;

    private static readonly int[] WaitChoices = ProtectionSettings.WaitChoices;

    // Auto-pause and the wait setting. Each change asks for administrator permission once, and turns on the
    // automatic check that does the work. onChanged refreshes the automatic check's switch.
    private Border ProtectionCard(Func<Task> onChanged)
    {
        var settings = controller.Protection;
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock { Text = "Protection", Style = Style("BodyStrongTextBlockStyle") });
        panel.Children.Add(new TextBlock
        {
            Text = "Once Windows has an update ready for your next restart, it installs it no matter how you shut down. " +
                "These act before that happens. Changing them asks for administrator permission and turns on the automatic check.",
            TextWrapping = TextWrapping.Wrap, Foreground = Secondary(),
        });

        var autoPause = new ToggleSwitch { Header = "Pause red updates automatically", IsOn = settings.AutoPauseRed };
        panel.Children.Add(autoPause);
        panel.Children.Add(new TextBlock
        {
            Text = "The hourly check pauses updates with confirmed problems, for as long as your pause length. They resume " +
                "when Microsoft fixes them. Updates in yellow and news reports are never paused on their own, and an update " +
                "you resume yourself stays resumed unless a new problem shows up.",
            TextWrapping = TextWrapping.Wrap, Foreground = Secondary(),
        });

        var wait = new ComboBox { Header = "Wait before installing new updates", Width = 220, Margin = new Thickness(0, 8, 0, 0) };
        foreach (var days in WaitChoices) wait.Items.Add(days == 0 ? "Off" : $"{days} days");
        wait.SelectedIndex = Math.Max(0, Array.IndexOf(WaitChoices, settings.WaitDays));
        panel.Children.Add(wait);
        var waitText = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Secondary() };
        panel.Children.Add(waitText);

        var managed = controller.DeferManagedElsewhere;
        if (managed)
        {
            wait.IsEnabled = false;
            waitText.Text = "Managed by your organization on this PC, so PatchLight leaves it alone.";
        }
        else
        {
            waitText.Text = "New monthly Windows updates almost never have known problems on their first day; they show up " +
                "days later. Waiting gives them time to appear. Security updates still arrive, just later. " +
                (controller.IsHomeEdition
                    ? "On Windows Home, PatchLight pauses new updates until they're old enough. This works best when the PC " +
                      "is on and online, because Windows might get an update ready first."
                    : "PatchLight uses Windows' own setting for this, so Windows Update will say some settings are managed " +
                      "by your organization. That's PatchLight.");
        }

        var applying = false;
        async Task ApplyAsync()
        {
            if (applying) return;
            applying = true;
            autoPause.IsEnabled = wait.IsEnabled = false;
            var result = await controller.SetProtectionAsync(autoPause.IsOn, WaitChoices[Math.Max(0, wait.SelectedIndex)]);
            // Show what was really saved: nothing changes if permission wasn't given.
            var saved = controller.Protection;
            autoPause.IsOn = saved.AutoPauseRed;
            wait.SelectedIndex = Math.Max(0, Array.IndexOf(WaitChoices, saved.WaitDays));
            autoPause.IsEnabled = true;
            wait.IsEnabled = !managed;
            applying = false;
            if (!result.Succeeded || result.Message is not null) Show(InfoBarSeverity.Warning, result.Message!);
            await onChanged();
        }
        autoPause.Toggled += async (_, _) => await ApplyAsync();
        wait.SelectionChanged += async (_, _) => await ApplyAsync();

        return Card(panel);
    }

    private string DailyText(DailyCheckState state)
    {
        var lastRun = controller.DailyCheckLastRunText();
        return state switch
        {
            DailyCheckState.Moved => "PatchLight was moved to another folder, so the automatic check can't find it. Turn it on again.",
            DailyCheckState.Outdated => "Set up by an older PatchLight, so it only runs once a day. Turn it off and on again to check every hour.",
            DailyCheckState.On => lastRun.Length > 0 ? lastRun : "On. It hasn't run yet.",
            _ => "Off. Paused updates then stay paused until you press Resume.",
        };
    }

    // ---- Helpers ----

    private void Show(InfoBarSeverity severity, string text, ButtonBase? action = null)
    {
        Message.Severity = severity;
        Message.Message = text;
        Message.ActionButton = action;
        Message.IsOpen = true;
    }

    private static StackPanel Section(UIElement control, string help)
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(control);
        panel.Children.Add(new TextBlock { Text = help, TextWrapping = TextWrapping.Wrap, Foreground = Secondary() });
        return panel;
    }

    private static Border Card(UIElement child) => new()
    {
        Child = child,
        Padding = new Thickness(16),
        CornerRadius = new CornerRadius(8),
        Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
        BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
        BorderThickness = new Thickness(1),
    };

    private static Border Note(string text) =>
        Card(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });

    private static Style? Style(string key) => Application.Current.Resources[key] as Style;

    private static Brush Secondary() => (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

    private static SolidColorBrush Brush(Color color) => new(color);

    private static Color LightColor(Light light) => light switch
    {
        Light.Green => GreenColor,
        Light.Yellow => Color.FromArgb(255, 202, 138, 4),
        _ => RedColor,
    };

    private static string StatusText(IssueStatus status) => status switch
    {
        IssueStatus.Confirmed => "confirmed",
        IssueStatus.Investigating => "being looked into",
        IssueStatus.Mitigated => "has a workaround",
        IssueStatus.Resolved => "fixed",
        _ => "status unknown",
    };

    private static string AppVersion() =>
        typeof(MainWindow).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "";

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
