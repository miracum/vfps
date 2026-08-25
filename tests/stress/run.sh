#!/bin/sh
# Sustained gRPC load runner used by the chaos-testing workflow (see tests/chaos/workflow.yaml).
# Creates a namespace, then keeps a ramping-then-steady load of PseudonymService/Create calls
# running while chaos-mesh injects faults, and fails if the observed error rate is too high.
set -eu

VFPS_GRPC_ADDRESS="${VFPS_GRPC_ADDRESS:-127.0.0.1:8081}"
NAMESPACE="${STRESS_NAMESPACE:-vfps-stress-test}"
DURATION="${STRESS_DURATION:-10m}"
MAX_CONCURRENCY="${STRESS_MAX_CONCURRENCY:-100}"
RAMP_DURATION="${STRESS_RAMP_DURATION:-2m}"
MAX_ERROR_RATE_PERCENT="${STRESS_MAX_ERROR_RATE_PERCENT:-0.1}"
REPORT_DIR="${STRESS_REPORT_DIR:-/tmp/reports}"
# the .proto files import each other as "Protos/..." (see src/Vfps/Protos/vfps/api/v1/*.proto),
# so the import path is the directory containing Protos/, not Protos/ itself.
IMPORT_PATH="$(dirname "$0")"
PROTO_DIR="${IMPORT_PATH}/Protos"

mkdir -p "${REPORT_DIR}"

echo "creating namespace '${NAMESPACE}' on ${VFPS_GRPC_ADDRESS} (ignoring AlreadyExists)"
ghz \
  --insecure \
  --import-paths "${IMPORT_PATH}" \
  --proto "${PROTO_DIR}/vfps/api/v1/namespaces.proto" \
  --call vfps.api.v1.NamespaceService/Create \
  -d "{\"name\": \"${NAMESPACE}\", \"pseudonymGenerationMethod\": \"PSEUDONYM_GENERATION_METHOD_SECURE_RANDOM_BASE64URL_ENCODED\", \"pseudonymLength\": 16, \"pseudonymPrefix\": \"stress-\"}" \
  -n 1 \
  -c 1 \
  --format=pretty \
  "${VFPS_GRPC_ADDRESS}" || true

echo "running sustained load: ramping to ${MAX_CONCURRENCY} concurrent workers over ${RAMP_DURATION}, then holding for ${DURATION}"
ghz \
  --insecure \
  --import-paths "${IMPORT_PATH}" \
  --proto "${PROTO_DIR}/vfps/api/v1/pseudonyms.proto" \
  --call vfps.api.v1.PseudonymService/Create \
  -d "{\"namespace\": \"${NAMESPACE}\", \"originalValue\": \"{{newUUID}}\"}" \
  -z "${DURATION}" \
  --concurrency-schedule=line \
  --concurrency-start=1 \
  --concurrency-end="${MAX_CONCURRENCY}" \
  --concurrency-step=1 \
  --concurrency-max-duration="${RAMP_DURATION}" \
  --count-errors \
  --format=json \
  --output="${REPORT_DIR}/ghz-report.json" \
  "${VFPS_GRPC_ADDRESS}"

total=$(jq -r '.count' "${REPORT_DIR}/ghz-report.json")
errors=$(jq -r '[.errorDistribution[]?] | add // 0' "${REPORT_DIR}/ghz-report.json")
error_rate=$(jq -n --argjson total "${total}" --argjson errors "${errors}" \
  'if $total == 0 then 100 else ($errors / $total) * 100 end')

echo "total requests: ${total}, errors: ${errors}, error rate: ${error_rate}%"

if [ "$(jq -n --argjson rate "${error_rate}" --argjson max "${MAX_ERROR_RATE_PERCENT}" '$rate > $max')" = "true" ]; then
  echo "error rate ${error_rate}% exceeds threshold of ${MAX_ERROR_RATE_PERCENT}%" >&2
  exit 1
fi

echo "stress test passed"
