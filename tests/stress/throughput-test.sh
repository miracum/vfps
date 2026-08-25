#!/bin/sh
# Finds the maximum sustainable throughput of PseudonymService/Create by running ghz at
# increasing concurrency levels and reporting the achieved RPS, p99 latency, and error rate
# at each one. The "maximum throughput" is the highest RPS reached before errors appear or
# RPS stops increasing with concurrency.
#
# Requires ghz (https://ghz.sh) and jq to be installed locally.
#
# Usage: tests/stress/throughput-test.sh [host:port]
set -eu

VFPS_GRPC_ADDRESS="${1:-${VFPS_GRPC_ADDRESS:-127.0.0.1:8081}}"
NAMESPACE="${STRESS_NAMESPACE:-throughput-test}"
DURATION_PER_LEVEL="${STRESS_DURATION_PER_LEVEL:-15s}"
CONCURRENCY_LEVELS="${STRESS_CONCURRENCY_LEVELS:-10 25 50 100 200 400 800}"
REPO_ROOT="$(CDPATH= cd -- "$(dirname "$0")/../.." && pwd)"
# the .proto files import each other as "Protos/..." (see src/Vfps/Protos/vfps/api/v1/*.proto),
# so the import path is src/Vfps/, not src/Vfps/Protos/ itself.
IMPORT_PATH="${REPO_ROOT}/src/Vfps"
PROTO_DIR="${IMPORT_PATH}/Protos"
WORK_DIR="$(mktemp -d)"
trap 'rm -rf "${WORK_DIR}"' EXIT

echo "creating namespace '${NAMESPACE}' on ${VFPS_GRPC_ADDRESS} (ignoring AlreadyExists)"
ghz \
  --insecure \
  --import-paths "${IMPORT_PATH}" \
  --proto "${PROTO_DIR}/vfps/api/v1/namespaces.proto" \
  --call vfps.api.v1.NamespaceService/Create \
  -d "{\"name\": \"${NAMESPACE}\", \"pseudonymGenerationMethod\": \"PSEUDONYM_GENERATION_METHOD_SECURE_RANDOM_BASE64URL_ENCODED\", \"pseudonymLength\": 16}" \
  -n 1 \
  -c 1 \
  --format=pretty \
  "${VFPS_GRPC_ADDRESS}" || true

printf '%-12s %10s %10s %10s %10s\n' "concurrency" "rps" "p99(ms)" "errors" "err-rate%"

for concurrency in ${CONCURRENCY_LEVELS}; do
  report="${WORK_DIR}/${concurrency}.json"

  ghz \
    --insecure \
    --import-paths "${IMPORT_PATH}" \
    --proto "${PROTO_DIR}/vfps/api/v1/pseudonyms.proto" \
    --call vfps.api.v1.PseudonymService/Create \
    -d "{\"namespace\": \"${NAMESPACE}\", \"originalValue\": \"{{newUUID}}\"}" \
    -z "${DURATION_PER_LEVEL}" \
    -c "${concurrency}" \
    --count-errors \
    --format=json \
    --output="${report}" \
    "${VFPS_GRPC_ADDRESS}"

  rps=$(jq -r '.rps' "${report}")
  p99_ms=$(jq -r '(.latencyDistribution[]? | select(.percentage == 99) | .latency) // 0' "${report}")
  p99_ms=$(awk -v ns="${p99_ms}" 'BEGIN { printf "%.2f", ns / 1000000 }')
  total=$(jq -r '.count' "${report}")
  errors=$(jq -r '[.errorDistribution[]?] | add // 0' "${report}")
  err_rate=$(jq -n --argjson total "${total}" --argjson errors "${errors}" \
    'if $total == 0 then 0 else ($errors / $total) * 100 end')

  printf '%-12s %10.1f %10s %10s %10.2f\n' "${concurrency}" "${rps}" "${p99_ms}" "${errors}" "${err_rate}"
done

echo
echo "Max throughput is the highest rps reached before err-rate rises or rps plateaus/declines."
