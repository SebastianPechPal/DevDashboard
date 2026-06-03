using Microsoft.Extensions.Configuration;

namespace CLIGoalHelper.Config;

public sealed class AppConfig
{
    public required AzureDevOpsConfig AzureDevOps { get; init; }
    public required List<string> RequiredReviewers { get; init; }
    public List<string> ExcludedFromStatsRepos { get; init; } = new();
    public required MetricsConfig Metrics { get; init; }
    public PrAgentConfig PrAgent { get; init; } = new();

    public static AppConfig Load()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.local.json", optional: true)
            .Build();

        var bound = configuration.Get<AppConfig>()
            ?? throw new InvalidOperationException("Failed to bind appsettings.json");

        return bound;
    }
}

public sealed class AzureDevOpsConfig
{
    public required string OrganizationUrl { get; init; }
    public required string BoardsProject { get; init; }
    public required List<string> CandidateProjects { get; init; }

    // Tracked repos mapped to their local working-copy path. An empty/missing value means the
    // repo is tracked but has no local checkout (the 'c' background-agent launch then refuses).
    public required Dictionary<string, string> Repositories { get; init; }
}

public sealed class PrAgentConfig
{
    // Prompt handed to `claude --bg`; {URL} is replaced with the selected PR's web URL.
    public string PromptTemplate { get; init; } =
        "Focus on this PR {URL}. Fetch azure infos and wait for next input";
}

public sealed class MetricsConfig
{
    public required int SlaBusinessHours { get; init; }
    public required int TrailingWindowDays { get; init; }
    public required int BackfillDays { get; init; }
    public required DateTime GoalStartDate { get; init; }
}
