# Performance

## Micro benchmarks

The pseudonym generation methods are continuously benchmarked. Results are viewable at <https://miracum.github.io/vfps/dev/bench/>.

## Pseudonym generation benchmarks

Create a pseudonym namespace used for benchmarking:

```sh
grpcurl \
  -plaintext \
  -d '{"name": "benchmark", "pseudonymGenerationMethod": "PSEUDONYM_GENERATION_METHOD_SECURE_RANDOM_BASE64URL_ENCODED", "pseudonymLength": 32}' \
  127.0.0.1:8081 \
  vfps.api.v1.NamespaceService/Create
```

Generate 100.000 pseudonyms in the namespace from random original values:

```sh
ghz -n 100000 \
    --insecure \
    --call vfps.api.v1.PseudonymService/Create \
    -d '{"originalValue": "{{randomString 32}}", "namespace": "benchmark"}' \
    127.0.0.1:8081
```

Sample output running on

```console
OS=Windows 11 (10.0.26300.9457/26H2)
12th Gen Intel Core i9-12900K, 1 CPU, 24 logical and 16 physical cores
32GiB of DDR5 4800MHz RAM
Samsung SSD 980 Pro 1TiB
vfps and PostgreSQL 18.6 (Windows x64 binaries) running natively on the same machine.
.NET SDK=10.0.401, ASP.NET Core Runtime=10.0.12
vfps Release build (v1.21.0), ASPNETCORE_ENVIRONMENT=Production, Authorization__IsEnabled=false, MetricsPort=0
ghz v0.121.0
```

| Setup                     | Namespace caching | Requests/sec  | P50          | P99          |
| ------------------------- | ----------------- | ------------- | ------------ | ------------ |
| WSL2 + Docker             | off               | 6,586         | 6.69 ms      | 13.37 ms     |
| WSL2 + Docker             | on                | 7,955         | 5.82 ms      | 10.80 ms     |
| Native, `fdatasync`       | on                | 11,787–11,986 | 3.81–3.85 ms | 8.80–9.03 ms |
| Native, `open_datasync`   | off               | 15,396–15,431 | 2.35–2.36 ms | 5.17–5.18 ms |
| Native, `open_datasync`   | on                | 17,389–18,284 | 1.88–2.02 ms | 4.58–4.72 ms |

The native results are the range over two to three consecutive runs of the `ghz` command above, each after at least one
warm-up run. The first run after starting vfps is noticeably slower (10,961 req/s and a P99 of 7.62 ms without namespace
caching) while the .NET JIT is still optimizing the hot paths.

> **Warning**
> PostgreSQL on Windows defaults to `wal_sync_method = open_datasync`. On an SSD without power-loss protection, like the
> Samsung 980 Pro used here, this only writes the WAL into the drive's volatile write cache: `pg_test_fsync` measures 24 µs
> per 8 kB write with `open_datasync` versus 2.1 ms with `fdatasync`. A power failure can then lose pseudonyms that vfps
> already returned to its clients. Set `wal_sync_method = fdatasync`, as in the configuration below, unless the drive has
> power-loss protection or its write cache is disabled.

### PostgreSQL setup

Initialize and start a cluster using the [PostgreSQL Windows x64 binaries](https://www.enterprisedb.com/download-postgresql-binaries)
with their `bin` directory on the `PATH`, where `pwfile.txt` contains the superuser password (`postgres`):

```powershell
initdb -D pgdata -U postgres --pwfile=pwfile.txt --auth=scram-sha-256 -E UTF8 --locale=C
Add-Content -Path pgdata/postgresql.conf -Value "include 'vfps-bench.conf'"
pg_ctl -D pgdata -l pgdata/server.log start
psql -h 127.0.0.1 -p 35432 -U postgres -c "CREATE DATABASE vfps;"
```

`pgdata/vfps-bench.conf` is tuned for the machine above:

```ini
listen_addresses = '127.0.0.1'
port = 35432
max_connections = 200

# memory
shared_buffers = 2GB
effective_cache_size = 16GB
work_mem = 16MB
maintenance_work_mem = 512MB
huge_pages = off

# WAL / checkpoints: keep checkpoints out of the benchmark window
wal_buffers = 64MB
min_wal_size = 2GB
max_wal_size = 16GB
checkpoint_timeout = 30min
checkpoint_completion_target = 0.9
wal_writer_delay = 10ms
# Windows defaults to open_datasync, which only reaches the SSD's volatile write cache.
# fdatasync forces a real flush so acknowledged pseudonyms survive a power loss.
wal_sync_method = fdatasync

# planner (SSD, short OLTP queries)
random_page_cost = 1.1
jit = off
```

### vfps setup

```powershell
dotnet publish src/Vfps/Vfps.csproj -c Release -o artifacts/vfps

$env:ASPNETCORE_ENVIRONMENT = "Production"
$env:Authorization__IsEnabled = "false"
# applies the database migrations on startup instead of requiring a separate `migrate` run
$env:ForceRunDatabaseMigrations = "true"
$env:MetricsPort = "0"
# "false" for the rows without namespace caching
$env:Pseudonymization__Caching__Namespaces__IsEnabled = "true"
$env:ConnectionStrings__PostgreSQL = "Host=127.0.0.1;Port=35432;Username=postgres;Password=postgres;Database=vfps;Timeout=60;Max Auto Prepare=5;Maximum Pool Size=50"

# appsettings.json is resolved from the working directory
cd artifacts/vfps
dotnet Vfps.dll
```

`MetricsPort=0` disables the Prometheus exporter. It runs on its own `HttpListener` bound to `http://+:8082/metrics/`,
which on Windows requires a URL reservation (`netsh http add urlacl url=http://+:8082/metrics/ user=<user>` from an
elevated shell). Unlike a default deployment, these runs therefore did not export any metrics.

## Resource efficiency

| CPU limit                              | Namespace caching | Requests/sec | P50            | P99              |
| -------------------------------------- | ----------------- | ------------ | -------------- | ---------------- |
| WSL2 + Docker, `cpus: "1"`             | off               | 1,184        | 29.68 ms       | 112.61 ms        |
| Native, pinned to 1 logical CPU        | off               | 2,808–2,825  | 15.92–16.04 ms | 30.25–31.09 ms   |
| Native, pinned to 1 logical CPU        | on                | 4,564–4,755  | 9.63–9.82 ms   | 18.04–22.32 ms   |
| Native, CPU rate hard cap              | off               | 1,780–2,159  | 8.10–8.99 ms   | 427.41–438.18 ms |

vfps runs inside a [Job Object](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects), the Windows
equivalent of a cgroup, with a 128 MiB job memory limit and either an affinity to a single logical CPU (like
`docker run --cpuset-cpus`) or a hard CPU rate cap of 4.16% of the machine, i.e. 0.998 of its 24 logical CPUs (like
`cpus: "1"`). The process has to be created suspended and assigned to the job before it is resumed, so the .NET runtime
sees the limits during startup. It then behaves as in a container: `Environment.ProcessorCount` is 1, Server GC falls
back to workstation GC, and the GC heap hard limit is 96 MiB (75% of 128 MiB). A cap of 4.17%, just over one CPU, would
already make .NET round up to 2 processors. Peak committed memory was 98–99 MiB in every run, and all requests succeeded. The environment is the same
as for the unconstrained native runs, plus `DOTNET_EnableDiagnostics=0` to mirror [compose.yaml](compose.yaml).

The results are ranges over two consecutive runs once the throughput had stabilized. On a single CPU, the background
JIT competes with request processing, so the first 100,000 to 200,000 requests after startup are noticeably slower
(2,118 req/s and a P99 of 61.83 ms when pinned without namespace caching).

Pinning to a single CPU gives the more useful numbers on Windows. Windows enforces the CPU rate cap over windows of about
600 ms: vfps processes requests on several cores for about 150 ms, exhausts its budget, and is then suspended entirely
for about 450 ms, which causes the P99 latency of over 400 ms. Linux enforces the CFS quota behind `cpus: "1"` over 100 ms
periods instead.

Two more differences from the WSL2 run: the job memory limit only counts committed private memory, while the cgroup
limit behind `memory: 128m` also counts the page cache, and the hyperthread sibling of the logical CPU vfps was pinned to
remained available to `ghz` and PostgreSQL.

## Cold start

How long a new vfps container takes from `docker run` to answering its first pseudonymization request, measured with
[`src/Vfps.Benchmarks/cold-start.sh`](https://github.com/miracum/vfps/blob/master/src/Vfps.Benchmarks/cold-start.sh)
against the PostgreSQL from [compose.yaml](https://github.com/miracum/vfps/blob/master/compose.yaml):

```sh
docker compose up -d --wait
src/Vfps.Benchmarks/cold-start.sh 10
# with the resource limits of the vfps service in compose.yaml
src/Vfps.Benchmarks/cold-start.sh 10 --cpus=1 --memory=128m
```

The script needs `grpcurl`, GNU `date`, a Linux Docker Engine for host networking, and free ports 8080 to 8082. It
first applies the database migrations with `Vfps.dll migrate`, as the Helm chart's migrations Job does, so the timed
starts don't include them. Each run then starts the container with the environment of the vfps service in
[compose.yaml](https://github.com/miracum/vfps/blob/master/compose.yaml), calls `PseudonymService/Create` with a new
original value in a loop until a call succeeds, sends one more call, and stops the container gracefully.

| Column           | Time from                               | until                                          |
| ---------------- | --------------------------------------- | ---------------------------------------------- |
| `total`          | `docker run`                            | the first successful response                  |
| `listening`      | the container start                     | Kestrel logs that it listens on gRPC port 8081 |
| `first_response` | the container start                     | the first successful response                  |
| `first_request`  | sending the first successful request    | its response                                   |
| `warm_request`   | sending a second request right after it | its response                                   |

All durations are in milliseconds and include about 20 ms for starting `grpcurl` itself, which is also about how long
each polling attempt takes. The `warm_request` column is therefore mostly that overhead.

Sample output running on

```console
OS=Windows 11 (10.0.26300.9457/26H2), WSL2 (Linux 6.18.40.1-microsoft-standard-WSL2) with 8 logical CPUs and 20 GiB of RAM
Intel Core Ultra 7 258V
Docker Engine 29.8.2 inside WSL2
vfps v1.22.2 container image, PostgreSQL 18.4 from compose.yaml
grpcurl v1.9.3
```

| Container limits         | `total` | `listening` | `first_response` | `first_request` | `warm_request` |
| ------------------------ | ------- | ----------- | ---------------- | --------------- | -------------- |
| none                     | 1,794   | 1,501       | 1,755            | 259             | 24             |
| `--cpus=1 --memory=128m` | 2,350   | 1,709       | 2,308            | 606             | 24             |

The values are medians over 10 runs. Apart from the slowest run of each series, with a `total` of 2,395 ms and 3,952 ms,
the `total` ranged from 1,723 to 1,826 ms without limits and from 2,229 to 2,500 ms with them.

Most of the start happens before Kestrel listens: about 1.5 s, during which vfps can't answer anything, including its
health checks. Limiting vfps to one CPU only adds about 200 ms to that. The first request then takes 259 ms, against
24 ms for the one after it. It is the first to run vfps' own code paths and those of EF Core, Npgsql and gRPC, none of
which are precompiled, so the .NET JIT compiles them during that request. This part does depend on the CPU: on one CPU,
the first request takes 606 ms. A replica that has just passed its readiness check therefore answers its first request
noticeably slower than the ones after it.
