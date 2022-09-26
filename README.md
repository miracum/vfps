# vfps

A very fast and resource-efficient pseudonym service.
P99 request latencies of 11 ms at below 1GiB of RAM utilization.
Horizontal service replication is possible for highly-available deployments.

## Run it

> **Warning**
> Using the provided docker-compose.yaml is not a production-ready deployment but merely
> used to get started and testing quickly.
> It sets strict and low resource limits, uses the `latest` tag, runs database migrations as part
> of the startup, and uses the default password for an included, unoptimized PostgreSQL deployment.

```sh
docker compose -f docker-compose.yaml --profile=test up
```

Visit <https://localhost:8080/> to view the OpenAPI specification of the Vfps API:

![Screenshot of the OpenAPI specification](docs/img/openapi.png)

<!-- ## Production-grade deployment

See <https://github.com/chgl/charts/tree/master/charts/vfps> for a production-grade deployment on Kubernetes using Helm. -->

## Observability

The service exports metrics in Prometheus format on `/metrics`.
Health-, readiness-, and liveness-probes are exposed at `/healthz`, `/readyz`, and `/livez` respectively.

## Benchmark

Create a pseudonym namespace used for benchmarking:

```sh
grpcurl -insecure \
    -d '{"name": "benchmark", "pseudonymGenerationMethod": "PSEUDONYM_GENERATION_METHOD_SECURE_RANDOM_BASE64URL_ENCODED", "pseudonymLength": 32}' \
    0.0.0.0:7078 \
    vfps.api.v1.NamespaceService/Create
```

Generate 100.000 pseudonyms in the namespace from random original values:

```sh
ghz -n 100000 \
    --skipTLS \
    --call vfps.api.v1.PseudonymService/Create \
    -d '{"originalValue": "{{randomString 32}}", "namespace": "benchmark"}' \
    0.0.0.0:7078
```

Sample output running on

```console
OS=Windows 11 (10.0.22000.978/21H2)
12th Gen Intel Core i9-12900K, 1 CPU, 24 logical and 16 physical cores
32GiB of DDR4 4800MHz RAM
Samsung SSD 980 Pro 1TiB
PostgreSQL running in WSL2 VM on the same machine.
.NET SDK=7.0.100-rc.1.22431.12
```

```console
Summary:
  Count:        100000
  Total:        16.68 s
  Slowest:      187.81 ms
  Fastest:      2.52 ms
  Average:      8.00 ms
  Requests/sec: 5993.51

Response time histogram:
  2.522   [1]     |
  21.051  [99748] |∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎
  39.580  [201]   |
  58.109  [0]     |
  76.639  [0]     |
  95.168  [0]     |
  113.697 [0]     |
  132.226 [0]     |
  150.755 [0]     |
  169.285 [0]     |
  187.814 [50]    |

Latency distribution:
  10 % in 6.26 ms
  25 % in 6.91 ms
  50 % in 7.72 ms
  75 % in 8.93 ms
  90 % in 9.57 ms
  95 % in 10.01 ms
  99 % in 11.86 ms

Status code distribution:
  [OK]   100000 responses
```

## Development

### Prerequisites

.NET 7.0: <https://dotnet.microsoft.com/en-us/download/dotnet>
Docker CLI 20.10.17: <https://www.docker.com/>
Docker Compose: <https://docs.docker.com/compose/install/>

### Build & Run

Start an empty PostgreSQL database for development (optionally add `-d` to run in the background):

```sh
docker compose -f docker-compose.yaml up
```

Restore dependencies and run in Debug mode with the [HTTPS profile](src/Vfps/Properties/launchSettings.json):

```sh
dotnet restore
dotnet run -c Debug --launch-profile=https --project=src/Vfps
```

Open <https://localhost:7078/> or <http://localhost:5119/> to see the OpenAPI UI for the JSON-transcoded gRPC services.
You can also use [grpcurl](https://github.com/fullstorydev/grpcurl) to interact with the API.

### Build container images

#### Main VFPS service

```sh
docker build -t ghcr.io/chgl/vfps:latest .
```

#### VFPS database migration container

```sh
docker build -t ghcr.io/chgl/vfps-migrations:latest --target=migrations .
```
