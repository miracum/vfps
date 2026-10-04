# Security

## Reporting a vulnerability

Please use the project's [private vulnerability reporting feature](https://github.com/miracum/vfps/security/advisories)
to report any vulnerabilities. For more information, see
<https://docs.github.com/en/code-security/security-advisories/guidance-on-reporting-and-writing/privately-reporting-a-security-vulnerability>.

Only the most recent major version is regularly updated and receives security fixes.

## Image signature and provenance verification

Prerequisites:

- [cosign](https://github.com/sigstore/cosign/releases)
- [GitHub CLI](https://cli.github.com/), logged in to any GitHub account (`gh auth login`) - it
  fetches the provenance from GitHub's attestations API
- [crane](https://github.com/google/go-containerregistry/releases)

All released container images are signed using [cosign](https://github.com/sigstore/cosign) and SLSA Level 3 provenance is available for verification.

<!-- x-release-please-start-version -->

```sh
IMAGE=ghcr.io/miracum/vfps:v1.22.3
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

gh attestation verify "oci://${IMAGE_DIGEST_PINNED}" \
   --repo miracum/vfps \
   --signer-workflow miracum/.github/.github/workflows/standard-build.yaml \
   --source-ref "refs/tags/${IMAGE_TAG}"
```

<!-- x-release-please-end -->

The images are built by a reusable workflow in [miracum/.github](https://github.com/miracum/.github),
which is why `--signer-workflow` names that workflow rather than one in this repository.

The provenance is also pushed to the registry next to the image, so it can be enforced at deploy
time - see GitHub's guide to
[enforcing artifact attestations with a Kubernetes admission controller](https://docs.github.com/en/actions/how-tos/secure-your-work/use-artifact-attestations/enforce-artifact-attestations).

## Compose artifact verification

The [getting-started](getting-started.md) Compose stack is an OCI artifact signed with cosign,
with a build provenance attestation. Unlike the images, it is signed by this repository's own `ci`
workflow. Verify it by digest, then run that digest:

<!-- x-release-please-start-version -->

```sh
ARTIFACT=ghcr.io/miracum/vfps/compose/getting-started:v1.22.3
DIGEST=$(crane digest "${ARTIFACT}")
ARTIFACT_DIGEST_PINNED="${ARTIFACT%:*}@${DIGEST}"
ARTIFACT_TAG="${ARTIFACT##*:}"

cosign verify \
   --certificate-oidc-issuer=https://token.actions.githubusercontent.com \
   --certificate-identity="https://github.com/miracum/vfps/.github/workflows/ci.yaml@refs/tags/${ARTIFACT_TAG}" \
   "${ARTIFACT_DIGEST_PINNED}"

gh attestation verify "oci://${ARTIFACT_DIGEST_PINNED}" --repo miracum/vfps

docker compose -f "oci://${ARTIFACT_DIGEST_PINNED}" up
```

<!-- x-release-please-end -->

Every image inside the artifact is pinned to a digest, so verifying the artifact fixes which
images run. Verify the vfps image itself as shown above.
