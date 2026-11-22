# Observability

The service exports metrics via OpenTelemetry, exposed in Prometheus text format on `:8082/metrics`.
Traces can optionally be pushed to an OTLP collector, see `Tracing:IsEnabled` and `Tracing:Otlp:Endpoint` in `appsettings.json`.
Health-, readiness-, and liveness-probes are exposed at `:8080/healthz`, `:8080/readyz`, and `:8080/livez` respectively.

## Grafana dashboard

[`deploy/grafana/vfps-dashboard.json`](https://github.com/miracum/vfps/blob/master/deploy/grafana/vfps-dashboard.json)
is a ready-made dashboard for Grafana 11 or later, built on these metrics scraped into Prometheus. Import it via
**Dashboards → New → Import**, then pick your Prometheus data source in the dashboard's _Data source_ dropdown.
It covers:

- **Overview** - request rate, 5xx ratio, request and database latency, connection-pool usage, stored pseudonyms,
  the next service-account token expiry, running versions and scrape health
- **HTTP & gRPC** - traffic, status codes and latency per route, a latency heatmap, Kestrel queueing, and
  exceptions by type
- **Database** - connection pool usage and waits, command and upsert latency, connection churn, and network I/O
- **Pseudonyms, cache & access tokens** - pseudonyms and growth per namespace, cache hit ratio, and every
  service-account token's remaining lifetime
- **Batches & outbound HTTP** - batch time per phase, and latency, errors and connections towards object storage and
  the OIDC provider
- **.NET runtime** - CPU, memory, GC, allocation rate, thread pool and lock contention

The _Job_ variable lists every scrape job that exposes vfps' own metrics, so it needs no relabelling and picks up
the worker Deployment's metrics as well when `worker.enabled` is set. Native gRPC calls complete with HTTP 200
whatever their gRPC status, so their failures don't show up in the status-code panels. vfps adds the gRPC status to
the same request metric instead, as the `rpc_grpc_status_code` label (`0` is OK, see the
[gRPC status codes](https://grpc.io/docs/guides/status-codes/)), which the alerts below use.

## Alerts

The Helm chart ships Prometheus alerting rules for the Prometheus Operator: set `prometheusRule.enabled`, along
with `serviceMonitor.enabled` so the metrics are scraped. Every group can be turned off on its own, and the
thresholds are chart values - see the chart's `prometheusRule.*` values. Critical alerts are meant to page someone,
warnings to open a ticket.

| Alert                                 | Severity | Fires when                                                                                             |
| ------------------------------------- | -------- | ------------------------------------------------------------------------------------------------------ |
| `VfpsDown`                            | critical | No API pod can be scraped for 5 minutes.                                                               |
| `VfpsWorkersDown`                     | critical | No worker pod can be scraped for 5 minutes - only with `worker.enabled`.                               |
| `VfpsHighErrorRate`                   | critical | More than 5% of requests end in HTTP 5xx or a server-side gRPC error, for 10 minutes.                  |
| `VfpsDatabaseConnectionTimeouts`      | critical | Requests time out waiting for a pooled database connection.                                            |
| `VfpsBatchesNotProcessed`             | critical | Batches have been queued for 30 minutes without any running - usually no instance processes batches.   |
| `VfpsServiceAccountTokenExpired`      | critical | A service-account token has expired.                                                                   |
| `VfpsHighLatency`                     | warning  | API p95 latency is above 1 s for 15 minutes.                                                           |
| `VfpsDatabasePoolNearlyExhausted`     | warning  | A connection pool is more than 90% used for 10 minutes.                                                |
| `VfpsSlowDatabase`                    | warning  | Database command p95 latency is above 500 ms for 15 minutes.                                           |
| `VfpsBatchWaitingTooLong`             | warning  | A batch has been queued for more than an hour - workers run, but can't keep up.                        |
| `VfpsBatchesFailing`                  | warning  | A batch failed on an error in the last hour. A batch set to fail on unknown values doesn't count.      |
| `VfpsBatchStalled`                    | warning  | A batch stalled in the last hour - its worker most likely crashed.                                     |
| `VfpsDependencyErrors`                | warning  | More than 10% of calls to one host - object storage, the identity provider, a VOPRF key server - fail. |
| `VfpsThreadPoolStarvation`            | warning  | Work queues up for the .NET thread pool for 5 minutes.                                                 |
| `VfpsServiceAccountTokenExpiringSoon` | warning  | A service-account token expires within 14 days.                                                        |

Pod restarts, crash loops and memory limits aren't covered here, since kube-prometheus-stack's default rules
already alert on them for every workload.

The batch alerts rest on three metrics vfps exports while batches are enabled:

- **`vfps_batches{state}`** - batches currently stored, by state (`awaiting_upload`, `queued`, `running`,
  `completed`, `failed`, `cancelled`, `stalled`). Every replica reports the same database counts, so aggregate it
  with `max()`, not `sum()`.
- **`vfps_batches_oldest_queued_age_seconds`** - how long the longest-queued batch has been waiting; 0 while none is.
- **`vfps_batches_finished_total{outcome, reason}`** - batches reaching a final state, counted by the replica that
  wrote it. A failed one carries a `reason`: `unknown_value` for a batch set to fail on a value its namespace doesn't
  know, `error` for everything else.

## Diagnosing a slow batch

A batch runs as a Hangfire background job rather than inside a request, so the AspNetCore
instrumentation says nothing about it. Two things cover it instead:

- **One span per batch**, `CsvPseudonymizationJob`, tagged with the batch's id, direction and row
  count. Every database command span the batch produces nests under it, so a batch's whole timeline is
  one trace. Enable `Tracing:IsEnabled` and point `Tracing:Otlp:Endpoint` at a collector - see the
  `jaeger` compose profile for a local one.
- **`vfps_csv_job_phase_duration_seconds_total`**, a counter of seconds broken down by `phase`
  (`fetch_input`, `parse_input`, `resolve_database`, `write_output`, `report_progress`) and
  `direction`. The same numbers are also set as tags on each batch's span, so a single trace carries
  its own breakdown without the metrics backend alongside it.

The phase split is what separates a batch bound by object storage from one bound by the database,
which is otherwise indistinguishable from outside - the output is written into a pipe that is
uploaded concurrently, so a slow upload shows up as time spent writing rather than as any error:

```promql
sum by (phase) (rate(vfps_csv_job_phase_duration_seconds_total[5m]))
```

`report_progress` is worth watching on its own. It is bookkeeping rather than the batch's actual
work, and its cost scales with database round-trip latency and the check-in cadence rather than
with rows, so a deployment where it grows into a visible share of the total is one whose cadence
(or database proximity) wants revisiting.

Database round trips themselves need no vfps-specific metric - the Npgsql instrumentation already
exports them, including `db_client_operation_duration_seconds` per command and, for the
connection-pool saturation that `CsvProcessing__WorkerCount` exists to bound:

```promql
db_client_connection_count{db_client_connection_state="used"} / db_client_connection_max
```

Npgsql additionally reports `db_client_connection_npgsql_pending_requests` and
`db_client_connection_npgsql_timeouts`, which appear in the scrape only once requests actually
start queueing for a connection - their absence on an idle instance is expected.
