# vfps

![Latest Version](https://img.shields.io/github/v/release/miracum/vfps)
![License](https://img.shields.io/github/license/miracum/vfps)
[![OpenSSF Scorecard](https://api.securityscorecards.dev/projects/github.com/miracum/vfps/badge)](https://api.securityscorecards.dev/projects/github.com/miracum/vfps)
[![SLSA 3](https://slsa.dev/images/gh-badge-level3.svg)](https://slsa.dev)

A [very fast](#e2e-load-testing) and [resource-efficient](#resource-efficiency) pseudonym service.

Supports horizontal service replication for highly-available deployments.

## Run it

> **Warning**
> Using the provided docker-compose.yaml is not a production-ready deployment but merely
> used to get started and testing quickly.
> It sets very restrictive resource limits uses the default password for an included,
> unoptimized PostgreSQL deployment.

```sh
docker compose -f docker-compose.yaml --profile=test up
```

Visit <http://localhost:8080/> to view the OpenAPI specification of the Vfps API:

![Screenshot of the OpenAPI specification](docs/img/openapi.png)

You can use the JSON-transcoded REST API described via OpenAPI or interact with the service using gRPC.
For example, using [grpcurl](https://github.com/fullstorydev/grpcurl) to create a new namespace:

```sh
grpcurl \
  -plaintext \
  -import-path src/Vfps/ \
  -proto src/Vfps/Protos/vfps/api/v1/namespaces.proto \
  -d '{"name": "test", "pseudonymGenerationMethod": "PSEUDONYM_GENERATION_METHOD_SECURE_RANDOM_BASE64URL_ENCODED", "pseudonymLength": 32}' \
  127.0.0.1:8081 \
  vfps.api.v1.NamespaceService/Create
```

And to create a new pseudonym inside this namespace:

```sh
grpcurl \
  -plaintext \
  -import-path src/Vfps/ \
  -proto src/Vfps/Protos/vfps/api/v1/pseudonyms.proto \
  -d '{"namespace": "test", "originalValue": "to be pseudonymized"}' \
  127.0.0.1:8081 \
  vfps.api.v1.PseudonymService/Create
```

## Production-grade deployment

See <https://github.com/miracum/charts/tree/master/charts/vfps> for a production-grade deployment on Kubernetes via Helm.

## Configuration

Available configuration options which can be set as environment variables:

| Variable                                           | Type         | Default             | Description                                                                                                                   |
| -------------------------------------------------- | ------------ | ------------------- | ----------------------------------------------------------------------------------------------------------------------------- |
| `ConnectionStrings__PostgreSQL`                    | `string`     | `""`                | Connection string to the PostgreSQL database. See <https://www.npgsql.org/doc/connection-string-parameters.html> for options. |
| `ForceRunDatabaseMigrations`                       | `bool`       | `false`             | Run database migrations as part of the startup. Only recommended when a single replica of the application is used.            |
| `Tracing__IsEnabled`                               | `bool`       | `false`             | Enable distributed tracing support.                                                                                           |
| `Tracing__Exporter`                                | `string`     | `"jaeger"`          | The tracing export format. One of `jaeger`, `otlp`.                                                                           |
| `Tracing__ServiceName`                             | `string`     | `"vfps"`            | Tracing service name.                                                                                                         |
| `Tracing__RootSampler`                             | `string`     | `"AlwaysOnSampler"` | Tracing parent root sampler. One of `AlwaysOnSampler`, `AlwaysOffSampler`, `TraceIdRatioBasedSampler`                         |
| `Tracing__SamplingProbability`                     | `double`     | `0.1`               | Sampling probability to use if `Tracing__RootSampler` is set to `TraceIdRatioBasedSampler`.                                   |
| `Tracing__Jaeger`                                  | `object`     | `{}`                | Jaeger exporter options.                                                                                                      |
| `Tracing__Otlp__Endpoint`                          | `string`     | `""`                | The OTLP gRPC Endpoint URL.                                                                                                   |
| `Pseudonymization__Caching__Namespaces__IsEnabled` | `bool`       | `false`             | Set to `true` to enable namespace caching.                                                                                    |
| `Pseudonymization__Caching__Pseudonyms__IsEnabled` | `bool`       | `false`             | Set to `true` to enable pseudonym caching.                                                                                    |
| `Pseudonymization__Caching__SizeLimit`             | `int`        | `65534`             | Maximum number of entries in the cache. The cache is shared between the pseudonyms and namespaces.                            |
| `Pseudonymization__Caching__AbsoluteExpiration`    | `D.HH:mm:nn` | `0.01:00:00`        | Time after which a cache entry expires.                                                                                       |

## Observability

The service exports metrics in Prometheus format on `:8082/metrics`.
Health-, readiness-, and liveness-probes are exposed at `:8080/healthz`, `:8080/readyz`, and `:8080/livez` respectively.

## FHIR operations

The service also exposes a FHIR operations endpoint. Sending a FHIR Parameters resource to `/v1/fhir/$create-pseudonym`
of the following schema:

```json
{
  "resourceType": "Parameters",
  "parameter": [
    {
      "name": "namespace",
      "valueString": "test"
    },
    {
      "name": "originalValue",
      "valueString": "hello world"
    }
  ]
}
```

will create a pseudonym in the `test` namespace. The expected response looks as follows:

```json
{
  "resourceType": "Parameters",
  "parameter": [
    {
      "name": "namespace",
      "valueString": "test"
    },
    {
      "name": "originalValue",
      "valueString": "hello world"
    },
    {
      "name": "pseudonymValue",
      "valueString": "8KWwnm3TXR5R9iUDVVKD-jUezE4DEyeydOeq4v_a_b5ejSLmqOlT8g"
    }
  ]
}
```

## Development

### Prerequisites

- .NET 7.0: <https://dotnet.microsoft.com/en-us/download/dotnet>
- Docker CLI 20.10.17: <https://www.docker.com/>
- Docker Compose: <https://docs.docker.com/compose/install/>

### Build & run

Start an empty PostgreSQL database for development (optionally add `-d` to run in the background):

```sh
docker compose -f docker-compose.yaml up
```

To additionally start an instance of [Jaeger Tracing](https://www.jaegertracing.io/), you can specify the `jaeger`
profile:

```sh
docker compose -f docker-compose.yaml --profile=jaeger up
```

Restore dependencies and run in Debug mode:

```sh
dotnet restore
dotnet run -c Debug --project=src/Vfps
```

Open <https://localhost:8080/> to see the OpenAPI UI for the JSON-transcoded gRPC services.
You can also use [grpcurl](https://github.com/fullstorydev/grpcurl) to interact with the API:

> **Note**
> In development mode gRPC reflection is enabled and used by grpcurl by default.

```sh
grpcurl -plaintext \
    -d '{"name": "test", "pseudonymGenerationMethod": "PSEUDONYM_GENERATION_METHOD_SECURE_RANDOM_BASE64URL_ENCODED", "pseudonymLength": 32}' \
    127.0.0.1:8081 \
    vfps.api.v1.NamespaceService/Create

grpcurl -plaintext \
    -d '{"namespace": "test", "originalValue": "a test value"}' \
    127.0.0.1:8081 \
    vfps.api.v1.PseudonymService/Create
```

#### Run unit tests

```sh
dotnet test src/Vfps.Tests \
  --configuration=Release \
  --collect:"XPlat Code Coverage" \
  --results-directory=./coverage \
  -l "console;verbosity=detailed" \
  --settings=src/Vfps.Tests/runsettings.xml
```

#### Generate Code coverage report

If not installed, install the report generation too:

```sh
dotnet tool install -g dotnet-reportgenerator-globaltool
```

```sh
reportgenerator -reports:"./coverage/*/coverage.cobertura.xml" -targetdir:"coveragereport" -reporttypes:Html
# remove the coverage directory so successive runs won't cause issues with their random GUID.
# See <https://github.com/microsoft/vstest/issues/2378>
rm -rf coverage/
```

### Build container image

```sh
docker build -t ghcr.io/miracum/vfps:latest .
```

### Run iter8 SLO experiments locally

```sh
kind create cluster

export IMAGE_TAG="iter8-test"

docker build -t ghcr.io/miracum/vfps:${IMAGE_TAG} .

kind load docker-image ghcr.io/miracum/vfps:${IMAGE_TAG}

helm upgrade --install \
  --set="image.tag=${IMAGE_TAG}" \
  -f tests/iter8/values.yaml \
  --wait \
  --timeout=15m \
  --version=^1.0.0 \
  vfps oci://ghcr.io/miracum/charts/vfps

kubectl apply -f tests/iter8/experiment.yaml

iter8 k assert -c completed --timeout 15m
iter8 k assert -c nofailure,slos
iter8 k report
```

## Benchmarks

### Micro benchmarks

The pseudonym generation methods are continuously benchmarked. Results are viewable at <https://miracum.github.io/vfps/dev/bench/>.

### E2E load testing

Create a pseudonym namespace used for benchmarking:

```sh
grpcurl \
  -plaintext \
  -import-path src/Vfps/ \
  -proto src/Vfps/Protos/vfps/api/v1/namespaces.proto \
  -d '{"name": "benchmark", "pseudonymGenerationMethod": "PSEUDONYM_GENERATION_METHOD_SECURE_RANDOM_BASE64URL_ENCODED", "pseudonymLength": 32}' \
  127.0.0.1:8081 \
  vfps.api.v1.NamespaceService/Create
```

Generate 100.000 pseudonyms in the namespace from random original values:

```sh
ghz -n 100000 \
    --insecure \
    --import-paths src/Vfps/ \
    --proto src/Vfps/Protos/vfps/api/v1/pseudonyms.proto \
    --call vfps.api.v1.PseudonymService/Create \
    -d '{"originalValue": "{{randomString 32}}", "namespace": "benchmark"}' \
    127.0.0.1:8081
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

### Sub-10ms P99-latency

By default, each pseudonym creation requests executes two database queries: one to fetch the namespace configuration
and a second one to persist the pseudonym if it doesn't already exist. There is an opt-in way to avoid the first
query by caching the namespaces in a non-distributed in-memory cache. It can be enabled and configured using the following
environment variables:

| Variable                                           | Type         | Default      | Description                                |
| -------------------------------------------------- | ------------ | ------------ | ------------------------------------------ |
| `Pseudonymization__Caching__Namespaces__IsEnabled` | `bool`       | `false`      | Set to `true` to enable namespace caching. |
| `Pseudonymization__Caching__SizeLimit`             | `int`        | `32`         | Maximum number of entries in the cache.    |
| `Pseudonymization__Caching__AbsoluteExpiration`    | `D.HH:mm:nn` | `0.01:00:00` | Time after which a cache entry expires.    |

> **Warning**
> Deleting a namespace does not automatically remove it from the in-memory cache.
> Pseudonym creation requests against such a stale cached namespace will fail until
> either the entry expired or the service is restarted.

Using the same setup as above but with namespace caching enabled, we can lower the per-request latencies and increase throughput:

```console
Summary:
  Count:        100000
  Total:        11.70 s
  Slowest:      27.23 ms
  Fastest:      1.55 ms
  Average:      5.47 ms
  Requests/sec: 8549.22

Response time histogram:
  1.546  [1]     |
  4.114  [17418] |∎∎∎∎∎∎∎∎∎∎
  6.682  [72827] |∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎
  9.251  [9382]  |∎∎∎∎∎
  11.819 [122]   |
  14.387 [0]     |
  16.956 [0]     |
  19.524 [0]     |
  22.092 [0]     |
  24.661 [49]    |
  27.229 [201]   |

Latency distribution:
  10 % in 4.00 ms
  25 % in 4.34 ms
  50 % in 5.81 ms
  75 % in 6.00 ms
  90 % in 6.66 ms
  95 % in 7.00 ms
  99 % in 8.00 ms

Status code distribution:
  [OK]   100000 responses
```

### Resource efficiency

The sample deployment described in [docker-compose.yaml](docker-compose.yaml) sets strict resource
limits for both the CPU (1 CPU) and memory (max 128MiB). Even under these constraints > 1k RPS are
possible, although with significantly increased P99 latencies:

```console
Summary:
  Count:        100000
  Total:        73.99 s
  Slowest:      268.06 ms
  Fastest:      5.26 ms
  Average:      36.69 ms
  Requests/sec: 1351.51

Response time histogram:
  5.257   [1]     |
  31.537  [57298] |∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎
  57.817  [21327] |∎∎∎∎∎∎∎∎∎∎∎∎∎∎∎
  84.097  [17685] |∎∎∎∎∎∎∎∎∎∎∎∎
  110.377 [3395]  |∎∎
  136.656 [243]   |
  162.936 [0]     |
  189.216 [1]     |
  215.496 [0]     |
  241.776 [0]     |
  268.055 [50]    |

Latency distribution:
  10 % in 14.62 ms
  25 % in 18.47 ms
  50 % in 29.46 ms
  75 % in 47.53 ms
  90 % in 71.96 ms
  95 % in 79.95 ms
  99 % in 97.22 ms

Status code distribution:
  [OK]   100000 responses
```

## Image signature and provenance verification

Prerequisites:

- [cosign](https://github.com/sigstore/cosign/releases)
- [slsa-verifier](https://github.com/slsa-framework/slsa-verifier/releases)
- [crane](https://github.com/google/go-containerregistry/releases)

All released container images are signed using [cosign](https://github.com/sigstore/cosign) and SLSA Level 3 provenance is available for verification.

<!-- x-release-please-start-version -->

```sh
IMAGE=ghcr.io/miracum/vfps:v1.3.6
DIGEST=$(crane digest "${IMAGE}")
IMAGE_DIGEST_PINNED="ghcr.io/miracum/vfps@${DIGEST}"
IMAGE_TAG="${IMAGE#*:}"

cosign verify \
   --certificate-oidc-issuer=https://token.actions.githubusercontent.com \
   --certificate-identity-regexp="https://github.com/miracum/.github/.github/workflows/standard-build.yaml@.*" \
   --certificate-github-workflow-name="ci" \
   --certificate-github-workflow-repository="miracum/vfps" \
   --certificate-github-workflow-trigger="release" \
   --certificate-github-workflow-ref="refs/tags/${IMAGE_TAG}" \
   "${IMAGE_DIGEST_PINNED}"

slsa-verifier verify-image \
    --source-uri github.com/miracum/vfps \
    --source-tag ${IMAGE_TAG} \
    "${IMAGE_DIGEST_PINNED}"
```

See also <https://github.com/slsa-framework/slsa-github-generator/tree/main/internal/builders/container#verification> for details on verifying the image integrity using automated policy controllers.
