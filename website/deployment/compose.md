# Docker Compose

For a single host without Kubernetes, every release publishes a production Compose stack as an
OCI artifact next to the container image. Running it needs Docker Compose 2.34 or later and nothing
from this repository.

The stack runs one vfps replica. For more than one, or for failover, use the
[Helm chart](production.md).

## What it runs

| Service        | What it does                                                                                                                                                  |
| -------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `postgresql`   | PostgreSQL 18, with its data on the `vfps-production_postgresql-data` volume.                                                                                 |
| `vfps-migrate` | Applies database migrations on every `up`, then exits. vfps only starts once it has succeeded, so a failed migration is a separate, visible failure.          |
| `vfps`         | The API and admin UI, with [OIDC authentication and access control](../access-control.md) turned on, published on `127.0.0.1:8080` (HTTP) and `:8081` (gRPC). |

Every image is pinned to a digest in the published artifact. To see the complete stack Compose will
run, including those digests, replace `up -d` with `config` in the command below.

## Configure

Settings that have no safe default are required variables. Put them in an env file, for example
`vfps.env`, and keep that file out of version control:

```sh
POSTGRES_PASSWORD=<a long random password>
VFPS_OIDC_AUTHORITY=https://idp.example.com/realms/vfps
VFPS_OIDC_CLIENT_ID=vfps
VFPS_OIDC_CLIENT_SECRET=<the client's secret>
VFPS_OIDC_AUDIENCE=vfps
VFPS_ADMIN_ROLE=vfps-admin
# optional: the host address the ports are published on, 127.0.0.1 if unset
# VFPS_BIND_ADDRESS=127.0.0.1
```

| Variable                  | Becomes                        | Meaning                                                                                                    |
| ------------------------- | ------------------------------ | ---------------------------------------------------------------------------------------------------------- |
| `POSTGRES_PASSWORD`       | the `postgres` user's password | Only takes effect on the first start, when the database is created. Changing it later needs `ALTER ROLE`.  |
| `VFPS_OIDC_AUTHORITY`     | `Authorization__Authority`     | The HTTPS issuer URL of your identity provider.                                                            |
| `VFPS_OIDC_CLIENT_ID`     | `Authorization__ClientId`      | The confidential client the admin UI signs in with.                                                        |
| `VFPS_OIDC_CLIENT_SECRET` | `Authorization__ClientSecret`  | That client's secret.                                                                                      |
| `VFPS_OIDC_AUDIENCE`      | `Authorization__Audience`      | The audience of the bearer tokens gRPC and REST callers send. If it's missing, every API call is rejected. |
| `VFPS_ADMIN_ROLE`         | `Authorization__AdminRoles__0` | The role that grants full access. Everything else is granted from the admin UI.                            |
| `VFPS_BIND_ADDRESS`       | the host side of both ports    | Defaults to `127.0.0.1`. See [TLS](#tls) before changing it.                                               |

The [configuration reference](../configuration.md) describes the `Authorization__*` settings in
detail.

## Start

<!-- x-release-please-start-version -->

```sh
docker compose --env-file vfps.env \
  -f oci://ghcr.io/miracum/vfps/compose/production:v1.22.1 \
  up -d
```

<!-- x-release-please-end -->

Compose lists the variables it resolved and asks before it starts anything. Pass `--yes` to `up`
when running it from a script. If a required variable is missing, Compose stops before creating any
container and names the variable.

Later commands can refer to the stack by its project name, without the artifact or the env file:

```sh
docker compose -p vfps-production ps
docker compose -p vfps-production logs vfps
```

## TLS

vfps serves plain HTTP on 8080 and plaintext gRPC on 8081. Put a reverse proxy that terminates TLS
in front of both, and set `VFPS_BIND_ADDRESS` only if the proxy runs on another host. vfps
trusts the proxy's `X-Forwarded-*` headers from any address, so the proxy must be the only way to
reach vfps.

## Changing anything else

Any other setting goes in an override file, passed as a second `-f` after the artifact. Compose
merges it into the published stack:

```yaml
# vfps.override.yaml
services:
  vfps:
    environment:
      Authorization__AccessTokens__IsEnabled: "true"
      Tracing__IsEnabled: "true"
      Tracing__Otlp__Endpoint: "http://otel-collector.example.com:4317"
```

<!-- x-release-please-start-version -->

```sh
docker compose --env-file vfps.env \
  -f oci://ghcr.io/miracum/vfps/compose/production:v1.22.1 \
  -f vfps.override.yaml \
  up -d
```

<!-- x-release-please-end -->

Use the same files every time you run `up`. Leaving out the override reverts its settings.

For example, the [Data Protection key ring](data-protection.md) is stored unencrypted in the
database unless you give vfps a certificate. Mount the pair
[generated there](data-protection.md#generating-a-certificate) as secrets, and make sure both files
are readable by the container's user, uid `65534`:

```yaml
# vfps.override.yaml
services:
  vfps:
    environment:
      DataProtection__Certificates__0__Path: /run/secrets/vfps-data-protection.crt
      DataProtection__Certificates__0__KeyPath: /run/secrets/vfps-data-protection.key
    secrets:
      - vfps-data-protection.crt
      - vfps-data-protection.key

secrets:
  vfps-data-protection.crt:
    file: ./tls.crt
  vfps-data-protection.key:
    file: ./tls.key
```

## Upgrade

Back up the database first:

```sh
docker compose -p vfps-production exec -T postgresql pg_dump -U postgres vfps > vfps-$(date +%F).sql
```

Then run the same `up -d` command with the new version's tag. `vfps-migrate` applies the new
release's migrations before the new vfps container starts. Migrations only go forward. To return to
an older release after an upgrade, restore the backup.

## Stop

```sh
docker compose -p vfps-production down
```

This keeps the database volume. Adding `--volumes` deletes it, and every pseudonym with it.

## Verify the artifact

The artifact is signed with cosign and has a build provenance attestation, like the container
images. See [Security](../security.md#compose-artifact-verification).
