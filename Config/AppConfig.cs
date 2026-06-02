using Microsoft.Extensions.Configuration;

namespace CLIGoalHelper.Config;

public sealed class AppConfig
{
    public required AzureDevOpsConfig AzureDevOps { get; init; }
    public required List<string> RequiredReviewers { get; init; }
    public List<string> ExcludedFromStatsRepos { get; init; } = new();
    public required MetricsConfig Metrics { get; init; }

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
    public required List<string> Repositories { get; init; }
}

public sealed class MetricsConfig
{
    public required int SlaBusinessHours { get; init; }
    public required int TrailingWindowDays { get; init; }
    public required int BackfillDays { get; init; }
    public required DateTime GoalStartDate { get; init; }
}
