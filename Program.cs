using CLIGoalHelper.Ado;
using CLIGoalHelper.BusinessTime;
using CLIGoalHelper.Cache;
using CLIGoalHelper.Config;
using CLIGoalHelper.Metrics;
using CLIGoalHelper.Sync;
using CLIGoalHelper.Views;
using Spectre.Console;

AnsiConsole.Write(new FigletText("Goal Dashboard").Color(Color.Cyan1));

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
        var configured = config.AzureDevOps.Repositories.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var repo in allRepos.Where(r => configured.Contains(r.Name)))
        {
            // Upsert preserves last_sync_utc on conflict via excluded.last_sync_utc; we want to
            // preserve the existing value, so re-read it first.
            var existing = cache.Repos.GetByName(repo.Name);
            cache.Repos.Upsert(new CachedRepo(repo.Id, repo.Project, repo.Name, existing?.LastSyncUtc));
        }
    });

var syncService = new SyncService(
    cache,
    pullRequestService,
    threadService,
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
    .FirstOrDefault(i => i.Email != null && i.Email.Equals("s.pech@palfinger.com", StringComparison.OrdinalIgnoreCase))
    ?.Id;

var reviewMetrics = new ReviewMetrics(
    cache,
    config.Metrics.TrailingWindowDays,
    config.Metrics.GoalStartDate,
    personalId);

var bugMetrics = new BugMetrics(
    cache,
    config.Metrics.TrailingWindowDays,
    config.Metrics.GoalStartDate);

var dashboard = new Dashboard(cache, clock, reviewMetrics, bugMetrics, config);

while (true)
{
    dashboard.Render();

    var key = Console.ReadKey(intercept: true);
    if (key.Key == ConsoleKey.Q)
    {
        break;
    }
    if (key.Key == ConsoleKey.R)
    {
        await RunSyncAsync(isColdStart: false);
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
