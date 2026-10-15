#!/usr/bin/env bash
# Generates website/api-reference.md from the API's .proto files and the comments in them, using
# protoc-gen-doc (https://github.com/pseudomuto/protoc-gen-doc) and scripts/api-reference.md.tmpl.
#
# Run by the docs workflow before the site is built, and by hand before previewing it. Needs Docker.
# The page is not committed: the protos are the single source of truth for the API, so the
# reference can't drift from them.
set -euo pipefail

# renovate: datasource=docker depName=pseudomuto/protoc-gen-doc
PROTOC_GEN_DOC_IMAGE="pseudomuto/protoc-gen-doc:1.5.1@sha256:779263a6dc01fbe375298c4e556d784640539e7b2bd433a50588285da88b74d5"

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
proto_root="${repo_root}/src/Vfps/Protos"
output_dir="${repo_root}/website"

mapfile -t api_protos < <(cd "${proto_root}" && find vfps/api/v1 -name '*.proto' | sort)

# The image's entrypoint script runs protoc once per file, and every run writes the same output
# file, so protoc is called directly instead. It is rooted at the Protos directory, the same root
# Vfps.csproj compiles from, which makes a single include path resolve every import.
docker run --rm \
  --user "$(id -u):$(id -g)" \
  --entrypoint protoc \
  --volume "${proto_root}:/protos:ro" \
  --volume "${repo_root}/scripts:/templates:ro" \
  --volume "${output_dir}:/out" \
  "${PROTOC_GEN_DOC_IMAGE}" \
  --proto_path=/protos \
  --doc_out=/out \
  --doc_opt=/templates/api-reference.md.tmpl,api-reference.md \
  "${api_protos[@]}"

echo "Wrote website/api-reference.md"
