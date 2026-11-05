# Product

<!-- impeccable:product-schema 1 -->

## Platform

web

## Users

- **Data integration staff**: data stewards and trust-center staff at a hospital's data integration
  center. In the admin UI they create and maintain namespaces and projects, manage access grants and
  service accounts, and run CSV pseudonymization jobs.
- **Researchers and study staff**: people who need pseudonyms for a specific study. They work only on
  the namespaces they've been granted: lookups, and CSV jobs that pseudonymize, de-pseudonymize,
  import or export study data.

Other audiences, inferred from the repository rather than confirmed in the interview:

- **Machine clients** (study pipelines, ETL jobs, FHIR tooling) call the gRPC, JSON-transcoded REST
  and FHIR APIs, authenticated as service accounts or with access tokens. Most pseudonym traffic goes
  through these APIs, not the UI.
- **Operators** deploy and run vfps (Helm chart, Docker Compose, PostgreSQL, OIDC, OpenTelemetry).
  They are the main readers of the deployment, configuration and security sections of the docs site.

## Product Purpose

vfps pseudonymizes identifiers for research data. It keeps the mapping between original values
(patient IDs, laboratory order numbers, sample IDs, DICOM accession numbers) and pseudonyms, hands
out pseudonyms, and resolves them back for those allowed to. Success means pseudonyms are created and
resolved fast enough never to hold up a data pipeline, and a mapping, once handed out, is never lost
or silently changed. Only people and machines granted access to a namespace can reach its pseudonyms.

## Positioning

vfps fills the same slot as the Greifswald trust-center tools gPAS and E-PIX. Its own claims:

- Very fast and resource-efficient, with published, reproducible benchmarks (`website/benchmarks.md`).
- Horizontally replicable for highly available deployments.
- gRPC, REST and FHIR APIs from one service.
- Deterministic, oblivious VOPRF pseudonyms (RFC 9497) from a separate key holder that never sees
  the values it pseudonymizes.
- Cloud-native operation: Helm chart, OpenTelemetry metrics and traces, signed container images with
  SLSA Level 3 provenance.
- Open source under Apache 2.0, developed under the MIRACUM GitHub organization.

No comparative claims against gPAS or E-PIX are on record. Don't invent any.

## Operating Context

- Runs inside hospital and research-network infrastructure, typically on Kubernetes, backed by
  PostgreSQL, with OIDC sign-in (e.g. Keycloak) and S3-compatible object storage for CSV job files.
- The admin UI is served at `/ui` by the same service. It is available in English and German, with
  light and dark themes.
- Core workflows: set up a namespace (pseudonym generation method, length, prefix/suffix, optional
  original value validation regex, optional parent namespace, optionally several pseudonyms per
  value); group namespaces into projects; grant users, roles or service accounts per-namespace
  access; run CSV jobs; issue personal and service-account access tokens for API clients.
- The data is identifying health data. De-pseudonymization and reverse lookup reveal original
  values, so they need their own grant.

## Capabilities and Constraints

- **Terminology**: namespace, original value, pseudonym, pseudonym generation method,
  parent/child (multi-level) namespace, multiple pseudonyms per original value, project, grant,
  admin, service account, access token (`vfps_pat_...` personal, `vfps_sat_...` service account),
  CSV job (pseudonymize, de-pseudonymize, import, export).
- A namespace's generation method, length, prefix and suffix are fixed at creation. Only its
  description, validation regex and projects can be edited later.
- Admins manage namespaces, projects, grants and service accounts. A service account is never an
  admin.
- Admin UI stack: Blazor Server with BlazorBlueprint components and Lucide icons. Docs site: Zensical.
- Pseudonym durability takes precedence over write throughput: a pseudonym is acknowledged only once
  it's durably stored.

## Brand Commitments

- Name: **vfps**, lowercase in prose and docs. The UI footer shows "vfps vX.Y.Z".
- Tagline in use: "A very fast and resource-efficient pseudonymization service".
- Existing assets: `website/img/text-logo.svg` (wordmark), `website/img/vfps-lockup-morph-loop.svg`
  (animated lockup on the docs home page), and the V-mark logo in the admin UI header.
- Voice in docs and UI copy: precise and technical, explains why a behavior exists, no marketing
  superlatives beyond the measured performance claims.

## Evidence on Hand

- Benchmarks with full methodology, hardware and PostgreSQL settings: `website/benchmarks.md`. The
  headline figures on the docs home page (~12,000 req/s, P99 < 10 ms, < 2 s cold start, ~1,000 req/s
  on 1 CPU and 128 MiB) come from there.
- Admin UI screenshots in light and dark themes: `website/img/ui/`.
- Feature documentation: `website/*.md`; API contracts: `src/Vfps/Protos/vfps/api/v1/`.
- No testimonials, adopter list, case studies or deployment counts are on record. Don't fabricate
  any.

## Product Principles

1. **A handed-out pseudonym is permanent.** Never trade durability or consistency for speed or
   convenience. Losing or silently regenerating a mapping breaks record linkage downstream.
2. **Access is scoped and earned.** Every view and action respects namespace grants, and revealing
   original values is a separate, deliberate permission.
3. **Performance claims come with receipts.** Speed is a core reason to choose vfps, so every number
   shown has to be reproducible from the published benchmarks.
4. **People in the UI, machines on the API.** Data stewards and study staff must be able to do their
   work in the UI without knowing gRPC. The UI explains consequences (fixed-at-creation settings,
   de-pseudonymization) in plain language.

## Accessibility & Inclusion

- No formal accessibility standard is binding. Keep the UI accessible as general good practice.
- English and German are both first-class UI languages (`src/Vfps/Resources/SharedResources*.resx`).
- Light and dark themes are both supported.
