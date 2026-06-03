using System.Diagnostics;
using CLIGoalHelper.Ado;
using CLIGoalHelper.BusinessTime;
using CLIGoalHelper.Cache;
using CLIGoalHelper.Config;
using CLIGoalHelper.Metrics;
using CLIGoalHelper.Sync;
using CLIGoalHelper.Views;
using Spectre.Console;

AnsiConsole.Write(new FigletText("Dev Dashboard").Color(Color.Cyan1));

BusinessClock.SelfCheck();
var clock = BusinessClock.Default;
var config = AppConfig.Load();
AnsiConsole.MarkupLine("[grey]Org:[/] {0}", config.AzureDevOps.OrganizationUrl);

using var cache = CacheStore.OpenDefault();
AnsiConsole.MarkupLine("[grey]Cache:[/] {0}", cache.DatabasePath);

using var client = new AdoClient(config.AzureDevOps.OrganizationUrl);
var identityService = new IdentityService(client, config.AzureDevOps.OrganizationUrl);
var repositoryService = new RepositoryService(client, config.AzureDevOps.OrganizationUrl);
var pullRequestService = new PullRequestService(client, config.AzureDevOps.OrganizationUrl);
var threadService = new ThreadService(client, config.AzureDevOps.OrganizationUrl);
var iterationService = new IterationService(client, config.AzureDevOps.OrganizationUrl);
var workItemService = new WorkItemService(client, config.AzureDevOps.OrganizationUrl);

await AnsiConsole.Status()
    .StartAsync("Authenticating via az CLI...", async _ =>
    {
        using var doc = await client.GetJsonAsync("_apis/connectionData?api-version=7.1-preview.1");
        var displayName = doc.RootElement.GetProperty("authenticatedUser")
            .GetProperty("providerDisplayName").GetString();
        AnsiConsole.MarkupLine("[green][[OK]][/] Authenticated as [cyan]{0}[/]", displayName ?? "?");
    });

await AnsiConsole.Status()
    .StartAsync("Resolving required reviewers...", async _ =>
    {
        var identities = await Task.WhenAll(
            config.RequiredReviewers.Select(name => identityService.ResolveByDisplayNameAsync(name)));
        foreach (var id in identities)
        {
            cache.Identities.Upsert(new CachedIdentity(id.Id, id.Descriptor, id.DisplayName, id.Email));
        }
    });

await AnsiConsole.Status()
    .StartAsync("Discovering repositories...", async _ =>
    {
        var allRepos = await repositoryService.ListAllAsync();
        var configured = new Dictionary<string, string>(
            config.AzureDevOps.Repositories, StringComparer.OrdinalIgnoreCase);
        foreach (var repo in allRepos.Where(r => configured.ContainsKey(r.Name)))
        {
            // Upsert preserves last_sync_utc on conflict via excluded.last_sync_utc; we want to
            // preserve the existing value, so re-read it first. The local path comes from config
            // (empty -> null = tracked but no local checkout).
            var existing = cache.Repos.GetByName(repo.Name);
            var localPath = configured.TryGetValue(repo.Name, out var p) && !string.IsNullOrWhiteSpace(p) ? p : null;
            cache.Repos.Upsert(new CachedRepo(repo.Id, repo.Project, repo.Name, existing?.LastSyncUtc, localPath));
        }
    });

var syncService = new SyncService(
    cache,
    pullRequestService,
    threadService,
    iterationService,
    workItemService,
    clock,
    cache.Identities.GetAll().Select(i => i.Id),
    config.Metrics.SlaBusinessHours,
    config.Metrics.BackfillDays,
    config.AzureDevOps.BoardsProject,
    config.Metrics.TrailingWindowDays,
    config.Metrics.GoalStartDate);

await RunSyncAsync(isColdStart: cache.Repos.GetAll().Any(r => r.LastSyncUtc == null));

var personalId = cache.Identities.GetAll()
    .FirstOrDefault(i => i.Email != null && i.Email.Equals(config.AzureDevOps.CurrentUserEmail, StringComparison.OrdinalIgnoreCase))
    ?.Id;

var excludedRepoNames = config.ExcludedFromStatsRepos
    .ToHashSet(StringComparer.OrdinalIgnoreCase);
var excludedRepoIds = cache.Repos.GetAll()
    .Where(r => excludedRepoNames.Contains(r.Name))
    .Select(r => r.Id)
    .ToList();

var reviewMetrics = new ReviewMetrics(
    cache,
    config.Metrics.TrailingWindowDays,
    config.Metrics.GoalStartDate,
    personalId,
    excludedRepoIds);

var bugMetrics = new BugMetrics(
    cache,
    config.Metrics.TrailingWindowDays,
    config.Metrics.GoalStartDate);

var throughputDefectMetrics = new ThroughputDefectMetrics(
    cache,
    config.Metrics.GoalStartDate,
    excludedRepoIds);

var shortNames = MailmapResolver.LoadFromHome();
var dashboard = new Dashboard(cache, clock, reviewMetrics, bugMetrics, throughputDefectMetrics, config, shortNames, excludedRepoNames);

while (true)
{
    dashboard.Render();

    var key = Console.ReadKey(intercept: true);
    // The transient status line (set by 'c') was just shown in the render above; clear it so it
    // lives for exactly one render cycle and doesn't linger across the next action.
    dashboard.ClearStatus();
    if (key.Key == ConsoleKey.Q)
    {
        break;
    }
    if (key.Key == ConsoleKey.R)
    {
        await RunSyncAsync(isColdStart: false);
    }
    if (key.Key == ConsoleKey.Tab)
    {
        dashboard.ToggleFocus();
    }
    // 'c' launches with the standard prompt; 'C' (shift) lets you append an extra instruction.
    // KeyChar (not Key) so the case distinction survives shift/caps-lock correctly.
    if (key.KeyChar == 'c')
    {
        LaunchAgentForSelectedPr(promptForExtra: false);
    }
    else if (key.KeyChar == 'C')
    {
        LaunchAgentForSelectedPr(promptForExtra: true);
    }
    if (key.Key is ConsoleKey.J or ConsoleKey.DownArrow)
    {
        dashboard.MoveSelection(+1);
    }
    if (key.Key is ConsoleKey.K or ConsoleKey.UpArrow)
    {
        dashboard.MoveSelection(-1);
    }
    if (key.Key == ConsoleKey.Enter)
    {
        OpenInBrowser(dashboard.SelectedUrl);
    }
}

AnsiConsole.MarkupLine("[grey]Bye.[/]");

async Task RunSyncAsync(bool isColdStart)
{
    var label = isColdStart ? "Cold-start backfill" : "Refreshing";
    AnsiConsole.MarkupLine($"[grey]{label}...[/]");
    await AnsiConsole.Progress()
        .Columns(
            new TaskDescriptionColumn(),
            new ProgressBarColumn(),
            new PercentageColumn(),
            new ElapsedTimeColumn())
        .StartAsync(ctx => syncService.SyncAsync(ctx));
}

// Launches a background Claude agent (`claude --bg`) focused on the selected PR, in that PR's
// repo's local working copy. Only fires when the Open-PRs panel is focused (SelectedPrAgentTarget
// is null otherwise). Refuses with a visible status when the repo has no usable local path.
void LaunchAgentForSelectedPr(bool promptForExtra)
{
    var target = dashboard.SelectedPrAgentTarget;
    if (target is null)
    {
        return;
    }

    if (string.IsNullOrWhiteSpace(target.LocalPath) || !Directory.Exists(target.LocalPath))
    {
        dashboard.SetStatus($"[yellow]⚠ No local path for repo '{Markup.Escape(target.RepoName)}' — add it to appsettings[/]");
        return;
    }

    var prompt = config.PrAgent.PromptTemplate.Replace("{URL}", target.Url);
    if (promptForExtra)
    {
        // Inline input field. Esc cancels the whole launch; empty (just Enter) sends the
        // standard prompt only; otherwise the typed text is appended to the standard prompt.
        var extra = ReadLineOrCancel($"[cyan]Extra instruction for PR {target.PrId}[/] [grey](esc to cancel)[/]:");
        if (extra is null)
        {
            dashboard.SetStatus("[grey]Agent launch cancelled[/]");
            return;
        }
        if (!string.IsNullOrWhiteSpace(extra))
        {
            prompt = $"{prompt}\n\n{extra}";
        }
    }

    var name = string.IsNullOrWhiteSpace(target.Title) ? target.PrId.ToString() : $"{target.PrId}: {target.Title}";

    try
    {
        // claude.exe is a console app; from a console parent it would otherwise write its
        // "backgrounded · <id>" line onto the dashboard. Redirecting the streams keeps that output
        // off-screen. `--bg` is fire-and-forget (returns immediately), so we don't wait.
        var psi = new ProcessStartInfo
        {
            FileName = "claude",
            WorkingDirectory = target.LocalPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("--bg");
        psi.ArgumentList.Add("--name");
        psi.ArgumentList.Add(name);
        psi.ArgumentList.Add(prompt);
        Process.Start(psi);
        dashboard.SetStatus($"[green]⏵ Launched agent for PR {target.PrId} ({Markup.Escape(target.RepoName)})[/]");
    }
    catch (Exception ex)
    {
        dashboard.SetStatus($"[red]Agent launch failed: {Markup.Escape(ex.Message)}[/]");
    }
}

// Reads a line of input with basic editing (echoed). Returns the text on Enter, or null if the
// user pressed Esc to cancel. Used for the optional extra-instruction prompt; the dashboard's own
// Render clears it on the next loop iteration.
string? ReadLineOrCancel(string promptMarkup)
{
    AnsiConsole.Markup(promptMarkup + " ");
    var input = string.Empty;
    while (true)
    {
        var k = Console.ReadKey(intercept: true);
        switch (k.Key)
        {
            case ConsoleKey.Escape:
                AnsiConsole.WriteLine();
                return null;
            case ConsoleKey.Enter:
                AnsiConsole.WriteLine();
                return input;
            case ConsoleKey.Backspace:
                if (input.Length > 0)
                {
                    input = input[..^1];
                    Console.Write("\b \b");
                }
                break;
            default:
                if (!char.IsControl(k.KeyChar))
                {
                    input += k.KeyChar;
                    Console.Write(k.KeyChar);
                }
                break;
        }
    }
}

void OpenInBrowser(string? url)
{
    if (url is null)
    {
        return;
    }

    // Open in Chrome explicitly (the user's request): "chrome" resolves via the Windows
    // App Paths registry key under ShellExecute. If Chrome isn't found, fall back to the
    // default browser rather than crashing the dashboard.
    try
    {
        Process.Start(new ProcessStartInfo("chrome", url) { UseShellExecute = true });
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[yellow]Chrome launch failed ({Markup.Escape(ex.Message)}); using default browser.[/]");
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}
