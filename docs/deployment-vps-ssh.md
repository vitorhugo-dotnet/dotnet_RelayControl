# VPS deployment over SSH

GitHub Actions implements this pipeline:

```text
Build -> Test -> Publish GHCR image -> Deploy API to VPS over SSH
```

The automated deployment is intentionally API-only. It copies `deploy/docker-compose.prod.yml` and `deploy/deploy.sh`; it does not provision PostgreSQL, Redis, coturn, nginx, certificates or backups. Use the full stack under `infra/` separately, or provide equivalent external services.

## Workflow behavior

`.github/workflows/vps-ci-cd.yml` runs on pull requests, pushes to `main` and its legacy architecture branch, and manual dispatch.

- Build restores and compiles the configured API project at `services/SonicRelay.Api/SonicRelay.Api.csproj`, failing if that exact path is missing.
- Test discovers every `*Test.csproj`/`*Tests.csproj`, restores it and uploads TRX results.
- Non-PR runs build the canonical root `Dockerfile` and publish `ghcr.io/vitorhugo-java/sonicrelay-api:sha-<commit>`.
- `main` additionally publishes `:latest`.
- Pushes to `main`, and manual runs with `deploy=true`, deploy the immutable SHA image to the production environment.

## GitHub secrets

| Secret | Required | Default/example |
| --- | --- | --- |
| `VPS_HOST` | Yes | VPS hostname or IP |
| `VPS_USER` | Yes | `deploy` |
| `VPS_SSH_KEY` | Yes | Private key for the deployment user |
| `VPS_PORT` | No | `22` |
| `VPS_APP_DIR` | No | `/opt/sonicrelay` |

`GITHUB_TOKEN` publishes the GHCR package. The package must be public for an unauthenticated VPS pull because the deployment script does not perform `docker login`.

## VPS bootstrap

Install Docker Engine and the Docker Compose plugin, then create a restricted deployment user and directory:

```bash
sudo adduser --disabled-password --gecos "" deploy
sudo usermod -aG docker deploy
sudo mkdir -p /opt/sonicrelay
sudo chown -R deploy:deploy /opt/sonicrelay
sudo install -d -m 700 -o deploy -g deploy /home/deploy/.ssh
```

Place the matching public key in `/home/deploy/.ssh/authorized_keys` with mode `600`. Re-login after adding the user to the Docker group.

## Runtime configuration

Create `/opt/sonicrelay/.env` before the first deployment. `deploy.sh` refuses to start without it.

```env
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://+:8080
API_BIND=127.0.0.1:8080

ConnectionStrings__Postgres=Host=postgres.example.internal;Port=5432;Database=sonicrelay;Username=sonicrelay;Password=CHANGE_ME
Redis__ConnectionString=redis.example.internal:6379,password=CHANGE_ME,abortConnect=false

Sessions__CodeTtlMinutes=10
Sessions__CodeHmacKey=CHANGE_ME_TO_A_HIGH_ENTROPY_SECRET
Sessions__MaxViewersPerSession=3
Sessions__CleanupEnabled=true
Sessions__CleanupIntervalSeconds=60
Sessions__DisconnectedParticipantRetentionHours=24
Sessions__ParticipantDisconnectGraceSeconds=15
Swagger__Enabled=false

DeviceIdentity__CredentialHmacKey=CHANGE_ME_TO_A_HIGH_ENTROPY_SECRET
DeviceIdentity__PairingCodeHmacKey=CHANGE_ME_TO_A_HIGH_ENTROPY_SECRET
DeviceIdentity__TokenSigningKey=CHANGE_ME_TO_A_HIGH_ENTROPY_SECRET_32_BYTES_MIN

# Only needed to serve a viewer on an origin other than the published
# https://sonicrelay.hugodotnet.dev. Setting index 0 replaces that default.
#Cors__AllowedOrigins__0=https://viewer.example

DataRetention__Enabled=true
DataRetention__MaxRetentionDays=90
DataRetention__CleanupIntervalHours=24
DataRetention__DeviceIdentityRotationDays=60
# Must equal the PostgreSQL backup window this host actually keeps.
DataRetention__BackupRetentionDays=7
```

The web viewer is a browser client, so it reaches this API cross-origin and the server must name its origin in `Cors__AllowedOrigins__*`. Left unset, the published viewer origin is allowed; get it wrong and every viewer call fails the browser's preflight before it reaches any endpoint. See [security](security.md#browser-origins-cors).

All three `DeviceIdentity__*` keys are required: sessions, signaling and TURN credential issuance authenticate exclusively via the `DeviceBearer` scheme and have no fallback, so device bootstrap and token issuance fail without them. See [device identity configuration](device-identity.md#configuration).

The `DataRetention__*` block defaults are already correct and are shown here so they are visible, not so they get tuned. They are what makes SonicRelay's Google Play Data Safety declaration ("user data is automatically deleted within 90 days") true, so raising `MaxRetentionDays` above 90, setting `BackupRetentionDays` lower than the backup window this host really keeps, or turning `Enabled` off all break a public statement made to users. Read [data retention](data-retention.md) before changing any of them.

The compose service binds to loopback by default. Put nginx, Caddy or another TLS reverse proxy in front of `127.0.0.1:8080` and forward WebSocket upgrades for `/ws/signaling`.

## Database migration

The application does not call `Database.Migrate()` at startup. Apply migrations as a separate release step using the same PostgreSQL connection before starting a schema-dependent image.

The GitHub Actions workflow (`.github/workflows/vps-ci-cd.yml`) does this automatically on every deploy: it builds a self-contained `dotnet ef migrations bundle`, copies it to the VPS alongside `deploy.sh`, and `deploy.sh` runs it against `ConnectionStrings__Postgres` from `.env` before starting the new image (`run_migrations()`). No manual step is needed for deploys that go through that workflow.

To apply migrations by hand instead (e.g. outside the automated pipeline):

```bash
dotnet ef database update \
  --project src/SonicRelay.Infrastructure/SonicRelay.Infrastructure.csproj \
  --startup-project services/SonicRelay.Api/SonicRelay.Api.csproj
```

## Deployment execution

GitHub Actions copies the two deployment files into the app directory and runs:

```bash
cd /opt/sonicrelay
chmod +x deploy.sh
IMAGE=ghcr.io/vitorhugo-java/sonicrelay-api:sha-<commit> ./deploy.sh
```

The script validates `.env`, Docker and Compose; pulls the API image; runs `docker compose up -d --remove-orphans`; prunes dangling images; and prints service status.

## Verification

On the VPS:

```bash
docker compose -f /opt/sonicrelay/docker-compose.prod.yml ps
docker logs --tail 100 sonicrelay-api
curl --fail http://127.0.0.1:8080/health/live
curl --fail http://127.0.0.1:8080/health/ready
```

`/health/live` proves the API process responds. `/health/ready` additionally proves PostgreSQL and Redis are reachable and that the data-retention cleanup has run recently.

Also confirm the retention sweep is alive — it is the only thing keeping the 90-day deletion guarantee true, and it fails silently:

```bash
curl -s http://127.0.0.1:8080/metrics | grep sonicrelay_data_retention
```

`sonicrelay_data_retention_last_success_timestamp` must be non-zero and advance at least daily. Load `observability/prometheus/sonicrelay-alerts.yml` so a stalled cleanup pages instead of going unnoticed.

From outside the VPS, verify TLS and the reverse proxy:

```bash
curl --fail https://stream.example.com/health/ready
```

## Rollback

Redeploy a previous immutable image tag:

```bash
cd /opt/sonicrelay
IMAGE=ghcr.io/vitorhugo-java/sonicrelay-api:sha-<previous-commit> ./deploy.sh
```

Database rollback is separate. Do not downgrade an image across an incompatible schema change without a tested migration rollback or restored backup.

## Backups and restore

This deployment path does not provision PostgreSQL or its backups, but the backup policy is not optional: data deleted from the primary database at 82 days still lives inside any backup taken before the deletion, so an unbounded backup window silently breaks the 90-day guarantee. Keep the backup window at `DataRetention__BackupRetentionDays` (7 days by default), encrypt backups at rest, and treat the two values as a pair — changing one means changing the other.

A restore reintroduces data that had already expired. After any restore, run a retention pass before serving traffic again; restarting the API is enough, since the sweep runs at boot. Verify with `sonicrelay_data_retention_last_success_timestamp` before reopening the reverse proxy. Restore drills belong on a non-production copy that is destroyed afterwards — a forgotten restore environment is an uncontrolled store of user data. See [data retention](data-retention.md#backups).

## Full infrastructure stack

The repository also contains a separate full-stack Compose topology:

```bash
cp infra/.env.prod.example infra/.env.prod
docker compose \
  --env-file infra/.env.prod \
  -f infra/compose.yml \
  -f infra/compose.prod.yml \
  --profile prod \
  up -d
```

It includes API, PostgreSQL, Redis, coturn and nginx. It is not copied or invoked by the GitHub Actions SSH deployment. Review `infra/nginx/default.conf`, `infra/coturn/turnserver.conf`, exposed ports, DNS and every example secret before production use.

TURN/STUN should use DNS-only records and native ports (`3478/udp`, `3478/tcp`, `5349/tcp`, and the configured UDP relay range), not a normal HTTP proxy.
