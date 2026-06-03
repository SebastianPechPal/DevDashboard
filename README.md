# DevDashboard

A terminal dashboard for Azure DevOps that tracks **pull-request review
turnaround** and **bug / defect metrics** across a fixed set of repositories.
Built with .NET 9 and [Spectre.Console](https://spectreconsole.net/).

## Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download)
- [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli) — sign in
  once with `az login`. Auth uses your existing CLI session (`AzureCliCredential`);
  there is **no PAT or token to configure**.
- Access to the Azure DevOps organization you want to track.
- *(Optional)* the `claude` CLI on your `PATH`, if you want the `c` key to launch
  a background agent for a PR.

## Setup

1. **Clone and run once** to generate your config:

   ```sh
   git clone https://github.com/SebastianPechPal/DevDashboard.git
   cd DevDashboard
   dotnet run
   ```

   On first run the app creates a starter config from
   [`appsettings.template.json`](appsettings.template.json) at:

   ```
   %LOCALAPPDATA%\DevDashboard\appsettings.json
   ```

   ...then exits and tells you the path.

2. **Edit that file** with your own values (the template is fully commented):
   - `AzureDevOps:OrganizationUrl` — e.g. `https://dev.azure.com/your-org`
   - `AzureDevOps:BoardsProject` — the project whose board the bug metrics read from
   - `AzureDevOps:CandidateProjects` — projects searched when discovering repos
   - `AzureDevOps:CurrentUserEmail` — your sign-in email (surfaces *your* review activity)
   - `AzureDevOps:Repositories` — repo name → local working-copy path (use `""` if you
     have no local checkout; the path is where the `c` background-agent launches)
   - `RequiredReviewers` — display names whose first vote the turnaround metric measures
   - `Metrics` — SLA hours, trailing window, backfill window, goal start date

3. **Sign in and run:**

   ```sh
   az login
   dotnet run
   ```

   The first real run does a cold-start backfill (~2–4 min); later runs sync incrementally.

## Keys

| Key | Action |
|-----|--------|
| `tab` | switch focused panel (Open PRs ↔ Bugs) |
| `j` / `k` (or ↓ / ↑) | move selection |
| `enter` | open the selected PR / bug in the browser |
| `c` | launch a background `claude` agent for the selected PR |
| `C` | same, with an extra one-off instruction |
| `r` | refresh (incremental sync) |
| `q` | quit |

## Data locations

Everything personal lives outside the repo, under `%LOCALAPPDATA%\DevDashboard\`:

- `appsettings.json` — your configuration (never committed)
- `cache.db` — local SQLite cache of PRs, identities and bugs

An optional `appsettings.local.json` in the same folder overrides individual settings.
