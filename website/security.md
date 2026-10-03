# Security

## Reporting a vulnerability

Please use the project's [private vulnerability reporting feature](https://github.com/miracum/vfps/security/advisories)
to report any vulnerabilities. For more information, see
<https://docs.github.com/en/code-security/security-advisories/guidance-on-reporting-and-writing/privately-reporting-a-security-vulnerability>.

Only the most recent major version is regularly updated and receives security fixes.

## Image signature and provenance verification

Prerequisites:

- [cosign](https://github.com/sigstore/cosign/releases)
- [slsa-verifier](https://github.com/slsa-framework/slsa-verifier/releases)
- [crane](https://github.com/google/go-containerregistry/releases)

All released container images are signed using [cosign](https://github.com/sigstore/cosign) and SLSA Level 3 provenance is available for verification.

<!-- x-release-please-start-version -->

```sh
IMAGE=ghcr.io/miracum/vfps:v1.22.1
DIGEST=$(crane digest "${IMAGE}")
IMAGE_DIGEST_PINNED="ghcr.io/miracum/vfps@${DIGEST}"
IMAGE_TAG="${IMAGE#*:}"

cosign verify \
   --certificate-oidc-issuer=https://token.actions.githubusercontent.com \
   --certificate-identity-regexp="https://github.com/miracum/.github/.github/workflows/standard-build.yaml@.*" \
   --certificate-github-workflow-name="ci" \
   --certificate-github-workflow-repository="miracum/vfps" \
   --certificate-github-workflow-trigger="release" \
   --certificate-github-workflow-ref="refs/tags/${IMAGE_TAG}" \
   "${IMAGE_DIGEST_PINNED}"

slsa-verifier verify-image \
    --source-uri github.com/miracum/vfps \
    --source-tag ${IMAGE_TAG} \
    "${IMAGE_DIGEST_PINNED}"
```

<!-- x-release-please-end -->

See also <https://github.com/slsa-framework/slsa-github-generator/tree/main/internal/builders/container#verification> for details on verifying the image integrity using automated policy controllers.

## Compose artifact verification

The [Docker Compose](deployment/compose.md) stacks are OCI artifacts signed with cosign, with a
build provenance attestation. Unlike the images, they are signed by this repository's own `ci`
workflow. Verify one by digest, then run that digest:

<!-- x-release-please-start-version -->

```sh
ARTIFACT=ghcr.io/miracum/vfps/compose/production:v1.22.1
DIGEST=$(crane digest "${ARTIFACT}")
ARTIFACT_DIGEST_PINNED="${ARTIFACT%:*}@${DIGEST}"
ARTIFACT_TAG="${ARTIFACT##*:}"

cosign verify \
   --certificate-oidc-issuer=https://token.actions.githubusercontent.com \
   --certificate-identity="https://github.com/miracum/vfps/.github/workflows/ci.yaml@refs/tags/${ARTIFACT_TAG}" \
   "${ARTIFACT_DIGEST_PINNED}"

gh attestation verify "oci://${ARTIFACT_DIGEST_PINNED}" --repo miracum/vfps

docker compose --env-file vfps.env -f "oci://${ARTIFACT_DIGEST_PINNED}" up -d
```

<!-- x-release-please-end -->

Every image inside the artifact is pinned to a digest, so verifying the artifact fixes which
images run. Verify the vfps image itself as shown above.
