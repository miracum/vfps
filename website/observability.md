# Observability

The service exports metrics via OpenTelemetry, exposed in Prometheus text format on `:8082/metrics`.
Traces can optionally be pushed to an OTLP collector, see `Tracing:IsEnabled` and `Tracing:Otlp:Endpoint` in `appsettings.json`.
Health-, readiness-, and liveness-probes are exposed at `:8080/healthz`, `:8080/readyz`, and `:8080/livez` respectively.

## Diagnosing a slow CSV job

A CSV job runs as a Hangfire background job rather than inside a request, so the AspNetCore
instrumentation says nothing about it. Two things cover it instead:

- **One span per job**, `CsvPseudonymizationJob`, tagged with the job's id, direction and row
  count. Every database command span the job produces nests under it, so a job's whole timeline is
  one trace. Enable `Tracing:IsEnabled` and point `Tracing:Otlp:Endpoint` at a collector - see the
  `jaeger` compose profile for a local one.
- **`vfps_csv_job_phase_duration_seconds_total`**, a counter of seconds broken down by `phase`
  (`read_input`, `resolve_database`, `write_output`, `report_progress`) and `direction`. The same
  four numbers are also set as tags on each job's span, so a single trace carries its own
  breakdown without the metrics backend alongside it.

The phase split is what separates a job bound by object storage from one bound by the database,
which is otherwise indistinguishable from outside - the output is written into a pipe that is
uploaded concurrently, so a slow upload shows up as time spent writing rather than as any error:

```promql
sum by (phase) (rate(vfps_csv_job_phase_duration_seconds_total[5m]))
```

`report_progress` is worth watching on its own. It is bookkeeping rather than the job's actual
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
