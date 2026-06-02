using System.Text;
using CLIGoalHelper.BusinessTime;
using CLIGoalHelper.Cache;
using CLIGoalHelper.Config;
using CLIGoalHelper.Metrics;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace CLIGoalHelper.Views;

public sealed class Dashboard
{
    private const int TargetPercentage = 65;
    private const int BaselinePercentage = 50;

    private readonly CacheStore _cache;
    private readonly BusinessClock _clock;
    private readonly ReviewMetrics _metrics;
    private readonly BugMetrics _bugMetrics;
    private readonly ThroughputDefectMetrics _throughput;
    private readonly AppConfig _config;
    private readonly MailmapResolver _shortNames;
    private readonly IReadOnlySet<string> _excludedRepoNames;

    // Which panel currently has keyboard focus. Tab toggles it; j/k and enter act on it.
    private enum FocusTarget { Pulls, Bugs }
    private FocusTarget _focus = FocusTarget.Pulls;

    // The open-PR rows in display order, rebuilt on every Render. The j/k selection cursor
    // and "open in browser" (enter) both index into this list.
    private IReadOnlyList<(CachedPullRequest Pr, CachedRepo Repo)> _openPrs = [];
    private int _selectedPrIndex;

    // The recent-bug rows in display order, rebuilt on every Render — same role as _openPrs
    // but for the bugs panel.
    private IReadOnlyList<BugWorkItemCache.RecentBug> _recentBugs = [];
    private int _selectedBugIndex;

    public Dashboard(
        CacheStore cache,
        BusinessClock clock,
        ReviewMetrics metrics,
        BugMetrics bugMetrics,
        ThroughputDefectMetrics throughput,
        AppConfig config,
        MailmapResolver shortNames,
        IReadOnlySet<string> excludedRepoNames)
    {
        _cache = cache;
        _clock = clock;
        _metrics = metrics;
        _bugMetrics = bugMetrics;
        _throughput = throughput;
        _config = config;
        _shortNames = shortNames;
        _excludedRepoNames = excludedRepoNames;
    }

    public void Render()
    {
        AnsiConsole.Clear();
        AnsiConsole.Write(new FigletText("Dev Dashboard").Color(Color.Cyan1));
        AnsiConsole.MarkupLine($"[grey]Last refresh: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}[/]");
        AnsiConsole.WriteLine();

        AnsiConsole.Write(BuildOpenPrsPanel());
        AnsiConsole.WriteLine();
        AnsiConsole.Write(BuildMetricPanel());
        AnsiConsole.WriteLine();
        // DLR stats on the left, the most-recent bugs alongside on the right. Collapse() sizes each
        // panel to its content (rather than stretching to equal halves); on a terminal too narrow
        // for both, Columns wraps the bug panel below the DLR panel.
        AnsiConsole.Write(new Columns(BuildDlrPanel(), BuildRecentBugsPanel()).Collapse());
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey][[tab]] switch panel   [[j/k]] select   [[enter]] open in browser   [[r]] refresh   [[q]] quit[/]");
    }

    private Panel BuildDlrPanel()
    {
        var snap = _bugMetrics.Compute();

        // DLR is "bad" — higher means more production leakage. Color thresholds inverted vs review %.
        var dlrColor = snap.Dlr90Percentage <= 15 ? "green" : snap.Dlr90Percentage <= 30 ? "yellow" : "red";
        var headline = new Markup(
            $"[bold]DLR90 Production[/]   [bold]{snap.Dlr90Percentage,5:0.0}%[/]  "
            + $"{Bar(snap.Dlr90Percentage, 30, dlrColor)} [grey]{snap.Production}/{snap.Total} — customer-found leakage[/]");

        // Breakdown by Found System (wiki): Production, QA+Test (grouped orange), Dev, unset.
        var breakdown = new Markup(
            $"[bold]Breakdown[/]: [red]Production {snap.Production}[/]  ·  [orange1]QA/Test {snap.QaTest}[/]  ·  [grey]Dev {snap.Dev}[/]"
            + (snap.Unknown > 0 ? $"  ·  [grey](unset {snap.Unknown})[/]" : ""));

        // Old DLR90-Production-by-month graph and the new throughput-vs-defects table, side by
        // side — both monthly over the same goal-start→now axis, so the rows line up.
        var sideBySide = new Grid();
        sideBySide.AddColumn(new GridColumn().PadRight(3));
        sideBySide.AddColumn();
        sideBySide.AddRow(BuildMonthlyDlrColumn(snap.Monthly), BuildThroughputColumn());

        var content = new Rows(
            headline,
            new Markup(string.Empty),
            breakdown,
            new Markup(string.Empty),
            sideBySide,
            new Markup(string.Empty),
            new Markup("[grey]* current month partial; recent bug counts lag as defects surface later.[/]"),
            new Markup("[grey]Bugs/PR = bugs ÷ completed PRs (excl. Prototype) — trend indicator, not absolute density.[/]"));

        return new Panel(content)
            .Header($"[bold]Defects & throughput[/] [grey](DLR90 trailing {_config.Metrics.TrailingWindowDays}d, {_config.AzureDevOps.BoardsProject})[/]")
            .Border(BoxBorder.Rounded);
    }

    // The restored "old" view: trailing-90d DLR90 Production leakage per month, as a bar + %.
    private IRenderable BuildMonthlyDlrColumn(IReadOnlyList<MonthProductionDlr> monthly)
    {
        var table = new Table()
            .Border(TableBorder.None)
            .AddColumn(new TableColumn("[grey]Month[/]"))
            .AddColumn(new TableColumn(string.Empty))
            .AddColumn(new TableColumn("[grey]DLR90 Prod[/]").RightAligned());

        foreach (var m in monthly)
        {
            var color = m.ProductionPercentage <= 15 ? "green" : m.ProductionPercentage <= 30 ? "yellow" : "red";
            table.AddRow(
                m.Label,
                Bar(m.ProductionPercentage, 12, color),
                $"[grey]{m.ProductionPercentage,5:0.0}% {m.Production}/{m.Total}[/]");
        }

        return new Rows(new Markup("[bold]DLR90 Production by month[/]"), table);
    }

    // Per-month PR throughput vs bug volume, with a bar on Bugs/PR scaled to the series max so
    // a rising rate is visible at a glance. The current (in-progress) month is flagged with '*'.
    private IRenderable BuildThroughputColumn()
    {
        var months = _throughput.Compute();
        var maxRate = months.Select(m => m.BugsPerPr ?? 0).DefaultIfEmpty(0).Max();
        var currentKey = DateTimeOffset.UtcNow.ToString("yyyy-MM");

        var table = new Table()
            .Border(TableBorder.None)
            .AddColumn(new TableColumn("[grey]Month[/]"))
            .AddColumn(new TableColumn("[grey]PRs[/]").RightAligned())
            .AddColumn(new TableColumn("[grey]Bugs[/]").RightAligned())
            .AddColumn(new TableColumn("[grey]Bugs/PR[/]").RightAligned())
            .AddColumn(new TableColumn(string.Empty));

        foreach (var m in months)
        {
            var label = m.YearMonth == currentKey ? $"{m.YearMonth}[yellow]*[/]" : m.YearMonth;
            var rate = m.BugsPerPr is { } r ? $"{r:0.00}" : "[grey]—[/]";
            var bar = m.BugsPerPr is { } rr && maxRate > 0
                ? Bar(100.0 * rr / maxRate, 10, "magenta")
                : string.Empty;
            table.AddRow(label, m.Prs.ToString(), m.Bugs.ToString(), rate, bar);
        }

        return new Rows(new Markup("[bold]Throughput vs defects[/]"), table);
    }

    private Panel BuildRecentBugsPanel()
    {
        _recentBugs = _cache.Bugs.GetRecent(10);
        var focused = _focus == FocusTarget.Bugs;

        if (_recentBugs.Count == 0)
        {
            _selectedBugIndex = 0;
            return WithFocusBorder(new Panel("[grey](no bugs)[/]").Header("[bold]Last 10 bugs[/]"), focused);
        }

        // Keep the cursor on a valid row as the list changes across refreshes.
        _selectedBugIndex = Math.Clamp(_selectedBugIndex, 0, _recentBugs.Count - 1);

        var now = DateTimeOffset.UtcNow;
        // Borderless table so the right-aligned age column lines up regardless of title length
        // (string padding would break on titles that contain markup-escaped characters).
        var table = new Table()
            .Border(TableBorder.None)
            .HideHeaders()
            .AddColumn(new TableColumn(string.Empty))
            .AddColumn(new TableColumn(string.Empty))
            .AddColumn(new TableColumn(string.Empty))
            .AddColumn(new TableColumn(string.Empty).RightAligned());

        for (var i = 0; i < _recentBugs.Count; i++)
        {
            var b = _recentBugs[i];
            // Whole row coloured by where the bug was found: Production red, Test yellow,
            // Dev grey, anything unset dimmed. Keeps the "red = production leakage" signal.
            var style = StyleForFoundIn(b.FoundInSystem);

            // The cursor (and underline) only render while this panel holds focus.
            var selected = focused && i == _selectedBugIndex;
            var cursorCell = selected ? "[cyan]❯[/]" : " ";
            var idCell = $"[{style}]{b.Id}[/]";
            var titleCell = $"[{style}]{Markup.Escape(Truncate(b.Title, 30))}[/]";
            var ageCell = $"[{style}]{FormatAge(b.CreatedUtc, now)}[/]";

            if (selected)
            {
                cursorCell = Underline(cursorCell);
                idCell = Underline(idCell);
                titleCell = Underline(titleCell);
                ageCell = Underline(ageCell);
            }

            table.AddRow(cursorCell, idCell, titleCell, ageCell);
        }

        return WithFocusBorder(new Panel(table).Header("[bold]Last 10 bugs[/] [grey](newest first)[/]"), focused);
    }

    // Applies the shared rounded border, tinted cyan while the panel holds keyboard focus.
    private static Panel WithFocusBorder(Panel panel, bool focused)
    {
        panel.Border(BoxBorder.Rounded);
        return focused ? panel.BorderColor(Color.Cyan1) : panel;
    }

    // Spectre style for a bug's "Found System" category (Bug-Report-Guidelines wiki).
    // Production (customer-found) is the worst → red; QA (release candidate) and Test (test
    // activities) are grouped as orange; Dev (nightly) is grey; anything unset is dimmed.
    private static string StyleForFoundIn(string? foundIn) => foundIn switch
    {
        "Production" => "red",
        "QA" => "orange1",
        "Test" => "orange1",
        "Dev" => "grey",
        _ => "dim"
    };

    // Compact calendar age (not business hours): minutes, then hours, then days, then weeks.
    private static string FormatAge(DateTimeOffset created, DateTimeOffset now)
    {
        var span = now - created;
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes}m";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours}h";
        if (span.TotalDays < 14) return $"{(int)span.TotalDays}d";
        return $"{(int)(span.TotalDays / 7)}w";
    }

    private Panel BuildOpenPrsPanel()
    {
        var slaHours = (double)_config.Metrics.SlaBusinessHours;
        var now = DateTimeOffset.UtcNow;
        var newSinceCutoff = ReadPreviousFullSyncCutoff();

        var table = new Table()
            .Border(TableBorder.MinimalHeavyHead)
            .AddColumn(" ")
            .AddColumn("Repo")
            .AddColumn("PR")
            .AddColumn("Author")
            .AddColumn("Age (biz)")
            .AddColumn("Last change")
            .AddColumn("Reviewers")
            .AddColumn("Status");

        _openPrs = _cache.Repos.GetAll()
            .SelectMany(repo => _cache.PullRequests.GetActiveForRepo(repo.Id).Select(pr => (Pr: pr, Repo: repo)))
            .OrderBy(t => t.Pr.CreationUtc)
            .ToList();

        // Keep the cursor on a valid row as the list grows or shrinks across refreshes.
        _selectedPrIndex = _openPrs.Count == 0 ? 0 : Math.Clamp(_selectedPrIndex, 0, _openPrs.Count - 1);
        var focused = _focus == FocusTarget.Pulls;

        for (var i = 0; i < _openPrs.Count; i++)
        {
            var (pr, repo) = _openPrs[i];
            var businessAge = _clock.ElapsedHours(pr.CreationUtc, now);
            var ageText = FormatBusinessHours(businessAge);
            var lastChangeUtc = pr.LastActivityUtc ?? pr.CreationUtc;
            var lastChangeText = FormatBusinessHours(_clock.ElapsedHours(lastChangeUtc, now));
            if (newSinceCutoff.HasValue && lastChangeUtc > newSinceCutoff.Value)
            {
                lastChangeText = $"[green]{lastChangeText}[/]";
            }
            var excluded = _excludedRepoNames.Contains(repo.Name);

            string status;
            if (pr.FirstRequiredVoteUtc is null)
            {
                status = businessAge > slaHours ? "[red]OVERDUE[/]" : "[yellow]Needs answer[/]";
            }
            else
            {
                status = pr.FirstRequiredVoteValue switch
                {
                    >= 5 => "[green]Approved · awaiting more[/]",
                    -5 => "[orange1]Waiting for author[/]",
                    <= -10 => "[red]Rejected[/]",
                    _ => "[grey]?[/]"
                };
            }

            // The cursor (and underline) only render while this panel holds focus.
            var selected = focused && i == _selectedPrIndex;
            var cursorCell = selected ? "[cyan]❯[/]" : " ";
            var repoCell = excluded ? repo.Name + "*" : repo.Name;
            var prCell = $"[bold]{pr.Id}[/] [grey]{Markup.Escape(Truncate(pr.Title, 50))}[/]";
            var authorCell = _shortNames.Shorten(pr.AuthorDisplayName, AuthorEmailFor(pr));
            var reviewersCell = BuildReviewersCell(pr.Id);

            if (selected)
            {
                // Underline the whole row so the cursor reference isn't lost on long lines.
                // Underline combines with each cell's foreground colour, unlike a background
                // block which would hide the default-coloured and low-contrast (yellow) cells.
                cursorCell = Underline(cursorCell);
                repoCell = Underline(repoCell);
                prCell = Underline(prCell);
                authorCell = Underline(authorCell);
                ageText = Underline(ageText);
                lastChangeText = Underline(lastChangeText);
                reviewersCell = Underline(reviewersCell);
                status = Underline(status);
            }

            table.AddRow(cursorCell, repoCell, prCell, authorCell, ageText, lastChangeText, reviewersCell, status);
        }

        return WithFocusBorder(
                new Panel(table).Header($"[bold]Open PRs[/] [grey](SLA = {slaHours}h business)[/]"),
                focused)
            .Expand();
    }

    // Tab toggles keyboard focus between the open-PRs and recent-bugs panels.
    public void ToggleFocus() => _focus = _focus == FocusTarget.Pulls ? FocusTarget.Bugs : FocusTarget.Pulls;

    // Moves the focused panel's selection cursor by delta rows, clamped to its list bounds
    // (vim j/k, no wrap-around). The row lists are rebuilt on the next Render.
    public void MoveSelection(int delta)
    {
        if (_focus == FocusTarget.Pulls)
        {
            if (_openPrs.Count == 0)
            {
                return;
            }

            _selectedPrIndex = Math.Clamp(_selectedPrIndex + delta, 0, _openPrs.Count - 1);
        }
        else
        {
            if (_recentBugs.Count == 0)
            {
                return;
            }

            _selectedBugIndex = Math.Clamp(_selectedBugIndex + delta, 0, _recentBugs.Count - 1);
        }
    }

    // The Azure DevOps web URL of the selected item in the focused panel, or null when that
    // panel's list is empty. Enter opens this in the browser.
    public string? SelectedUrl => _focus == FocusTarget.Pulls ? SelectedPrUrl : SelectedBugUrl;

    private string? SelectedPrUrl
    {
        get
        {
            if (_selectedPrIndex < 0 || _selectedPrIndex >= _openPrs.Count)
            {
                return null;
            }

            var (pr, repo) = _openPrs[_selectedPrIndex];
            var org = _config.AzureDevOps.OrganizationUrl.TrimEnd('/');
            return $"{org}/{Uri.EscapeDataString(repo.Project)}/_git/{Uri.EscapeDataString(repo.Name)}/pullrequest/{pr.Id}";
        }
    }

    private string? SelectedBugUrl
    {
        get
        {
            if (_selectedBugIndex < 0 || _selectedBugIndex >= _recentBugs.Count)
            {
                return null;
            }

            var bug = _recentBugs[_selectedBugIndex];
            var org = _config.AzureDevOps.OrganizationUrl.TrimEnd('/');
            return $"{org}/{Uri.EscapeDataString(bug.Project)}/_workitems/edit/{bug.Id}";
        }
    }

    private static string? AuthorEmailFor(CachedPullRequest pr)
    {
        // The PR row doesn't store author email separately — fall back to None so the
        // resolver derives the short code from AuthorDisplayName alone. (Acceptable because
        // collision handling is explicitly out of scope per design.)
        return null;
    }

    private string BuildReviewersCell(int prId)
    {
        var engagements = _cache.PullRequests.GetEngagementsFor(prId);
        if (engagements.Count == 0)
        {
            return "[grey](none)[/]";
        }

        // Sort by first-engagement time ascending; passive reviewers (no action yet) go last.
        var ordered = engagements
            .OrderBy(e => e.FirstEngagementUtc ?? DateTimeOffset.MaxValue)
            .Select(e =>
            {
                var color = e.Kind == EngagementKind.Reviewer ? "green" : "red";
                var code = _shortNames.Shorten(e.DisplayName, e.Email);
                return $"[{color}]{code}[/]";
            });

        return string.Join(" ", ordered);
    }

    private Panel BuildMetricPanel()
    {
        var snap = _metrics.Compute();
        var headlineColor = ColorFor(snap.TrailingPassPercentage);
        var body = new StringBuilder();

        body.AppendLine($"[bold]{snap.TrailingPassPercentage:0.0}%[/] of {snap.TrailingTotal} completed PRs got their first required vote within SLA");
        body.AppendLine($"  {Bar(snap.TrailingPassPercentage, 40, headlineColor)} [grey]target {TargetPercentage}%, baseline ~{BaselinePercentage}%[/]");
        body.AppendLine();
        body.AppendLine("[bold]Monthly[/]");
        foreach (var m in snap.Monthly)
        {
            body.AppendLine($"  {m.YearMonth}  {Bar(m.PassPercentage, 20, ColorFor(m.PassPercentage))} [grey]{m.PassPercentage,5:0.0}%  ({m.Pass}/{m.Total})[/]");
        }
        body.AppendLine();

        var medianText = snap.MedianBusinessHours.HasValue
            ? FormatBusinessHours(snap.MedianBusinessHours.Value)
            : "[grey](no data)[/]";
        body.AppendLine($"[bold]Median time to first required vote:[/] {medianText}");
        body.AppendLine($"[bold]Your activity (30d):[/] first required vote on {snap.PersonalFirstVotes30d} PR(s)");

        if (snap.TrailingNoVote > 0)
        {
            body.Append($"[yellow]Data quality:[/] {snap.TrailingNoVote} completed PR(s) had no vote from any of the four — investigate");
        }

        if (_excludedRepoNames.Count > 0)
        {
            body.AppendLine();
            body.Append($"[grey]* PRs from {string.Join(", ", _excludedRepoNames)} are excluded from these stats[/]");
        }

        return new Panel(body.ToString().TrimEnd())
            .Header($"[bold]PR review turnaround[/] [grey](trailing {_config.Metrics.TrailingWindowDays}d, completed only)[/]")
            .Border(BoxBorder.Rounded)
            .Expand();
    }

    private static string FormatBusinessHours(double hours)
    {
        if (hours >= 24) return $"{hours / 24:0.0}d";
        if (hours >= 1) return $"{(int)hours}h {(int)((hours - (int)hours) * 60)}m";
        return $"{(int)(hours * 60)}m";
    }

    private static string Truncate(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text.Length <= max ? text : text[..(max - 1)] + "…";
    }

    private static string Bar(double pct, int width, string color)
    {
        var clamped = Math.Clamp(pct, 0, 100);
        var filled = (int)Math.Round(clamped / 100 * width);
        return $"[{color}]{new string('█', filled)}[/][grey]{new string('░', width - filled)}[/]";
    }

    private static string ColorFor(double pct) => pct >= TargetPercentage ? "green" : pct >= BaselinePercentage ? "yellow" : "red";

    // Wraps already-marked-up cell content in an underline decoration; the inner foreground
    // colours are preserved because Spectre combines decorations with the active style.
    private static string Underline(string cell) => $"[underline]{cell}[/]";

    private DateTimeOffset? ReadPreviousFullSyncCutoff()
    {
        var raw = _cache.Meta.Get(Sync.SyncService.PreviousFullSyncMetaKey);
        return raw is null ? null : DateTimeOffset.Parse(raw);
    }
}
