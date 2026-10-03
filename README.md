# Listing Studio

.NET 10 foundation for a real-estate Listing Studio, implemented as a clean modular monolith with a Blazor Web App, an ASP.NET Core background worker, PostgreSQL persistence, organization tenancy, ASP.NET Core Identity, and a MudBlazor property-management workspace.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (the SDK is pinned in `global.json`)
- Docker with Docker Compose v2 (for PostgreSQL)

## Projects

| Project | Responsibility |
| --- | --- |
| `ListingStudio.Domain` | Framework-independent domain core |
| `ListingStudio.Application` | Use cases and abstraction contracts |
| `ListingStudio.Infrastructure` | PostgreSQL, Identity, Blob Storage, and Stripe adapters |
| `ListingStudio.AI` | OpenAI and voice/TTS adapters |
| `ListingStudio.Video` | AI video provider adapters |
| `ListingStudio.Web` | Blazor/ASP.NET Core web composition root |
| `ListingStudio.Worker` | Background processing composition root |
| `ListingStudio.UnitTests` | Fast tests for inward layers |
| `ListingStudio.IntegrationTests` | Tests of the composed web host |

See [ARCHITECTURE.md](ARCHITECTURE.md) for dependency rules.

## Configure local development

Tracked JSON files contain empty placeholders only. Copy the environment template and replace its example values locally:

```bash
cp .env.example .env
```

Set `POSTGRES_PASSWORD` and the password segment of `PostgreSQL__ConnectionString` to the same local-only value. The connection string is quoted because semicolons have special meaning in a shell. Load the local variables before running .NET commands:

```bash
set -a
source .env
set +a
```

Never commit `.env`, user secrets, access keys, or connection strings. ASP.NET Core reads environment variables with `__` as the section separator. Alternatively, keep development credentials in .NET user secrets:

```bash
dotnet user-secrets init --project src/ListingStudio.Web
dotnet user-secrets set --project src/ListingStudio.Web "OpenAI:ApiKey" "your-key"
```

The same runtime configuration sections are available to both hosts: `PostgreSQL`, `AzureBlobStorage`, `OpenAI`, `AIVideo`, `Voice`, and `Stripe`.

## Run

Start PostgreSQL and restore the repository-local EF Core tool:

```bash
docker compose up -d postgres
docker compose ps
dotnet tool restore
```

Apply the database migration:

```bash
dotnet ef database update \
  --project src/ListingStudio.Infrastructure \
  --startup-project src/ListingStudio.Web
```

Restore, build, and test the full solution:

```bash
dotnet restore ListingStudio.slnx
dotnet build ListingStudio.slnx --no-restore
dotnet test ListingStudio.slnx --no-build
```

Run either host:

```bash
dotnet run --project src/ListingStudio.Web
dotnet run --project src/ListingStudio.Worker
```

Open the Web URL printed by `dotnet run`. Registering creates an Identity user, a new organization, and an owner membership. `/auth` is protected and redirects anonymous users to `/Account/Login`. In Development only, the forgot-password confirmation page displays the locally generated reset link. Configure a production identity email adapter before deploying; reset tokens are never written to logs or source control.

After signing in, open `/properties` to create, list, inspect, edit, and archive properties. Archived properties are read-only and hidden from the default dashboard; select **Show archived** and refresh to include them. Property queries derive organization scope from the authenticated user's membership, and never accept an organization identifier from the browser.

Integration tests require a running Docker daemon. They start a disposable PostgreSQL 17 container and apply the committed migrations automatically.

Stop PostgreSQL with `docker compose down`; add `--volumes` only when you also intend to delete local database data.
