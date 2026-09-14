#!/usr/bin/env bash
#
# Drives the HA chaos test: stands vfps up on a four-node kind cluster against a replicated
# CloudNativePG cluster, runs a load generator in-cluster, and breaks things on a schedule while it
# runs. See docs/testing/ha-chaos-testing.md for what this is asserting and why.
#
# Usage: tests/chaos/ha/run.sh [all|up|scenarios|collect|down]
#
set -Eeuo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../../.." && pwd)"

CLUSTER_NAME="${CLUSTER_NAME:-vfps-ha}"
NAMESPACE="${NAMESPACE:-vfps}"
LOADGEN_NAMESPACE="${LOADGEN_NAMESPACE:-vfps-loadgen}"
CHART_PATH="${CHART_PATH:-${REPO_ROOT}/charts/vfps}"
ARTIFACT_DIR="${ARTIFACT_DIR:-${REPO_ROOT}/ha-chaos-artifacts}"

VFPS_IMAGE_REGISTRY="${VFPS_IMAGE_REGISTRY:-ghcr.io}"
VFPS_IMAGE_REPOSITORY="${VFPS_IMAGE_REPOSITORY:-miracum/vfps}"
VFPS_IMAGE_TAG="${VFPS_IMAGE_TAG:-ci}"
VFPS_IMAGE="${VFPS_IMAGE_REGISTRY}/${VFPS_IMAGE_REPOSITORY}:${VFPS_IMAGE_TAG}"
LOADGEN_IMAGE="${LOADGEN_IMAGE:-ghcr.io/miracum/vfps/stress-test:ci}"

# Scenarios to run, in order. Trim this to shorten a local run - `SCENARIOS=baseline` is how you
# calibrate RESILIENCE_ERROR_BUDGET against an undisturbed cluster.
SCENARIOS="${SCENARIOS:-baseline,vfps-pod-kill,cnpg-primary-kill,db-network-partition,rollout,drain}"

DURATION_BASELINE="${DURATION_BASELINE:-120}"
DURATION_VFPS_POD_KILL="${DURATION_VFPS_POD_KILL:-120}"
DURATION_CNPG_PRIMARY_KILL="${DURATION_CNPG_PRIMARY_KILL:-240}"
DURATION_DB_NETWORK_PARTITION="${DURATION_DB_NETWORK_PARTITION:-120}"
DURATION_ROLLOUT="${DURATION_ROLLOUT:-180}"
DURATION_DRAIN="${DURATION_DRAIN:-180}"
DRAIN_TIMEOUT="${DRAIN_TIMEOUT:-120}"

# Headroom so the chaos schedule always finishes inside the load window rather than bleeding into
# the settle period, where it would corrupt the verification pass it is supposed to precede.
LOAD_MARGIN_SECONDS="${LOAD_MARGIN_SECONDS:-60}"

RESILIENCE_RATE_PER_SECOND="${RESILIENCE_RATE_PER_SECOND:-50}"
RESILIENCE_SETTLE_SECONDS="${RESILIENCE_SETTLE_SECONDS:-60}"
RESILIENCE_MAX_IN_FLIGHT="${RESILIENCE_MAX_IN_FLIGHT:-200}"
# Placeholder, not a calibrated value - see ResilienceOptions.ErrorBudget.
RESILIENCE_ERROR_BUDGET="${RESILIENCE_ERROR_BUDGET:-0.005}"

VFPS_GRPC_ADDRESS="${VFPS_GRPC_ADDRESS:-dns:///vfps-headless.${NAMESPACE}.svc.cluster.local:8081}"

FAILURES=()

log() { printf '\n\033[1m[%s] %s\033[0m\n' "$(date -u +%H:%M:%S)" "$*"; }
fail() {
  printf '\n\033[31m[%s] FAIL: %s\033[0m\n' "$(date -u +%H:%M:%S)" "$*"
  FAILURES+=("$*")
}

# Written alongside the load generator's own per-second timeline so the two can be correlated after
# the fact - which is how a per-scenario story gets told without the two halves sharing a clock.
timeline() {
  mkdir -p "${ARTIFACT_DIR}"
  local file="${ARTIFACT_DIR}/chaos-timeline.csv"
  [[ -f "${file}" ]] || echo "timestamp,scenario,event" >"${file}"
  echo "$(date -u +%Y-%m-%dT%H:%M:%SZ),$1,$2" >>"${file}"
  printf '  \033[36m→ %s: %s\033[0m\n' "$1" "$2"
}

require_tools() {
  local missing=()
  for tool in kind kubectl helm envsubst docker; do
    command -v "${tool}" >/dev/null 2>&1 || missing+=("${tool}")
  done
  if ((${#missing[@]})); then
    echo "missing required tools: ${missing[*]}" >&2
    exit 2
  fi
}

compute_load_seconds() {
  local total=0 scenario
  local -a selected
  IFS=',' read -ra selected <<<"${SCENARIOS}"
  for scenario in "${selected[@]}"; do
    case "${scenario}" in
      baseline) total=$((total + DURATION_BASELINE)) ;;
      vfps-pod-kill) total=$((total + DURATION_VFPS_POD_KILL)) ;;
      cnpg-primary-kill) total=$((total + DURATION_CNPG_PRIMARY_KILL)) ;;
      db-network-partition) total=$((total + DURATION_DB_NETWORK_PARTITION)) ;;
      rollout) total=$((total + DURATION_ROLLOUT)) ;;
      drain) total=$((total + DURATION_DRAIN)) ;;
      *)
        echo "unknown scenario '${scenario}' in SCENARIOS" >&2
        exit 2
        ;;
    esac
  done
  echo $((total + LOAD_MARGIN_SECONDS))
}

create_cluster() {
  if kind get clusters 2>/dev/null | grep -qx "${CLUSTER_NAME}"; then
    log "kind cluster ${CLUSTER_NAME} already exists"
  else
    log "creating kind cluster ${CLUSTER_NAME}"
    kind create cluster --name "${CLUSTER_NAME}" --config "${SCRIPT_DIR}/kind-config.yaml" --wait 180s
  fi
  kubectl cluster-info --context "kind-${CLUSTER_NAME}"
}

load_images() {
  # The load generator is always built from this checkout - it carries the test code itself. The
  # application image is only side-loaded when it was built here too; when a released tag is being
  # tested instead, the chart's IfNotPresent policy lets the kubelet pull it normally.
  local images=("${LOADGEN_IMAGE}")
  if [[ "${LOAD_VFPS_IMAGE:-true}" == "true" ]]; then
    images+=("${VFPS_IMAGE}")
  fi

  log "loading images into the cluster: ${images[*]}"
  kind load docker-image --name "${CLUSTER_NAME}" "${images[@]}"
}

# Polls readyInstances rather than waiting on a condition name, because CloudNativePG has renamed
# its conditions across versions and a wait on a condition that no longer exists silently succeeds.
wait_cnpg_ready() {
  local timeout="${1:-300}"
  local deadline=$((SECONDS + timeout))
  local ready="" instances=""

  while ((SECONDS < deadline)); do
    ready="$(kubectl get cluster vfps-db -n "${NAMESPACE}" -o jsonpath='{.status.readyInstances}' 2>/dev/null || true)"
    instances="$(kubectl get cluster vfps-db -n "${NAMESPACE}" -o jsonpath='{.spec.instances}' 2>/dev/null || true)"
    if [[ -n "${ready}" && "${ready}" == "${instances}" ]]; then
      echo "  CloudNativePG healthy: ${ready}/${instances} instances ready"
      return 0
    fi
    sleep 5
  done

  fail "CloudNativePG did not reach ${instances:-?} ready instances within ${timeout}s (last seen: ${ready:-none})"
  kubectl get cluster vfps-db -n "${NAMESPACE}" -o yaml || true
  return 1
}

install_cnpg() {
  log "installing the CloudNativePG operator"
  helm repo add cnpg https://cloudnative-pg.github.io/charts >/dev/null
  helm repo update cnpg >/dev/null
  helm upgrade --install cnpg cnpg/cloudnative-pg \
    --namespace cnpg-system --create-namespace --wait --timeout 5m

  kubectl create namespace "${NAMESPACE}" --dry-run=client -o yaml | kubectl apply -f -

  log "creating the PostgreSQL cluster"
  kubectl apply -f "${SCRIPT_DIR}/cnpg-cluster.yaml"
  wait_cnpg_ready 600
}

install_chaos_mesh() {
  log "installing Chaos Mesh"
  helm repo add chaos-mesh https://charts.chaos-mesh.org >/dev/null
  helm repo update chaos-mesh >/dev/null
  helm upgrade --install chaos-mesh chaos-mesh/chaos-mesh \
    --namespace chaos-mesh --create-namespace --wait --timeout 5m \
    --set chaosDaemon.runtime=containerd \
    --set chaosDaemon.socketPath=/run/containerd/containerd.sock \
    --set dashboard.create=false
}

install_vfps() {
  log "fetching chart dependencies"
  helm dependency build "${CHART_PATH}"

  log "installing vfps from ${CHART_PATH} (image ${VFPS_IMAGE})"
  helm upgrade --install vfps "${CHART_PATH}" \
    --namespace "${NAMESPACE}" --create-namespace \
    --values "${SCRIPT_DIR}/vfps-ha-values.yaml" \
    --set "image.registry=${VFPS_IMAGE_REGISTRY}" \
    --set "image.repository=${VFPS_IMAGE_REPOSITORY}" \
    --set "image.tag=${VFPS_IMAGE_TAG}" \
    --wait --timeout 10m
}

# Chaos must never start against a cluster that is still converging: the migrations Job plus the
# wait-for-migrations-job init container mean a database disruption during install fails the release
# outright, and that failure looks nothing like the ones this test is for.
wait_steady_state() {
  log "waiting for steady state"
  kubectl rollout status "deployment/vfps" -n "${NAMESPACE}" --timeout=600s
  wait_cnpg_ready 300

  echo "  API pod placement:"
  kubectl get pods -n "${NAMESPACE}" -l app.kubernetes.io/component=api \
    -o custom-columns='NAME:.metadata.name,NODE:.spec.nodeName,ZONE:.metadata.labels.topology\.kubernetes\.io/zone' \
    --no-headers | sed 's/^/    /'
}

start_loadgen() {
  local load_seconds="$1"
  log "starting the load generator (${RESILIENCE_RATE_PER_SECOND}/s for ${load_seconds}s)"

  kubectl create namespace "${LOADGEN_NAMESPACE}" --dry-run=client -o yaml | kubectl apply -f -

  # Enough for the load window, the settle period, and a verification pass over the whole ledger.
  export JOB_DEADLINE_SECONDS=$((load_seconds + RESILIENCE_SETTLE_SECONDS + 900))
  export RESILIENCE_LOAD_SECONDS="${load_seconds}"
  export LOADGEN_IMAGE VFPS_GRPC_ADDRESS RESILIENCE_RATE_PER_SECOND
  export RESILIENCE_SETTLE_SECONDS RESILIENCE_ERROR_BUDGET RESILIENCE_MAX_IN_FLIGHT

  envsubst <"${SCRIPT_DIR}/loadgen-job.yaml" | kubectl apply -f -
  kubectl wait --for=condition=Ready pod \
    -l app.kubernetes.io/name=vfps-resilience -n "${LOADGEN_NAMESPACE}" --timeout=300s
  timeline "loadgen" "started"
}

scenario_baseline() {
  log "scenario: baseline (${DURATION_BASELINE}s, nothing injected)"
  timeline "baseline" "start"
  sleep "${DURATION_BASELINE}"
  timeline "baseline" "end"
}

scenario_vfps_pod_kill() {
  log "scenario: vfps pod kill (${DURATION_VFPS_POD_KILL}s)"
  kubectl apply -f "${SCRIPT_DIR}/chaos/vfps-pod-kill.yaml"
  timeline "vfps-pod-kill" "applied"
  sleep "${DURATION_VFPS_POD_KILL}"
  kubectl delete -f "${SCRIPT_DIR}/chaos/vfps-pod-kill.yaml" --ignore-not-found
  timeline "vfps-pod-kill" "removed"
  kubectl rollout status "deployment/vfps" -n "${NAMESPACE}" --timeout=300s || true
}

# Rounds are sequenced by cluster health rather than by a timer: a second kill before synchronous
# replication has been re-established could leave one instance, no quorum, and blocked writes -
# a self-inflicted stall rather than anything vfps did wrong.
scenario_cnpg_primary_kill() {
  log "scenario: CloudNativePG primary kill (${DURATION_CNPG_PRIMARY_KILL}s budget)"
  local deadline=$((SECONDS + DURATION_CNPG_PRIMARY_KILL))
  local round=0 primary

  while ((SECONDS < deadline)); do
    round=$((round + 1))
    primary="$(kubectl get pods -n "${NAMESPACE}" \
      -l 'cnpg.io/cluster=vfps-db,cnpg.io/instanceRole=primary' \
      -o jsonpath='{.items[0].metadata.name}' 2>/dev/null || true)"

    if [[ -z "${primary}" ]]; then
      fail "could not identify the CloudNativePG primary - check the cnpg.io/instanceRole label against the operator version"
      return 1
    fi

    timeline "cnpg-primary-kill" "round=${round} killing=${primary}"
    kubectl apply -f "${SCRIPT_DIR}/chaos/cnpg-primary-kill.yaml"
    sleep 20
    kubectl delete -f "${SCRIPT_DIR}/chaos/cnpg-primary-kill.yaml" --ignore-not-found

    wait_cnpg_ready 300 || return 1
    timeline "cnpg-primary-kill" "round=${round} recovered"
    sleep 15
  done
}

scenario_db_network_partition() {
  log "scenario: vfps to database network partition (${DURATION_DB_NETWORK_PARTITION}s)"
  kubectl apply -f "${SCRIPT_DIR}/chaos/db-network-partition.yaml"
  timeline "db-network-partition" "applied"
  sleep "${DURATION_DB_NETWORK_PARTITION}"
  kubectl delete -f "${SCRIPT_DIR}/chaos/db-network-partition.yaml" --ignore-not-found
  timeline "db-network-partition" "removed"
}

scenario_rollout() {
  log "scenario: rolling upgrade under load"
  timeline "rollout" "restart issued"
  kubectl rollout restart "deployment/vfps" -n "${NAMESPACE}"
  if ! kubectl rollout status "deployment/vfps" -n "${NAMESPACE}" --timeout="${DURATION_ROLLOUT}s"; then
    fail "rolling upgrade did not complete within ${DURATION_ROLLOUT}s"
  fi
  timeline "rollout" "complete"
}

# A drain that times out *is* the failure: it means the PodDisruptionBudget cannot be satisfied, the
# exact deadlock docs/deployment/high-availability.md calls out. Uncordoning happens either way, so
# the cluster is healthy again before verification.
scenario_drain() {
  log "scenario: node drain (PodDisruptionBudget)"
  local node
  node="$(kubectl get pods -n "${NAMESPACE}" -l app.kubernetes.io/component=api \
    -o jsonpath='{.items[0].spec.nodeName}' 2>/dev/null || true)"

  if [[ -z "${node}" ]]; then
    fail "no vfps API pod found to pick a drain target from"
    return 1
  fi

  timeline "drain" "draining ${node}"
  if ! kubectl drain "${node}" --ignore-daemonsets --delete-emptydir-data --timeout="${DRAIN_TIMEOUT}s"; then
    fail "drain of ${node} did not complete within ${DRAIN_TIMEOUT}s - likely a PodDisruptionBudget deadlock"
    kubectl get pdb --all-namespaces || true
  fi
  timeline "drain" "uncordoning ${node}"
  kubectl uncordon "${node}"

  local remaining=$((DURATION_DRAIN - DRAIN_TIMEOUT))
  if ((remaining > 0)); then
    sleep "${remaining}"
  fi
  kubectl rollout status "deployment/vfps" -n "${NAMESPACE}" --timeout=300s || true
  wait_cnpg_ready 300 || true
}

run_scenarios() {
  local started=${SECONDS} scenario
  local -a selected
  IFS=',' read -ra selected <<<"${SCENARIOS}"

  for scenario in "${selected[@]}"; do
    case "${scenario}" in
      baseline) scenario_baseline ;;
      vfps-pod-kill) scenario_vfps_pod_kill ;;
      cnpg-primary-kill) scenario_cnpg_primary_kill || true ;;
      db-network-partition) scenario_db_network_partition ;;
      rollout) scenario_rollout ;;
      drain) scenario_drain || true ;;
    esac
  done

  log "all scenarios finished after $((SECONDS - started))s"
}

collect() {
  mkdir -p "${ARTIFACT_DIR}"
  log "waiting for the resilience Job to finish"

  local deadline=$((SECONDS + ${COLLECT_TIMEOUT:-1800}))
  local outcome="timeout"

  while ((SECONDS < deadline)); do
    if [[ "$(kubectl get job vfps-resilience -n "${LOADGEN_NAMESPACE}" \
      -o jsonpath='{.status.conditions[?(@.type=="Complete")].status}' 2>/dev/null || true)" == "True" ]]; then
      outcome="complete"
      break
    fi
    if [[ "$(kubectl get job vfps-resilience -n "${LOADGEN_NAMESPACE}" \
      -o jsonpath='{.status.conditions[?(@.type=="Failed")].status}' 2>/dev/null || true)" == "True" ]]; then
      outcome="failed"
      break
    fi
    sleep 10
  done

  log "resilience Job: ${outcome}"
  kubectl logs "job/vfps-resilience" -n "${LOADGEN_NAMESPACE}" --tail=-1 \
    >"${ARTIFACT_DIR}/resilience.log" 2>&1 || true

  # Lifted straight out of the pod logs, so no shared volume is needed to get it off the cluster.
  awk '/----BEGIN TIMELINE CSV----/{flag=1;next}/----END TIMELINE CSV----/{flag=0}flag' \
    "${ARTIFACT_DIR}/resilience.log" >"${ARTIFACT_DIR}/load-timeline.csv" || true

  # Closes a silent-pass hole: a Job that selected no test - a mistyped trait, or a missing
  # `-explicit only` against the [Fact(Explicit = true)] marker - would exit 0 and report a green run
  # without ever having issued a request. The test emits its timeline before it asserts, so the CSV
  # is present even for a run that failed P1/P2/P3; its absence means the load window never finished.
  if [[ "$(wc -l <"${ARTIFACT_DIR}/load-timeline.csv" 2>/dev/null || echo 0)" -lt 2 ]]; then
    fail "no load timeline in the Job output - the resilience test did not run, or died before the load window finished"
  fi

  log "collecting diagnostics"
  kubectl get pods --all-namespaces -o wide >"${ARTIFACT_DIR}/pods.txt" 2>&1 || true
  kubectl get events --all-namespaces --sort-by=.lastTimestamp >"${ARTIFACT_DIR}/events.txt" 2>&1 || true
  kubectl get cluster vfps-db -n "${NAMESPACE}" -o yaml >"${ARTIFACT_DIR}/cnpg-cluster.yaml" 2>&1 || true
  kubectl logs -n "${NAMESPACE}" -l cnpg.io/cluster=vfps-db --all-containers --tail=-1 --prefix \
    >"${ARTIFACT_DIR}/cnpg-instances.log" 2>&1 || true
  kubectl logs -n "${NAMESPACE}" -l app.kubernetes.io/component=api --all-containers --tail=-1 --prefix \
    >"${ARTIFACT_DIR}/vfps-api.log" 2>&1 || true
  kubectl cluster-info dump --all-namespaces --output-directory "${ARTIFACT_DIR}/cluster-dump" >/dev/null 2>&1 || true

  # Optional, and worth having when present: the plugin's bundle includes replication state at the
  # time of failure, which is the first thing to look at after a P2 or P3 violation.
  if kubectl cnpg version >/dev/null 2>&1; then
    kubectl cnpg report cluster vfps-db -n "${NAMESPACE}" -f "${ARTIFACT_DIR}/cnpg-report.zip" >/dev/null 2>&1 || true
  fi

  tail -n 60 "${ARTIFACT_DIR}/resilience.log" || true

  [[ "${outcome}" == "complete" ]] || fail "resilience Job did not complete successfully (${outcome})"
}

summary() {
  if ((${#FAILURES[@]})); then
    log "FAILED with ${#FAILURES[@]} problem(s):"
    printf '  - %s\n' "${FAILURES[@]}"
    exit 1
  fi
  log "PASSED - artifacts in ${ARTIFACT_DIR}"
}

main() {
  require_tools
  local command="${1:-all}"
  local load_seconds
  load_seconds="$(compute_load_seconds)"

  case "${command}" in
    up)
      create_cluster
      load_images
      install_cnpg
      install_chaos_mesh
      install_vfps
      wait_steady_state
      ;;
    scenarios)
      start_loadgen "${load_seconds}"
      run_scenarios
      ;;
    collect)
      collect
      summary
      ;;
    down)
      kind delete cluster --name "${CLUSTER_NAME}"
      ;;
    all)
      log "load window: ${load_seconds}s across scenarios [${SCENARIOS}]"
      create_cluster
      load_images
      install_cnpg
      install_chaos_mesh
      install_vfps
      wait_steady_state
      start_loadgen "${load_seconds}"
      run_scenarios
      collect
      summary
      ;;
    *)
      echo "usage: $0 [all|up|scenarios|collect|down]" >&2
      exit 2
      ;;
  esac
}

main "$@"
