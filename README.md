# Listing Studio

Initial .NET 10 foundation for a real-estate Listing Studio, implemented as a clean modular monolith with a Blazor Web App, an ASP.NET Core background worker, and separate technology adapters. No business features are included yet.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (the SDK is pinned in `global.json`)
- Docker with Docker Compose v2 (for PostgreSQL)

## Projects

| Project | Responsibility |
| --- | --- |
| `ListingStudio.Domain` | Framework-independent domain core |
| `ListingStudio.Application` | Use cases and abstraction contracts |
| `ListingStudio.Infrastructure` | PostgreSQL, Blob Storage, and Stripe adapters |
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

Never commit `.env`, user secrets, access keys, or connection strings. ASP.NET Core reads environment variables with `__` as the section separator. Alternatively, keep development credentials in .NET user secrets:

```bash
dotnet user-secrets init --project src/ListingStudio.Web
dotnet user-secrets set --project src/ListingStudio.Web "OpenAI:ApiKey" "your-key"
```

The same runtime configuration sections are available to both hosts: `PostgreSQL`, `AzureBlobStorage`, `OpenAI`, `AIVideo`, `Voice`, and `Stripe`.

## Run

Start PostgreSQL (requires `POSTGRES_PASSWORD` in `.env`):

```bash
docker compose up -d postgres
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

Stop PostgreSQL with `docker compose down`; add `--volumes` only when you also intend to delete local database data.
