# Access control

Roles and individual users are granted read, write and reverse-lookup access per namespace. Only
admins (`Authorization__AdminRoles`) can see any of this, and only while `Authorization__IsEnabled`
is `true` - with authorization off, every request already has full access to everything.

Grants are made on the **Namespaces** page: select a namespace in the hierarchy and use _Configure
access_. The **Access Control** page is a read-only overview - the admin roles, and who effectively
holds which permission on each namespace.

A grant names one **scope** (a single namespace, or _All namespaces_ - which also covers namespaces
created later) and one **grantee**:

- a **role**, matched against the caller's `Authorization__RoleClaimType` claims exactly as the
  identity provider emits them; or
- an **email address**, matched against the caller's `email` claim, granting that one person access
  regardless of which roles they hold. **Only a verified address matches**: the caller's token must
  also carry `email_verified: true`, since an unconfirmed address is a self-asserted string and a
  realm allowing self-registration or an unverified address change would otherwise let anyone claim
  someone else's grants. An identity provider that issues no `email_verified` claim at all
  therefore never matches an email grant - use role grants there.

Grants are additive and there are no deny rules - a caller gets the union of everything granted to
any of their roles and to their email address, and the absence of a grant is the denial.
Reverse-lookup (revealing a pseudonym's original value) is a separate, more tightly-scoped
permission than read: read access alone never reveals original values. Grants are **not** inherited
down a [namespace hierarchy](namespaces.md#multi-level-namespaces) - like the generation settings a namespace
carries, access is set per namespace.

Toggling a permission saves it immediately. It takes effect at once on the replica handling the
request; other replicas pick it up within `Authorization__GrantCacheDuration` (30s by default).

Grants scoped to _All namespaces_ can no longer be created or changed through the UI, since the
per-namespace dialog only edits grants belonging to the namespace it was opened for. Existing ones
keep working, and are listed for context wherever they apply.

## Authenticating API calls

With `Authorization__IsEnabled` set, the whole API - gRPC, the JSON-transcoded REST routes
(`/v1/namespaces/...`) and the FHIR operation (`/v1/fhir/$create-pseudonym`) - accepts **bearer
tokens only**. A call arriving without one is refused by the request pipeline before it reaches a
service method: gRPC clients get `UNAUTHENTICATED`, HTTP clients a `401` carrying
`WWW-Authenticate: Bearer`. A machine client obtains its token from the authority the usual way - an
OAuth2 `client_credentials` grant against a confidential client of its own, separate from
`Authorization__ClientId`, which is the admin UI's - and the token's `aud` has to match
`Authorization__Audience`. Namespace access is then resolved from the token's roles and `email`
claim against the grants described above, so a valid token still only reaches the namespaces it has
been granted.

A client that can't get a confidential client of its own provisioned in the realm can instead
present a **vfps-issued access token** - `Authorization: Bearer vfps_pat_...` or `vfps_sat_...` -
once `Authorization__AccessTokens__IsEnabled` is on. The two credentials share the header and are
told apart by that prefix, so each request reaches exactly one handler: a vfps token is never sent
to the identity provider, and keeps working while the authority is unreachable. See
[Access Tokens](#access-tokens).

That pipeline-level `401` carries no body, so it is the one FHIR error the service answers without
an `OperationOutcome`. Every refusal made _after_ authentication - including a `403` for a namespace
the token holds no grant on - is an `OperationOutcome`.

The gRPC health-checking service and the `/healthz`, `/livez` and `/readyz` endpoints stay
anonymous, so probes and load balancers need no token. gRPC server reflection does not: it
describes the API, so listing the services takes a token like calling them does - grpcurl sends
its `-H` header with both. An admin's browser session, on the other
hand, does _not_ authenticate an API call: the login cookie is for the admin UI, and the API reads
bearer tokens only. The bundled Swagger UI at `/swagger` therefore gains an **Authorize** button
when authorization is enabled - paste an access token there, without the `Bearer ` prefix, before
using _Try it out_.

## Access tokens

Not every client can obtain a token from the identity provider. A data-integration job may belong
to a team that can't get a confidential client provisioned in the realm, or run in a tool with no
OAuth2 support at all. For those, vfps can issue its own bearer credentials - set
`Authorization__AccessTokens__IsEnabled` to `true` (it is off by default) and two things appear in
the admin UI.

**Personal access tokens** are self-service: any signed-in user creates them on the **Access
tokens** page, and a token _acts as its creator_. It captures the identity they hold at that
moment - their subject, their verified email address and their roles - and every request made
with it resolves against the same grants their browser session does. Nobody can grant themselves
anything by creating one, which is why no admin role is needed to do it.

**Service accounts** are the other half, and are admin-only: they are principals of their own, for
a client that belongs to no person. Create one on the **Service accounts** page, then grant it
access exactly like a role or a user - _Configure access_ on the Namespaces page, with **Service
account** as the grantee type - and issue it a token. Rotating that token is issuing a second one,
rolling it out, and revoking the first; the grants belong to the account, so they never move.

Both kinds are presented the same way as an identity provider's token, so no client needs changing:

```sh
# the token as the admin UI showed it, once
export VFPS_TOKEN="vfps_sat_..."

curl -H "Authorization: Bearer $VFPS_TOKEN" \
  http://localhost:8080/v1/namespaces/test/pseudonyms \
  -H "Content-Type: application/json" \
  -d '{"originalValue": "to be pseudonymized"}'

grpcurl \
  -plaintext \
  -H "authorization: Bearer $VFPS_TOKEN" \
  -d '{"namespace": "test", "originalValue": "to be pseudonymized"}' \
  127.0.0.1:8081 \
  vfps.api.v1.PseudonymService/Create
```

A token is shown **once**, when it is created: only its SHA-256 hash is stored, so one that has
been lost can be replaced but never recovered. The `vfps_pat_`/`vfps_sat_` prefix is fixed so that
a leaked token is findable by a secret scanner and recognizable in a log.

Things worth knowing before switching this on:

- **A token always expires.** `Authorization__AccessTokens__DefaultLifetime` pre-fills the form and
  `Authorization__AccessTokens__MaximumLifetime` caps it. Expiry is the bound that holds without
  anyone noticing anything is wrong - a token whose owner has left, or that was pasted into a CI
  log, stops working on its own.
- **Revoking is immediate on the replica that does it**, and takes effect on the others within
  `Authorization__GrantCacheDuration`, the same window that bounds a withdrawn grant. Admins see
  every token in the deployment, and can revoke anyone's, on the Access tokens page.
- **A service account is never an admin**, whatever it is granted: it cannot create or delete a
  namespace, manage access grants, or open the Hangfire dashboard. A personal access token, by
  contrast, is as powerful as its owner - an admin's token can do admin-only API operations, though
  no token can sign in to the admin UI, which is cookie-authenticated.
- **No token can create another token.** Otherwise a leaked one could renew itself indefinitely and
  its expiry would stop meaning anything.
- **A personal token's identity is a snapshot.** Roles withdrawn at the identity provider are not
  reflected in a token already issued - revoke it (or let it expire) when someone's roles change.
  Grants, on the other hand, are read live, so editing or removing one takes effect at once.
- **Last used** is recorded per token, written back on a timer
  (`Authorization__AccessTokens__UsageFlushInterval`) rather than per request, so it lags by up to
  that interval. It is there to tell a token still in use from one nobody has touched in months.

Deleting a service account deletes its tokens _and_ its access grants, so that an account later
re-created under the same name doesn't silently inherit them.

### Noticing a token before it expires

A token close to its expiry is marked **Expires soon** on the Access tokens and Service accounts
pages, `Authorization__AccessTokens__ExpiryWarningPeriod` (14 days by default) ahead. That's enough
for a personal token, since its owner is the one using it. A service-account token usually sits in
a pipeline that nobody watches, so it is also exported as a metric to alert on:

- **`vfps_service_account_token_expiration_timestamp_seconds`**, a gauge holding each unrevoked
  service-account token's expiry as a Unix timestamp, labelled `service_account`, `token_id` and
  `token_name`. An expired token stays reported until it is revoked or deleted, so an alert keeps
  firing past the expiry instead of resolving the moment the token stops working. Revoking the old
  token once its replacement is rolled out is what clears it. Every replica reports the same
  values, so aggregate with `max()`.

```promql
# days left on each service-account token
(max by (service_account, token_id, token_name) (vfps_service_account_token_expiration_timestamp_seconds) - time()) / 86400
```

The Helm chart ships this as two alerts, `VfpsServiceAccountTokenExpiringSoon` and
`VfpsServiceAccountTokenExpired`, behind `prometheusRule.enabled`.

## Migrating from `Authorization__NamespaceRules`

The static `Authorization__NamespaceRules` section is gone and is **not** imported automatically -
an upgrade leaves every non-admin without access until an admin re-creates the equivalent grants
with _Configure access_ on the Namespaces page. Each old rule maps directly: the rule's `Namespace`
becomes the namespace whose access is being configured, and each role in `ReadRoles`/`WriteRoles`/
`ReverseLookupRoles` becomes a role grant with the matching permission switched on. A rule scoped
to `"*"` has no UI equivalent any more and has to be re-created per namespace. Remove the
`Authorization__NamespaceRules__*` variables from your deployment; they are now ignored.
