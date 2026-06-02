using System.Text;
using CLIGoalHelper.BusinessTime;
using CLIGoalHelper.Cache;
using CLIGoalHelper.Config;
using CLIGoalHelper.Metrics;
using Spectre.Console;

namespace CLIGoalHelper.Views;

public sealed class Dashboard
{
    private const int TargetPercentage = 65;
    private const int BaselinePercentage = 50;

    private readonly CacheStore _cache;
    private readonly BusinessClock _clock;
    private readonly ReviewMetrics _metrics;
    private readonly BugMetrics _bugMetrics;
    private readonly AppConfig _config;
    private readonly MailmapResolver _shortNames;
    private readonly IReadOnlySet<string> _excludedRepoNames;

    // The open-PR rows in display order, rebuilt on every Render. The j/k selection cursor
    // and "open in browser" (enter) both index into this list.
    private IReadOnlyList<(CachedPullRequest Pr, CachedRepo Repo)> _openPrs = [];
    private int _selectedIndex;

    public Dashboard(
        CacheStore cache,
        BusinessClock clock,
        ReviewMetrics metrics,
        BugMetrics bugMetrics,
        AppConfig config,
        MailmapResolver shortNames,
        IReadOnlySet<string> excludedRepoNames)
    {
        _cache = cache;
        _clock = clock;
        _metrics = metrics;
        _bugMetrics = bugMetrics;
        _config = config;
        _shortNames = shortNames;
        _excludedRepoNames = excludedRepoNames;
    }

    public void Render()
    {
        AnsiConsole.Clear();
        AnsiConsole.Write(new FigletText("Goal Dashboard").Color(Color.Cyan1));
        AnsiConsole.MarkupLine($"[grey]Last refresh: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}[/]");
        AnsiConsole.WriteLine();

        AnsiConsole.Write(BuildOpenPrsPanel());
        AnsiConsole.WriteLine();
        AnsiConsole.Write(BuildMetricPanel());
        AnsiConsole.WriteLine();
        AnsiConsole.Write(BuildDlrPanel());
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey][[j/k]] select   [[enter]] open in browser   [[r]] refresh   [[q]] quit[/]");
    }

    private Panel BuildDlrPanel()
    {
        var snap = _bugMetrics.Compute();
        var body = new StringBuilder();

        // DLR is "bad" — higher means more production leakage. Color thresholds inverted vs review %.
        var dlrColor = snap.Dlr90Percentage <= 15 ? "green" : snap.Dlr90Percentage <= 30 ? "yellow" : "red";

        body.AppendLine($"[bold]DLR90 Production[/]      [bold]{snap.Dlr90Percentage,5:0.0}%[/]  {Bar(snap.Dlr90Percentage, 30, dlrColor)} [grey]{snap.Production}/{snap.Total} — customer-found leakage[/]");
        var testingColor = snap.Dlr90TestingPercentage <= 30 ? "green" : snap.Dlr90TestingPercentage <= 50 ? "yellow" : "red";
        body.AppendLine($"[bold]DLR90 Testing[/]         [bold]{snap.Dlr90TestingPercentage,5:0.0}%[/]  {Bar(snap.Dlr90TestingPercentage, 30, testingColor)} [grey]{snap.Production + snap.Test}/{snap.Total} — escaped Dev (caught by QA or worse)[/]");
        body.AppendLine();
        body.AppendLine($"[bold]Breakdown[/]: [red]Production {snap.Production}[/]  ·  [yellow]Test {snap.Test}[/]  ·  [grey]Dev {snap.Dev}[/]" +
                        (snap.Unknown > 0 ? $"  ·  [grey](unset {snap.Unknown})[/]" : ""));
        body.AppendLine();
        body.AppendLine($"[bold]Trailing {_config.Metrics.TrailingWindowDays}d DLR by month-end (rolling)[/]");
        body.AppendLine("           [grey]DLR90 Production                    DLR90 Testing[/]");
        foreach (var m in snap.Monthly)
        {
            var prodColor = m.ProductionPercentage <= 15 ? "green" : m.ProductionPercentage <= 30 ? "yellow" : "red";
            var testColor = m.TestingPercentage <= 30 ? "green" : m.TestingPercentage <= 50 ? "yellow" : "red";
            var prodCounts = $"{m.Production}/{m.Total}";
            var testCounts = $"{m.Production + m.Test}/{m.Total}";
            body.AppendLine(
                $"  {m.Label}   "
                + $"{Bar(m.ProductionPercentage, 15, prodColor)} [grey]{m.ProductionPercentage,5:0.0}% {prodCounts,-7}[/]    "
                + $"{Bar(m.TestingPercentage, 15, testColor)} [grey]{m.TestingPercentage,5:0.0}% {testCounts,-7}[/]");
        }

        return new Panel(body.ToString().TrimEnd())
            .Header($"[bold]DLR90 — bugs from production[/] [grey](trailing {_config.Metrics.TrailingWindowDays}d, project {_config.AzureDevOps.BoardsProject})[/]")
            .Border(BoxBorder.Rounded)
            .Expand();
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
        _selectedIndex = _openPrs.Count == 0 ? 0 : Math.Clamp(_selectedIndex, 0, _openPrs.Count - 1);

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

            var cursorCell = i == _selectedIndex ? "[cyan]❯[/]" : " ";
            var repoCell = excluded ? repo.Name + "*" : repo.Name;
            var prCell = $"[bold]{pr.Id}[/] [grey]{Markup.Escape(Truncate(pr.Title, 50))}[/]";
            var authorCell = _shortNames.Shorten(pr.AuthorDisplayName, AuthorEmailFor(pr));
            var reviewersCell = BuildReviewersCell(pr.Id);

            table.AddRow(cursorCell, repoCell, prCell, authorCell, ageText, lastChangeText, reviewersCell, status);
        }

        return new Panel(table)
            .Header($"[bold]Open PRs[/] [grey](SLA = {slaHours}h business)[/]")
            .Border(BoxBorder.Rounded)
            .Expand();
    }

    // Moves the open-PR selection cursor by delta rows, clamped to the list bounds
    // (vim j/k, no wrap-around). The row list itself is rebuilt on the next Render.
    public void MoveSelection(int delta)
    {
        if (_openPrs.Count == 0)
        {
            return;
        }

        _selectedIndex = Math.Clamp(_selectedIndex + delta, 0, _openPrs.Count - 1);
    }

    // The Azure DevOps web URL of the currently selected PR, or null when the list is empty.
    public string? SelectedPrUrl
    {
        get
        {
            if (_selectedIndex < 0 || _selectedIndex >= _openPrs.Count)
            {
                return null;
            }

            var (pr, repo) = _openPrs[_selectedIndex];
            var org = _config.AzureDevOps.OrganizationUrl.TrimEnd('/');
            return $"{org}/{Uri.EscapeDataString(repo.Project)}/_git/{Uri.EscapeDataString(repo.Name)}/pullrequest/{pr.Id}";
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

    private DateTimeOffset? ReadPreviousFullSyncCutoff()
    {
        var raw = _cache.Meta.Get(Sync.SyncService.PreviousFullSyncMetaKey);
        return raw is null ? null : DateTimeOffset.Parse(raw);
    }
}
