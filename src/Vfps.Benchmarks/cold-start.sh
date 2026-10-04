#!/usr/bin/env bash
#
# Measures vfps' cold start: the time from `docker run` until the first gRPC
# PseudonymService/Create call is answered. See the "Cold start" section of website/benchmarks.md
# for what each column means and sample results.
#
# Expects the PostgreSQL from the repository's compose.yaml (`docker compose up -d --wait`) and a
# Linux Docker Engine: the container shares the host network, both to reach that PostgreSQL on
# 127.0.0.1:35432 and so the polling below talks to Kestrel directly rather than to docker-proxy,
# which accepts connections on a published port before the app inside is listening.
#
# Usage: src/Vfps.Benchmarks/cold-start.sh [runs] [extra docker run args...]
#   e.g. src/Vfps.Benchmarks/cold-start.sh 10 --cpus=1 --memory=128m
#
set -Eeuo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"

VFPS_IMAGE_TAG="${VFPS_IMAGE_TAG:-v1.22.4}" # x-release-please-version
VFPS_IMAGE="${VFPS_IMAGE:-ghcr.io/miracum/vfps:${VFPS_IMAGE_TAG}}"
CONTAINER_NAME="${CONTAINER_NAME:-vfps-cold-start}"
NAMESPACE="${NAMESPACE:-cold-start}"
# The vfps service's connection string from compose.yaml, pointed at the published PostgreSQL port.
CONNECTION_STRING="${CONNECTION_STRING:-Host=127.0.0.1:35432;Database=vfps;Timeout=60;Max Auto Prepare=5;Application Name=vfps;Maximum Pool Size=50;Gss Encryption Mode=Disable;}"
# Per start, before giving up and printing the container's log.
TIMEOUT_MS="${TIMEOUT_MS:-120000}"

RUNS="${1:-10}"
shift || true
EXTRA_DOCKER_ARGS=("$@")

for cmd in docker grpcurl; do
  command -v "${cmd}" >/dev/null || {
    echo "${cmd} is required" >&2
    exit 1
  }
done

# Usage: grpc <method> <request json>
grpc() {
  grpcurl -plaintext -connect-timeout 1 \
    -import-path "${REPO_ROOT}/src/Vfps/Protos" \
    -proto vfps/api/v1/pseudonyms.proto \
    -proto vfps/api/v1/namespaces.proto \
    -d "$2" 127.0.0.1:8081 "$1"
}

# Same environment as the vfps service in compose.yaml, minus the authorization and S3 settings
# that only matter once those features are enabled.
vfps() {
  docker run --network host \
    -e DOTNET_EnableDiagnostics=0 \
    -e PGUSER=postgres \
    -e PGPASSWORD=postgres \
    -e "ConnectionStrings__PostgreSQL=${CONNECTION_STRING}" \
    "$@"
}

start_vfps() {
  vfps -d --name "${CONTAINER_NAME}" "${EXTRA_DOCKER_ARGS[@]}" "${VFPS_IMAGE}" >/dev/null
}

# A graceful stop rather than `docker rm -f`: killed outright, vfps never deregisters its Hangfire
# server or releases the distributed locks it holds, and the next start then spends its first
# minute waiting on them.
stop_vfps() {
  docker stop "${CONTAINER_NAME}" >/dev/null 2>&1 || true
  docker rm -f "${CONTAINER_NAME}" >/dev/null 2>&1 || true
}
trap stop_vfps EXIT

now_ms() { echo $(($(date +%s%N) / 1000000)); }
to_ms() { echo $(($(date -d "$1" +%s%N) / 1000000)); }

give_up_after() {
  if (($(now_ms) - $1 > TIMEOUT_MS)); then
    echo "vfps did not answer within ${TIMEOUT_MS} ms:" >&2
    docker logs "${CONTAINER_NAME}" 2>&1 | tail -20 >&2
    exit 1
  fi
}

median() {
  printf '%s\n' "$@" | sort -n | awk '{ v[NR] = $1 } END { print (NR % 2 ? v[(NR + 1) / 2] : int((v[NR / 2] + v[NR / 2 + 1]) / 2)) }'
}

stop_vfps
# Anything already answering here - a vfps started from the IDE, say - would be timed instead.
if (echo >/dev/tcp/127.0.0.1/8081) 2>/dev/null; then
  echo "port 8081 is already in use, stop whatever is listening on it first" >&2
  exit 1
fi

# Untimed setup: migrate the schema the way the Helm chart's migrations Job does, so the timed
# starts don't include it, and make sure the namespace exists.
if ! output="$(vfps --rm "${VFPS_IMAGE}" /opt/vfps/Vfps.dll migrate 2>&1)"; then
  echo "${output}" >&2
  exit 1
fi

setup_start="$(now_ms)"
start_vfps
until grpc vfps.api.v1.NamespaceService/Create "{\"name\":\"${NAMESPACE}\",\"pseudonymLength\":32}" >/dev/null 2>&1 ||
  grpc vfps.api.v1.NamespaceService/Get "{\"name\":\"${NAMESPACE}\"}" >/dev/null 2>&1; do
  give_up_after "${setup_start}"
  sleep 0.1
done
stop_vfps

columns=(total listening first_response first_request warm_request)
declare -A results
printf '%-6s' run
printf ' %17s' "${columns[@]/%/_ms}"
echo

for run in $(seq 1 "${RUNS}"); do
  run_start="$(now_ms)"
  start_vfps

  # A refused connection fails within a few milliseconds, so this polls as fast as grpcurl starts.
  while :; do
    request_start="$(now_ms)"
    if grpc vfps.api.v1.PseudonymService/Create \
      "{\"namespace\":\"${NAMESPACE}\",\"originalValue\":\"cold-${run_start}\"}" >/dev/null 2>&1; then
      break
    fi
    give_up_after "${run_start}"
  done
  first_response="$(now_ms)"

  warm_start="$(now_ms)"
  grpc vfps.api.v1.PseudonymService/Create \
    "{\"namespace\":\"${NAMESPACE}\",\"originalValue\":\"warm-${run_start}\"}" >/dev/null
  warm_end="$(now_ms)"

  container_start="$(to_ms "$(docker inspect -f '{{.State.StartedAt}}' "${CONTAINER_NAME}")")"
  # Docker timestamps each log line as it arrives, which is good enough to place Kestrel's startup
  # message without changing the app's own log format.
  listening_at="$(docker logs --timestamps "${CONTAINER_NAME}" 2>&1 |
    awk '/Now listening on: .*:8081/ && !ts { ts = $1 } END { print ts }')"

  results[total]+=" $((first_response - run_start))"
  results[listening]+=" $(($(to_ms "${listening_at}") - container_start))"
  results[first_response]+=" $((first_response - container_start))"
  results[first_request]+=" $((first_response - request_start))"
  results[warm_request]+=" $((warm_end - warm_start))"

  printf '%-6s' "${run}"
  for column in "${columns[@]}"; do
    # shellcheck disable=SC2086 # word splitting is the point: it's a space-separated list
    set -- ${results[${column}]}
    printf ' %17s' "${!#}"
  done
  echo

  stop_vfps
done

printf '%-6s' median
for column in "${columns[@]}"; do
  # shellcheck disable=SC2086
  printf ' %17s' "$(median ${results[${column}]})"
done
echo
