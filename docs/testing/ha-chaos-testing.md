# HA chaos testing in CI

`.github/workflows/ha-chaos.yaml` stands vfps up the way the
[high-availability notes](../deployment/high-availability.md) say it should be run - three replicas
spread across three zones, backed by a replicated CloudNativePG cluster - and then breaks it on
purpose while asserting that it keeps its promises.

It replaces the old `nightly-chaos.yaml`, which installed the *published* chart with
`replicaCount: 3` and the bundled single-instance PostgreSQL, killed one vfps pod a minute, and
asserted the failure rate stayed under 0.1%. That covered exactly one failure mode - an API pod
dying - against the one component whose failure was guaranteed to be a total outage anyway. This
closes the `# TODO: could add database/cnpg chaos if deployed HA` that sat in the old
`tests/chaos/chaos.yaml`, and section 7 of the HA notes.

## 1. What this is actually testing

**Availability is not the interesting property.**

A pseudonymization service has a stronger obligation than staying up. It hands a caller a pseudonym
and implicitly promises that the pseudonym is stable and resolvable forever. `Create` is idempotent -
`PseudonymAppService.CreateTrustedAsync` returns the existing set when one exists rather than minting
a new value - and that idempotency is the contract callers build their retries on.

So a failover that loses committed transactions does not show up as errors. It shows up as a
pseudonym the caller already holds that no longer resolves, and a later `Create` for the same
original value quietly returning a *different* pseudonym - one research subject silently split into
two. Every pod is `Running`, every probe is green, the error rate is zero, and the data is wrong.

The test therefore gates on three properties:

| | Property | How it is measured | Budget |
| --- | --- | --- | --- |
| **P1** | **Availability.** Calls succeed within a bounded client retry budget. | Failure count over the load window. | `RESILIENCE_ERROR_BUDGET`, which **must be calibrated** - see §7. |
| **P2** | **Pseudonym stability.** Every `(original → pseudonym)` pair observed before chaos re-`Create`s to the byte-identical pseudonym afterwards. | Sampled ledger, replayed in a verification pass. | **Zero.** |
| **P3** | **Reverse lookup survives.** `Get(namespace, pseudonym_value)` for that same sample still returns its original value. | Same verification pass. | **Zero.** |

P2 and P3 are the reason this exists. P1 alone was already covered, weakly, by the old nightly.

### Two measurement traps

Both are places the previous NBomber-based stress test got it wrong for this purpose, which is
worth recording now that it is gone.

**The retry policy is deliberately narrow.** The old test retried `StatusCode.Internal` alongside
`Unavailable`. `Internal` is how a genuine server-side failure surfaces, so retrying it hides
precisely what is under test. `ResilienceTests` retries `Unavailable` only.

**The load profile is an open model.** The old test used `KeepConstant(copies: 100)`, a closed
model: every virtual user waits for its own response before issuing the next request. When the
database fails over and latency spikes, a closed model simply issues fewer requests, so the outage
shows up as a throughput dip rather than as errors, systematically understating impact.
`OpenModelLoadRunner` dispatches on a wall-clock schedule and never waits, so offered load stays flat
and an outage lands where it belongs - in the failure count. For the same reason, load shedding past
`MaxInFlight` is **counted as a failure** rather than quietly dropped: silently issuing less work
under stress is the closed-model blind spot by another name.

## 2. Cluster topology

kind, one control-plane and three workers, each labelled with a synthetic zone
(`tests/chaos/ha/kind-config.yaml`). A single-node cluster makes most of this untestable:
`topologySpreadConstraints` becomes a no-op, CNPG's three instances all land on the same kubelet, and
there is nothing to drain.

The load generator is pinned to the **control-plane** node, which the drain scenario never targets.
Its ledger of issued pseudonyms lives in that one pod's memory for the whole run, so an eviction
would not merely interrupt the load - it would destroy the only record of what the service promised,
taking P2 and P3 with it.

## 3. Database: CloudNativePG

`tests/chaos/ha/cnpg-cluster.yaml`: three instances, `primaryUpdateStrategy: unsupervised`, required
pod anti-affinity by hostname, and `max_connections: 200` budgeted against the chart's
`Maximum Pool Size=25` per replica.

### Synchronous replication is not optional here

```yaml
postgresql:
  synchronous:
    method: any
    number: 1
    dataDurability: required
```

With CNPG's default asynchronous streaming replication, a promoted standby may be behind the
primary's last acknowledged commit. A pseudonym vfps already returned to a caller can therefore be
gone after failover - which is exactly the P2/P3 violation above. Left async, the test would be both
flaky *and* right to fail: a real defect indistinguishable from noise.

Requiring one standby to acknowledge every commit means promotion cannot lose an acknowledged write.
That is what makes P2 and P3 legitimately zero-tolerance.

**Hard consequence:** never take down more than one database instance at a time. With three instances
and `number: 1`, losing the primary leaves two and writes continue; losing a primary *and* a standby
leaves one, quorum is unreachable, and writes block indefinitely. That is correct PostgreSQL
behaviour, not a vfps bug. `run.sh` therefore sequences primary kills by cluster health rather than
by a timer, and never runs two database scenarios concurrently.

### Chart wiring

`postgres.enabled: false` plus CNPG's generated `vfps-db-app` secret. **No chart change was needed** -
verified with `helm template`:

```
ConnectionStrings__PostgreSQL: "Host=vfps-db-rw:5432;Database=vfps;…;Maximum Pool Size=25;"
PGPASSWORD: secretKeyRef{ name: "vfps-db-app", key: "password" }
```

Two details that make it work:

- `database.username` must match `bootstrap.initdb.owner`. The chart reads the username from values
  rather than from the secret, so a mismatch fails at connect time, not at render time.
- Point at the **`-rw`** Service. CNPG repoints it at the new primary on promotion; `-ro` and `-r`
  would silently send writes to a standby.

Migrations need no superuser - there is no `CREATE EXTENSION` anywhere in `src/Vfps/Migrations/` - so
CNPG's non-superuser app owner is sufficient.

## 4. Install to steady state before any chaos

Non-negotiable, and the most likely source of a confusing red build. The migrations Job plus the
`wait-for-migrations-job` init container mean a database disruption *during* install fails
`helm upgrade --wait` outright, and `migrationsJob.backoffLimit: 3` gives only so much cover.
`run.sh` waits for three ready CNPG instances and a completed rollout before applying the first
chaos object.

## 5. Scenarios

Run sequentially under continuous load. Durations are `DURATION_*` variables in `run.sh`; the load
window is computed as their sum plus `LOAD_MARGIN_SECONDS`, so the two halves can never disagree
about how long the run is.

| # | Scenario | Mechanism | What it proves |
| --- | --- | --- | --- |
| 0 | `baseline` | nothing injected | The ambient error floor. Without it you cannot tell a chaos-induced failure from a flaky runner - and it is how the error budget gets calibrated. |
| 1 | `vfps-pod-kill` | `PodChaos/pod-kill`, `mode: one`, Schedule @45s | Replicas, PDB, Service endpoint churn. |
| 2 | `cnpg-primary-kill` | one-shot `PodChaos`, selector `cnpg.io/instanceRole: primary`, repeated as health allows | **The headline.** A real failover: promotion, `-rw` repointing, `EnableRetryOnFailure` riding it out. P2 and P3 are decided here. |
| 3 | `db-network-partition` | `NetworkChaos/partition`, 20s bursts | A distinct failure mode from 2: the database is *up* but unreachable, so Npgsql sees timeouts rather than resets. Tests `Timeout=60` and the retry policy, not failover. |
| 4 | `rollout` | `kubectl rollout restart` | HA notes §2 - `preStop`, `terminationGracePeriodSeconds`, `minReadySeconds`. |
| 5 | `drain` | `kubectl drain --timeout=120s` | HA notes §1's PDB deadlock. **A drain that times out is the failure.** `run.sh` uncordons either way so the cluster is healthy before verification. |

## 6. Load generator

`src/Vfps.StressTests/ResilienceTests.cs`. It is marked `[Fact(Explicit = true)]`, so a plain
`dotnet test` - at the solution level or on this project - **skips it**: it needs a live kind cluster
with Chaos Mesh and CloudNativePG behind it and takes the better part of twenty minutes, so it should
only ever run on purpose. The Job opts in with `-explicit only`, alongside
`-trait Category=Resilience`.

A skipped explicit test is reported as *skipped* rather than *not discovered*, so the assembly still
exits 0 and a solution-wide `dotnet test` does not fail on it. That also means forgetting
`-explicit only` would give a green Job that ran nothing - which is precisely what the timeline guard
in §6 catches, since a skipped test emits no CSV.

It runs as a Job in its own namespace (`vfps-loadgen`) and reaches the service at
`dns:///vfps-headless.vfps.svc.cluster.local:8081` - the only way to exercise the client-side
round-robin load balancing the HA notes §4 recommend. A ClusterIP Service balances connections rather
than requests, so one long-lived HTTP/2 connection would pin the whole run to a single replica and
make scenario 1 far easier to pass than it should be.

Phases, all driven by `RESILIENCE_*` environment variables:

1. **Init** - create the namespace, tolerating `AlreadyExists`.
2. **Load** - a fixed offered rate for the full window. Each iteration `Create`s a fresh GUID;
   successful pairs go into a bounded reservoir (`PseudonymLedger`, Algorithm R, 2,000 entries).
3. **Settle** - a quiet period so a just-promoted primary has caught up. P2/P3 are zero-tolerance, so
   they must never be able to fire on a cluster that is merely still converging.
4. **Verify** - replay the reservoir: re-`Create` each original value and compare the pseudonym (P2);
   `Get` each pseudonym and compare the original value (P3). `NotFound` on the `Get` is the clearest
   possible signal of a lost write.
5. **Assert** - P2/P3 exactly, P1 against the budget, plus guards that the run served real traffic
   and the ledger is non-empty, so an empty run cannot pass vacuously.

### No load-testing framework

NBomber became proprietary with License Agreement 3.0 (2025-09-01): *"NBomber is not free for
organizational use. Any use by, for, or on behalf of an organization ... requires a valid Commercial
Subscription."* Only v4 and earlier are Apache-2.0. It has been **removed from the repository
entirely**, along with the `RunStressSimulation` scenario that used it.

Rather than swap one third-party dependency for another, the resilience test uses none. This is not
really a load test: P2/P3 need a ledger of issued pseudonyms recorded during traffic and replayed
afterwards, which no load framework provides, so that part is bespoke either way. What is left -
"issue N requests per second for M minutes and count failures" - is a `PeriodicTimer` and a
semaphore. The parts of NBomber that would have earned their keep (ramping profiles, distributed
agents, HTML reports) are exactly the parts this test does not use, and writing it directly means
reusing `Vfps.Protos`, the existing `GrpcChannel` setup, xunit and AwesomeAssertions.

`Vfps.StressTests` now contains only the resilience test and takes no third-party dependency beyond
xunit, AwesomeAssertions and the gRPC client it already shared with the application. The project name
is now a slight misnomer; renaming it would churn the Dockerfile, the published
`ghcr.io/miracum/vfps/stress-test` image path and the Job manifest, so it has been left alone.

### What is machine-gated vs. human-diagnosed

Per-scenario error attribution needs the load generator and the chaos driver to agree on a clock. The
split instead:

- **Gated:** the global P1 budget, and P2/P3 exactly.
- **Diagnosed from artifacts:** `load-timeline.csv` (per-second ok/failed/shed/latency, lifted out of
  the Job's pod logs) correlated against `chaos-timeline.csv` (written by `run.sh` as it applies and
  removes each scenario).

Per-scenario budgets are a reasonable follow-up once the baseline is understood.

## 7. Running it

```bash
# everything, on a throwaway kind cluster
./tests/chaos/ha/run.sh all

# or in stages
./tests/chaos/ha/run.sh up
./tests/chaos/ha/run.sh scenarios
./tests/chaos/ha/run.sh collect
./tests/chaos/ha/run.sh down
```

Requires `kind`, `kubectl`, `helm`, `envsubst` and `docker`, and the two images built locally:

```bash
docker buildx build --load -t ghcr.io/miracum/vfps:ci .
docker buildx build --load --target=stress-test -t ghcr.io/miracum/vfps/stress-test:ci .
```

A plain `dotnet test Vfps.slnx` is unaffected by any of this - the resilience test is explicit and
gets skipped.

In CI it is `workflow_dispatch` plus a weekly schedule. The dispatch form takes `image-tag` (test a
released image rather than building from the checkout), `scenarios`, `rate-per-second` and
`error-budget`.

### On a pull request

Not on every PR - it takes the best part of an hour, and a chaos test that gates merges is a chaos
test that gets disabled. **Apply the `ha-chaos` label** to opt a PR in; it then runs on every push
until the label is removed, and `cancel-in-progress` supersedes a run when you push again.

A labelled PR gets a trimmed set - `baseline,vfps-pod-kill,cnpg-primary-kill` with shorter durations,
about 6 minutes of load against the weekly run's 17. That keeps the baseline it needs to mean
anything and the CloudNativePG failover that decides P2/P3, and leaves the rollout, partition and
drain scenarios - the slowest, and the least likely to regress from an application change - to the
weekly run. A `scenarios` input always wins over this.

Requiring a label also means a forked PR cannot spend an hour of runner time unless a maintainer asks
for it.

### Calibrate the error budget before trusting P1

`RESILIENCE_ERROR_BUDGET` defaults to `0.005`. **That is a placeholder, not a measurement.** Run:

```
workflow_dispatch → scenarios: baseline
```

a handful of times, read the ambient failure rate off `load-timeline.csv`, and set the budget with
headroom above it. A budget picked before seeing a baseline is the usual reason chaos jobs end up
permanently marked `continue-on-error`.

## 8. Artifacts

Always uploaded, whatever the outcome: `resilience.log`, `load-timeline.csv`, `chaos-timeline.csv`,
`pods.txt`, `events.txt`, `cnpg-cluster.yaml` (the live status, including replication state),
`cnpg-instances.log`, `vfps-api.log`, and a full `cluster-dump/`. If the `cnpg` kubectl plugin
happens to be installed, `cnpg-report.zip` too - its bundle is the first thing to open after a P2 or
P3 violation.

A chaos failure you cannot reconstruct after the cluster is gone is a chaos failure you will end up
ignoring.

### Why the timeline comes out through stdout

`kubectl cp` is the obvious alternative and does not work here. It shells out to `tar` via
`kubectl exec`, which needs a **running** container - and the Job's pod terminates at exactly the
moment the timeline becomes complete, because finishing the test is what ends the container. There is
also no file to copy: the test writes the CSV to stdout between markers and never touches the
filesystem.

Container stdout is persisted by the kubelet and stays readable through `kubectl logs` long after the
container has exited, which is the property that matters. Getting a real file out instead would mean
a PVC plus a second pod to mount it after the Job ends - a lot of moving parts for ~40 KB that is
already being captured in `resilience.log` regardless.

The test emits the timeline **before** it asserts, so the CSV is present even for a run that failed
P1, P2 or P3 - which is the run you actually want it for.

## 9. Risks

1. **Runner capacity.** A 4-node kind cluster, the CNPG operator, three PostgreSQL instances, Chaos
   Mesh's controller and daemons, three vfps replicas and a load generator on a 4-vCPU / 16 GB runner
   is the main feasibility question. The Chaos Mesh dashboard is disabled for this reason. If it is
   tight, `RESILIENCE_RATE_PER_SECOND` is the first dial to turn down - P2/P3 need traffic, not
   throughput.
2. **CNPG API drift.** The `postgresql.synchronous` stanza replaced `minSyncReplicas`/
   `maxSyncReplicas`, and `cnpg.io/instanceRole` replaced the deprecated `role` label in CNPG 1.24.
   `run.sh` fails loudly rather than silently if it cannot find a pod labelled as the primary, and
   polls `.status.readyInstances` rather than waiting on a condition name that has changed across
   versions.
3. **`NetworkChaos` on kind** needs the chaos daemon to manipulate pod network namespaces. It works,
   but scenario 3 is the one to prove out first.
4. **Nothing here has been run against a live cluster yet.** The chart wiring, both trait filters,
   the Job manifest after `envsubst`, and all YAML and shell syntax are verified; the cluster
   behaviour is not.

## 10. Deliberately out of scope

- **CSV job resilience.** Resumable CSV jobs are implemented but deliberately unmerged (HA notes,
  "Deferred"), so on master a job killed mid-processing restarts from row 0 *by design*. A test
  asserting otherwise would fail correctly and tell us nothing. Revisit when `resumable-csv` lands.
- **S3 / object storage**, which CSV jobs need and nothing else does.
- **NetworkPolicy.** The chart can render one; enabling it here would require allowing the
  cross-namespace loadgen traffic. A follow-up scenario of its own.
- **The admin UI.** Blazor session affinity under failover (HA notes §3) is a real gap, but it is a
  Playwright problem, not a load-generator one.

## 11. Files

```
.github/workflows/ha-chaos.yaml
tests/chaos/ha/run.sh                          # the driver
tests/chaos/ha/kind-config.yaml
tests/chaos/ha/cnpg-cluster.yaml
tests/chaos/ha/vfps-ha-values.yaml
tests/chaos/ha/loadgen-job.yaml
tests/chaos/ha/chaos/vfps-pod-kill.yaml
tests/chaos/ha/chaos/cnpg-primary-kill.yaml
tests/chaos/ha/chaos/db-network-partition.yaml
src/Vfps.StressTests/ResilienceTests.cs
src/Vfps.StressTests/Resilience/ResilienceOptions.cs
src/Vfps.StressTests/Resilience/PseudonymLedger.cs
src/Vfps.StressTests/Resilience/OpenModelLoadRunner.cs
```

Removed with NBomber: `src/Vfps.StressTests/StressTests.cs`, the `NBomber` package reference, its two
global usings, and 296 lines of transitive dependencies from `packages.lock.json`.

Removed with the old nightly: `Taskfile.yaml`, `.github/workflows/nightly-chaos.yaml`, and the Argo
Workflows assets under `tests/chaos/` (`workflow.yaml`, `argo-workflows-values.yaml`,
`chaos-mesh-rbac.yaml`, `chaos.yaml`, `vfps-values.yaml`). Argo installed a controller, a CLI and a
cluster-wide RBAC bundle to do what amounts to "apply a CR, run a container, delete the CR"; its
`onExit` cleanup bought nothing in CI, where the cluster is discarded seconds later. Driving chaos
from outside the cluster with the runner's own kubeconfig also removed the RBAC entirely, and let the
stress-test image drop its bundled `kubectl`.
