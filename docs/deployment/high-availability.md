# High availability, fault resistance and zero-downtime upgrades

Working notes on what's still missing for a vfps deployment that survives node failures, database
blips and its own rolling upgrades without dropping requests.

The application-side items are **done**. Most of the chart-side work is done too, on the
`feat/vfps-ha` branch of [miracum/charts](https://github.com/miracum/charts/tree/master/charts/vfps)
(chart **3.3.0**, not yet merged) - what that covers is marked below, and what is still open is
listed under "Still to do".

Operator-facing guidance that came out of this lives in the README under
[Running more than one replica](../../README.md#running-more-than-one-replica).

## Done application-side

| Change | Why |
| --- | --- |
| `ShutdownTimeout` (default 25s) now bounds host shutdown | Nothing set it before, so the host used its 30s default - identical to Kubernetes' default `terminationGracePeriodSeconds`, leaving zero margin before `SIGKILL`. |
| `DataProtectionKeyContext` uses the same `EnableRetryOnFailure` policy as `PseudonymContext` | It had none, so a PostgreSQL blip the API rode out cleanly still threw on every auth-cookie and antiforgery operation, taking the admin UI down on its own. |
| Hangfire `WorkerCount` pinned to 4 (`CsvProcessing__WorkerCount`) | Hangfire's default of `min(cores * 5, 20)` ignores the shared Npgsql pool: at 20 workers, de-pseudonymization's 20-way concurrency per job is up to 400 concurrent connection requests from one replica against a default pool maximum of 100. |
| Hangfire dead-server detection shortened to 2min (`CsvProcessing__OrphanedJobRecoveryDelay`), heartbeat and check interval derived from it | Hangfire's 5min default races with `CsvProcessing__StalledJobThreshold` (10min); whenever the stall watchdog won, a job that was about to be re-dispatched got flagged `Stalled` instead. |
| Hangfire `ShutdownTimeout` made explicit (`CsvProcessing__JobServerShutdownTimeout`) | So it can be kept ordered below the host's `ShutdownTimeout`, which in turn sits below the pod's grace period. |
| CSV jobs run in a dedicated worker Deployment when the chart's `worker.enabled` is set (`CsvProcessing__ProcessJobs`) | Hangfire's processing loop is now registered independently of its client and dashboard, so API pods can accept and display jobs without running any. One Deployment can only have one shutdown window and one resource budget; splitting lets jobs drain for minutes while API pods keep rolling in seconds. Defaults to enabled everywhere, so a single-Deployment install is unchanged. |
| `vfps_pseudonyms` is now computed once and shared through a `metric_snapshots` row (new migration), and the instrument changed to an `ObservableGauge` | The `GROUP BY` count over the whole pseudonyms table ran on *every* replica every 5 minutes, so scaling out multiplied this metric's database cost by the replica count. One replica now wins an atomic claim (a single conditional `UPDATE` - no lock, lease or leader election) and stores the result; the rest read it. Every replica reports identical values, no stale series are stranded on a replica that computed it once, and a freshly started replica exports a real value immediately instead of nothing until its first recompute. |

## Deferred

**Resumable CSV jobs.** Implemented on the `resumable-csv` branch (output written as a multipart
upload left open across an interruption, with a checkpoint recording the stored parts and the input
row they end on) but deliberately not merged for now, so on `master` a job killed mid-processing
still restarts from row 0. Dedicated workers reduce how often that happens - API rollouts no longer
touch running jobs - but do not remove it: upgrading the workers themselves still interrupts them.

## Chart-side

Unless a heading says otherwise, the item is implemented in chart 3.3.0 on the `feat/vfps-ha`
branch.

### 1. Template bugs

- **`topologySpreadConstraints` never renders.** `deployment.yaml` has
  `{{- with .topologySpreadConstraints }}` instead of `{{- with .Values.topologySpreadConstraints }}`,
  so the documented value silently does nothing and pods get no zone/node spreading at all. One-word
  fix, and the highest value-per-character item on this list.
- **The `appsettings` volume is declared unconditionally.** `deployment.yaml` always lists the
  `appsettings` volume, but `appsettings-cm.yaml` only creates that ConfigMap
  `{{- if .Values.appsettings }}` - which is empty by default. kubelet sets up every volume in
  `spec.volumes` regardless of whether a container mounts it, so this should leave pods stuck in
  `ContainerCreating` with `configmap ... not found`. Note `ci/kitchen-sink-test-values.yaml` *does*
  set `appsettings`, so CI may not be covering the default path - **verify before fixing**, then
  either gate the volume on the same condition or mark it `optional: true`.
- **PDB defaults deadlock a drain.** `replicaCount: 1` with `podDisruptionBudget.minAvailable: 1`
  means no voluntary eviction can ever succeed, so a node drain hangs indefinitely. Default to
  `maxUnavailable: 1`, or refuse to render the PDB below 2 replicas.
- **No `checksum/config` annotation.** Editing `.Values.appsettings` updates the ConfigMap without
  restarting anything, so replicas silently diverge until something else triggers a rollout. Add the
  usual `checksum/config: {{ include (print $.Template.BasePath "/appsettings-cm.yaml") . | sha256sum }}`
  pod annotation.

### 2. Rolling upgrades

- **Add a `preStop` hook.** Endpoint removal and `SIGTERM` race, so without one, requests are still
  routed to a pod that has already stopped accepting them. **The runtime image is chiseled and has
  no shell**, so `exec: ["/bin/sh", "-c", "sleep 5"]` will not work - use the native
  `lifecycle.preStop.sleep` handler (beta in 1.30, GA in 1.32) behind a `.Capabilities.KubeVersion`
  guard, since the chart still declares `kubeVersion: ">= 1.19.0"`.
- **Expose `terminationGracePeriodSeconds`** and default it above the app's `ShutdownTimeout` (25s)
  plus the `preStop` sleep - 60s is a reasonable default.
- **Expose `strategy` and `minReadySeconds`.** Neither is configurable today. `maxUnavailable: 0` /
  `maxSurge: 1` with a short `minReadySeconds` is the right default for a service other systems
  depend on synchronously.

### 3. Session affinity for the admin UI

The UI is Blazor Server: a circuit lives in one replica's memory and cannot be resumed elsewhere.
There is no affinity anywhere in the chart today - no `service.sessionAffinity`, no ingress
annotations - so with `replicaCount > 1` the initial request and the circuit's WebSocket can land on
different replicas. Add values for ingress cookie affinity and/or `sessionAffinity: ClientIP`.

(The Data Protection key ring is already shared via PostgreSQL, so auth cookies and antiforgery
tokens are portable across replicas - this is only about the circuit.)

### 4. gRPC load balancing

A ClusterIP Service balances connections, not requests, so a gRPC client's single long-lived HTTP/2
connection pins all its traffic to one replica. `headless-service.yaml` already exists for
client-side load balancing but is undocumented and unmentioned in the chart README. Document the
`dns:///` + `round_robin` usage, and consider shipping a recommended client `retryPolicy` service
config. Kestrel has no max-connection-age setting, so long-lived clients only rebalance when
something forces a reconnect.

### 5. Connection pool defaults

`database.additionalConnectionStringParameters` defaults to `"Timeout=60;Max Auto Prepare=5;"` with
**no `Maximum Pool Size`**, so each replica may open up to Npgsql's default of 100 connections. With
`autoscaling.maxReplicas: 5` plus the Data Protection context and Hangfire's own storage pool,
that's comfortably past a stock PostgreSQL `max_connections` of 100. Both `values-test.yaml` and
`ci/kitchen-sink-test-values.yaml` already set it (25 and 50) - promote that to the default, and
consider bundling or documenting PgBouncer.

### 6. Migrations job - partly done

- `backoffLimit: 1` with `restartPolicy: Never` means one transient failure (a database failover
  mid-migration) fails the Job permanently. New pods then wait forever in the
  `wait-for-migrations-job` init container and `helm upgrade --wait` times out with no automatic
  recovery. Raise `backoffLimit`, and add `activeDeadlineSeconds` and `ttlSecondsAfterFinished`.
- A failed `CREATE INDEX CONCURRENTLY` leaves an `INVALID` index that has to be dropped by hand
  before the migration can succeed. Combined with the above, that's a manual-intervention outage;
  worth calling out in the chart README at minimum.
- **Not done, and worth writing down somewhere: expand/contract is a convention, not a guarantee.** The Job is a plain resource rather
  than a `pre-upgrade` hook, so migrations run while the previous version's pods are still serving -
  which is the right model, but only if every migration is backwards-compatible. Worth stating
  explicitly in `CONTRIBUTING`/the chart README.
- **Not done:** setting `lock_timeout` on the migration connection so a migration can't sit blocking
  the live application indefinitely. Relevant precedent in this repo: `20260826124015_AddSequenceNumberToPseudonyms`
  drops and re-adds the primary key on `pseudonyms`, and `ALTER TABLE ... ADD PRIMARY KEY` builds its
  unique index **non-concurrently under `ACCESS EXCLUSIVE`** - a full read/write outage on the
  largest table for the duration of the build. The index-creating migrations correctly use
  `Npgsql:CreatedConcurrently`; primary key changes have no such escape hatch and need an explicit
  plan.

### 7. Database - documented only

Documented in the chart README's new High availability section, but nothing enforces it:
`postgres.enabled: true` pulls in a single-instance PostgreSQL with resources capped at 192Mi - any
restart is a full outage, softened only by the application's retry budget. Document (or support)
CloudNativePG or an external managed PostgreSQL for deployments that actually need availability.

### 8. Separate the CSV job workers

Done, in chart 3.4.0: `worker.enabled` runs jobs in a second Deployment of the same image and
switches the API pods to accept-only.

This originally needed no Hangfire queues at all - with the API pods running no server whatsoever,
every server was a worker and the default queue sufficed. That stopped being true once the
pseudonym-count metric became a recurring job: every pod now runs a server, so "does this pod
process CSV jobs?" had to become a property of the queues it serves rather than of whether it has a
server. `CsvProcessing__ProcessJobs=false` pods serve only the `metrics` queue; job-processing pods
serve `default` (where CSV jobs land) ahead of it, so a metrics tick never delays a CSV job.

The workers get their own `terminationGracePeriodSeconds` (600 against the API pods' 60), resources,
replicas, PDB and scheduling, which is the whole point: a killed CSV job has to be re-dispatched and
reprocessed, so it is worth waiting for one to finish, but that is not a wait you want on every API
pod rollout.

One trap worth remembering: every other Service in the chart selects
`app.kubernetes.io/component: api`, so the workers needed their own headless metrics Service and a
widened `ServiceMonitor` selector - otherwise enabling them would have silently dropped their
metrics.

### 9. Secrets

Done. `extraEnvFrom` landed in 3.3.0, and 3.5.0 made S3 first-class: `s3.existingSecret` (or a
chart-created Secret from `s3.accessKey`/`s3.secretKey`) replaces putting `S3__SecretKey` in
`extraEnv`, where it sat in the pod spec and the Helm release as plaintext.
`Authorization__ClientSecret` still has no first-class equivalent and needs `extraEnvFrom`.

**Also fixed in 3.5.0, found while doing this:** with an external database and no
`database.existingSecret`, the chart referenced a `<fullname>-db-secret` Secret that no template
ever created - four `secretKeyRef` entries against zero rendered Secrets, so every pod failed with
`CreateContainerConfigError` and `database.password` was unusable. Same class of bug as the
`appsettings` ConfigMap in section 1.

### 10. Smaller gaps - partly done

Done: `autoscaling.behavior` and `priorityClassName` are now values. Still open:

- No `NetworkPolicy` - nothing constrains ingress or egress for a service holding identifying data.
- No `PrometheusRule` - the chart ships a `ServiceMonitor` and the app exports rich metrics, but
  there are no alerts.
- Probes only cover HTTP on 8080. The app maps a gRPC health service
  (`MapGrpcHealthChecksService()`) that nothing uses; a `grpc:` probe on 8081 would catch a wedged
  HTTP/2 listener.
- HPA still scales on CPU only; request rate or in-flight requests would be a better signal for a
  latency-bound gRPC service.
- No default anti-affinity - `topologySpreadConstraints` now works, but has to be set explicitly.

## Deliberately not changed

- **`/readyz` never fails.** It filters on a `ready` health-check tag that nothing is registered
  under, so it only reports "the process is listening". That's the right call for a shared database
  dependency - a database blip would otherwise pull *every* replica out of service simultaneously,
  turning a slow dependency into a total outage - but it does mean a replica broken in a purely
  local way is never removed. Left as is; revisit only with a genuinely replica-local signal.
- **Cross-replica cache invalidation.** `Pseudonymization__Caching__*` is per-process, so a
  namespace deletion isn't visible to other replicas' caches until the entry expires. Fixing it
  properly means a distributed cache or PostgreSQL `LISTEN`/`NOTIFY`; for now the window is bounded
  by `AbsoluteExpiration` and documented in the README, and namespaces are immutable once created so
  deletion is the only affected operation.
