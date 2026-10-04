---
title: vfps
description: A very fast and resource-efficient pseudonymization service for research data.
hide:
  - navigation
  - toc
---

# vfps { .vfps-visually-hidden }

<div class="vfps-hero" markdown>

![vfps](img/vfps-lockup-morph-loop.svg)

<p class="vfps-tagline">A very fast and resource-efficient pseudonymization service for research data,
with gRPC, REST and FHIR APIs, an admin UI, and highly available deployments.</p>

[Get started](getting-started.md){ .md-button .md-button--primary }
[View on GitHub](https://github.com/miracum/vfps){ .md-button }

</div>

<div class="vfps-stats" markdown>

<div><strong>~12,000 req/s</strong>pseudonym creation on a single instance</div>
<div><strong>&lt; 10 ms</strong>P99 latency</div>
<div><strong>~1,000 req/s</strong>with just 1 CPU and 128 MiB memory</div>

</div>

<p align="center" markdown><small>[How these numbers were measured](benchmarks.md)</small></p>

<!-- prettier-ignore-start -->

<div class="grid cards" markdown>

- :lucide-folder-tree:{ .lg .middle } **Namespaces, one level or many**

    ---

    Each namespace has its own pseudonym format and length. Build multi-level
    pseudonymization by chaining child namespaces off a parent, and hand out several
    pseudonyms per original value where a study needs them.

    [:octicons-arrow-right-24: Namespaces](namespaces.md)

- :lucide-key-round:{ .lg .middle } **Oblivious, verifiable pseudonyms**

    ---

    Derive deterministic pseudonyms with an RFC 9497 VOPRF. The key holder never sees the
    value it pseudonymizes.

    [:octicons-arrow-right-24: VOPRF pseudonyms](voprf.md)

- :lucide-flame:{ .lg .middle } **gRPC, REST and FHIR**

    ---

    Every operation is available over gRPC and JSON-transcoded REST, described by an OpenAPI
    specification. FHIR clients can use the MII pseudonymization operations.

    [:octicons-arrow-right-24: MII FHIR pseudonymization operations](https://medizininformatik-initiative.github.io/mii-interface-module-pseudonymization/)

- :lucide-layout-dashboard:{ .lg .middle } **Admin UI and CSV jobs**

    ---

    Browse and create namespaces, then pseudonymize, de-pseudonymize, import or
    export larger-than-memory CSV files as background jobs that stream straight
    to and from S3-compatible storage.

    [:octicons-arrow-right-24: Admin UI](admin-ui.md)

- :lucide-shield-check:{ .lg .middle } **Namespace-scoped access control**

    ---

    OIDC sign-in, separate read, write and reverse-lookup grants per namespace.
    Vfps-issued personal access tokens and service accounts with fine-grained access
    for machine users.

    [:octicons-arrow-right-24: Access control](access-control.md)

- :lucide-server:{ .lg .middle } **Built for production**

    ---

    Stateless replicas on PostgreSQL, a hardened Helm chart,
    and OpenTelemetry metrics and traces.

    [:octicons-arrow-right-24: Production deployment](deployment/production.md)

</div>

<!-- prettier-ignore-end -->

## Try it in a minute

<!-- x-release-please-start-version -->

```sh
docker compose -f oci://ghcr.io/miracum/vfps/compose/getting-started:v1.22.2 up
```

<!-- x-release-please-end -->

Then create a namespace and a first pseudonym through the REST API:

```sh
curl -X POST http://localhost:8080/v1/namespaces \
  -H "Content-Type: application/json" \
  -d '{"name": "test", "pseudonymGenerationMethod": "PSEUDONYM_GENERATION_METHOD_SECURE_RANDOM_BASE64URL_ENCODED", "pseudonymLength": 32}'

curl -X POST http://localhost:8080/v1/namespaces/test/pseudonyms \
  -H "Content-Type: application/json" \
  -d '{"originalValue": "to be pseudonymized"}'
```

[Continue with the getting started guide](getting-started.md).

## Supply chain you can verify

Every released container image is signed with [cosign](https://github.com/sigstore/cosign) and
ships with [SLSA Level 3](https://slsa.dev) build provenance, and the repository is continuously
assessed by the [OpenSSF Scorecard](https://scorecard.dev/viewer/?uri=github.com/miracum/vfps).
See [Security](security.md) for how to verify an image before deploying it.
