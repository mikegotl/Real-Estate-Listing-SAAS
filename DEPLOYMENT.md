# Listing Studio production deployment

This runbook deploys Listing Studio to Azure Container Apps without storing Azure, database, AI, media or payment credentials in Git. The production workflow is manual, targets the protected GitHub `production` environment and authenticates to Azure with OpenID Connect (OIDC). A pull request or merge never deploys by itself.

## Production topology

- Public Azure Container App for `ListingStudio.Web`, with sticky sessions for Blazor Server.
- Private background Azure Container App for `ListingStudio.Worker`.
- Manual Azure Container Apps Job containing the EF Core migration bundle.
- Azure Database for PostgreSQL Flexible Server on a delegated private subnet with public access disabled.
- Private Azure Blob container accessed with the runtime managed identity; shared-key access and public blobs are disabled.
- Azure Container Registry accessed with managed identity and immutable commit-SHA tags.
- Azure Key Vault with RBAC, soft delete and purge protection. The PostgreSQL password is referenced by the containers and is not copied into their configuration.
- Application Insights backed by Log Analytics. Web and Worker use the runtime managed identity with the `Monitoring Metrics Publisher` role to emit JSON console logs plus OpenTelemetry traces, metrics and logs; local-key ingestion is disabled.

The Bicep template is [infra/main.bicep](infra/main.bicep). Web, Worker and migration images use [Dockerfile.web](Dockerfile.web), [Dockerfile.worker](Dockerfile.worker) and [Dockerfile.migrations](Dockerfile.migrations).

## One-time prerequisites

1. Choose an Azure subscription and a region that supports Azure Container Apps and PostgreSQL Flexible Server.
2. Create a Microsoft Entra application or user-assigned managed identity for GitHub Actions.
3. Give that principal `Contributor` and `User Access Administrator` on the dedicated production resource group. The latter is required only because Bicep creates least-privilege `AcrPull`, `Storage Blob Data Contributor` and `Key Vault Secrets User` assignments for the runtime identity.
4. Add a federated credential whose subject is exactly:

   ```text
   repo:mikegotl/Real-Estate-Listing-SAAS:environment:production
   ```

5. In GitHub, create an environment named `production`, add at least one required reviewer and prevent self-review when the organization plan supports it. Keep deployment branches restricted to `main`.

The OIDC principal needs no client secret. Record these protected GitHub environment secrets:

| Secret | Purpose |
| --- | --- |
| `AZURE_CLIENT_ID` | Entra application or managed-identity client ID |
| `AZURE_TENANT_ID` | Entra tenant ID |
| `AZURE_SUBSCRIPTION_ID` | Target subscription ID |
| `POSTGRES_ADMIN_PASSWORD` | Initial PostgreSQL administrator password |

Record these non-secret GitHub environment variables:

| Variable | Example | Constraint |
| --- | --- | --- |
| `AZURE_RESOURCE_GROUP` | `listing-studio-production` | Dedicated resource group |
| `AZURE_LOCATION` | `eastus2` | Supported Azure region |
| `AZURE_NAME_PREFIX` | `listingstudio` | 3-12 lowercase letters/numbers/hyphens |
| `POSTGRES_ADMIN_LOGIN` | `listingstudioadmin` | PostgreSQL administrator login |

Do not place provider keys in workflow files, Bicep parameter files, image layers, repository variables or deployment summaries.

## Validate before deployment

Pull requests run the normal restore/build/test suite, publish both hosts, compile Bicep, build all three production containers, verify FFmpeg/ffprobe and call each host's liveness endpoint. Run the same core checks locally:

```bash
dotnet restore ListingStudio.slnx
dotnet build ListingStudio.slnx --configuration Release --no-restore
dotnet test ListingStudio.slnx --configuration Release --no-build

docker run --rm -v "$PWD:/workspace" -w /workspace \
  mcr.microsoft.com/azure-cli:2.77.0 \
  az bicep build --file infra/main.bicep --stdout >/dev/null

docker build -f Dockerfile.web -t listingstudio-web:local .
docker build -f Dockerfile.worker -t listingstudio-worker:local .
docker build -f Dockerfile.migrations -t listingstudio-migrations:local .
```

Use `az deployment group what-if` with the same protected parameter values before an infrastructure change. A what-if is read-only but still requires Azure access.

## Deploy

1. Merge a fully reviewed commit to `main`.
2. Open **Actions → Deploy production → Run workflow** from `main` and enter an auditable release reason.
3. Review the protected-environment approval, commit SHA and proposed change. Approve only when production creation or mutation is intended.
4. Watch the workflow through these fail-closed stages:
   - restore, Release build, all tests and publish;
   - Bicep validation;
   - idempotent foundation deployment;
   - Web, Worker and migration image builds pushed with the exact commit SHA;
   - Container App and migration-job revision deployment;
   - one manual migration execution;
   - Worker enablement only after migration succeeds;
   - public Web readiness and Worker revision-health verification.
5. Record the workflow URL, commit SHA, Web URL, migration execution and approver in the release ticket.

The workflow never uses `latest`. Re-running the same commit reuses its immutable tag and EF migrations remain idempotent.

## Provider secrets after the foundation exists

The baseline deployment leaves OpenAI, AI-video, voice and Stripe integrations disabled or unconfigured. Enabling them can spend money and requires explicit owner approval. Add each approved secret to Key Vault, grant no additional identity beyond the existing runtime identity, reference it as a Container Apps Key Vault secret and map the corresponding environment variable with `secretRef`.

Recommended Key Vault names are:

- `openai-api-key`
- `ai-video-api-key`
- `voice-api-key`
- `stripe-secret-key`
- `stripe-webhook-secret`
- `google-client-secret`
- `apple-sign-in-private-key`

Use the Azure portal or a protected administrative shell that does not persist secret values in history. Never pass a secret as a plain Container Apps environment value. Apply the same secret reference to Web and Worker only when that host needs it. Set non-secret model, endpoint, voice, Stripe price and allowance settings separately.

For Stripe, set `Stripe__PublicBaseUrl` to the deployed Web origin and register `https://<web-host>/billing/stripe-webhook` in Stripe test mode first. Do not enable live mode as part of infrastructure deployment.

Google and Apple sign-in are Web-only integrations. Store `Authentication__Google__ClientSecret` and `Authentication__Apple__PrivateKey` as Key Vault-backed Container Apps secrets. Set the non-secret Google client ID and Apple Services ID, Team ID and Key ID as ordinary environment values, then enable each provider with `Authentication__Google__Enabled=true` or `Authentication__Apple__Enabled=true`. Register the exact public callbacks `https://<web-host>/signin-google` and `https://<web-host>/signin-apple` before enabling them. The Web container processes Azure Container Apps' forwarded HTTPS scheme so those provider redirects use the public HTTPS origin.

## Health, logs and routine operations

- `GET /health/live` checks that the host process can answer. Container Apps uses it for startup and liveness probes.
- `GET /health/ready` checks PostgreSQL connectivity and FFmpeg availability. Web ingress and both hosts use it for readiness.
- The Worker exposes health only inside the Container Apps environment; it has no public ingress.
- Application Insights receives correlated ASP.NET Core, HTTP client, trace, metric, exception and log telemetry when `APPLICATIONINSIGHTS_CONNECTION_STRING` is present. The Azure Monitor exporter authenticates with the user-assigned runtime identity identified by `AZURE_CLIENT_ID`; no instrumentation key is accepted for ingestion.
- Container stdout/stderr uses one JSON object per log event in Production and also flows to Log Analytics.

Useful commands:

```bash
az containerapp logs show --name <web-app> --resource-group <resource-group> --follow
az containerapp logs show --name <worker-app> --resource-group <resource-group> --follow
az containerapp revision list --name <web-app> --resource-group <resource-group> --output table
az containerapp job execution list --name <migration-job> --resource-group <resource-group> --output table
```

Alert on sustained readiness failures, repeated Worker restarts, unhandled exceptions, migration failures, PostgreSQL saturation and abnormal provider failure/cost rates before pilot traffic.

## Rollback

Application rollback is revision-based and does not automatically reverse schema changes.

1. Identify the last healthy Web revision and prior immutable Worker image SHA from the preceding successful deployment.
2. Route Web traffic back without deleting the failed revision:

   ```bash
   az containerapp revision list --name <web-app> --resource-group <resource-group> --output table
   az containerapp ingress traffic set --name <web-app> --resource-group <resource-group> \
     --revision-weight <previous-web-revision>=100
   ```

3. Stop new Worker processing, then restore the prior immutable image:

   ```bash
   az containerapp update --name <worker-app> --resource-group <resource-group> --min-replicas 0
   az containerapp update --name <worker-app> --resource-group <resource-group> \
     --image <acr>.azurecr.io/listingstudio-worker:<previous-commit-sha> \
     --min-replicas 1 --max-replicas 1
   ```

4. Verify Web `/health/ready`, Worker revision health and Application Insights before resuming normal traffic.
5. Prefer a forward database fix. Run an EF down migration only after reviewing data-loss risk and taking a verified PostgreSQL restore point. For destructive or incompatible schema incidents, stop the Worker, place the Web app in maintenance, restore PostgreSQL to a separate server, validate it, then change the runtime database configuration through Key Vault/Container Apps.
6. For infrastructure rollback, run `what-if` from the previous known-good Bicep commit and inspect every delete or replacement. Never delete the storage account, Key Vault or PostgreSQL server as an application rollback shortcut.

Purge protection, database backups and blob/container soft delete make destructive recovery intentionally slower. Escalate any data-loss decision to the owner.
