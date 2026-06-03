using Microsoft.Extensions.Configuration;

namespace DevDashboard.Config;

public sealed class AppConfig
{
    public required AzureDevOpsConfig AzureDevOps { get; init; }
    public required List<string> RequiredReviewers { get; init; }
    public List<string> ExcludedFromStatsRepos { get; init; } = new();
    public required MetricsConfig Metrics { get; init; }
    public PrAgentConfig PrAgent { get; init; } = new();

    // The per-user directory under %LOCALAPPDATA% that holds both the config and the
    // SQLite cache. Kept out of the repo so no personal data is ever committed.
    public static string ConfigDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevDashboard");

    public static AppConfig Load()
    {
        var configPath = Path.Combine(ConfigDirectory, "appsettings.json");

        // First run: no config yet. Seed one from the bundled template, point the user at it,
        // and stop — running against placeholder values would only produce confusing errors.
        if (!File.Exists(configPath))
        {
            SeedConfigFromTemplate(configPath);
            Console.WriteLine(
                $"No configuration found. A starter config was created at:\n  {configPath}\n" +
                "Fill in your Azure DevOps organization, repositories and reviewers, then run again.");
            Environment.Exit(1);
        }

        var configuration = new ConfigurationBuilder()
            .AddJsonFile(configPath, optional: false)
            .AddJsonFile(Path.Combine(ConfigDirectory, "appsettings.local.json"), optional: true)
            .Build();

        return configuration.Get<AppConfig>()
            ?? throw new InvalidOperationException($"Failed to bind {configPath}");
    }

    private static void SeedConfigFromTemplate(string configPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        var template = Path.Combine(AppContext.BaseDirectory, "appsettings.template.json");
        if (File.Exists(template))
        {
            File.Copy(template, configPath);
        }
    }
}

public sealed class AzureDevOpsConfig
{
    public required string OrganizationUrl { get; init; }
    public required string BoardsProject { get; init; }
    public required List<string> CandidateProjects { get; init; }

    // Email of the signed-in (az login) user, used to surface their own review activity
    // separately in the dashboard. Leave empty to disable the personal breakdown.
    public string CurrentUserEmail { get; init; } = "";

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
