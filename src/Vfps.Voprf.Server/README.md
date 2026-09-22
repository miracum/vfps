# Vfps.Voprf.Server

The key-holding half of [`Vfps.Voprf`](../Vfps.Voprf/README.md), behind gRPC.

```text
client                          this server
------                          -----------
Blind(identifier)  ──────────▶  BlindEvaluate
                                  evaluatedElement = k · blindedElement
                   ◀──────────    + a DLEQ proof that k is the published key
Finalize(...)
  verify proof
  unblind  ──▶  pseudonym
```

> **This service does not return pseudonyms, and cannot.** It never learns what it is
> pseudonymizing - that is the property the whole arrangement exists to provide. The pseudonym
> is computed on the client, from the evaluated element. A service that accepted raw identifiers
> and handed back pseudonyms would be given exactly the data this design withholds; that is the
> thing not to build.

## Running it for development

```bash
dotnet run --project src/Vfps.Voprf.Server
```

`appsettings.Development.json` turns hardening off and generates an ephemeral key, so the
server listens on plaintext HTTP/2 at `:8081` with no credentials and gRPC reflection on, and
`grpcurl -plaintext localhost:8081 list` works. It logs its posture at startup and warns loudly
that it is unprotected.

## Hardening

Every protection is on by default. One switch turns them all off:

```jsonc
"VoprfServer": { "Hardening": { "IsEnabled": false } }
```

**Startup fails if that is set outside the Development environment.** The relaxed configuration
cannot reach production by being forgotten - carrying it there takes deliberately setting
`ASPNETCORE_ENVIRONMENT` too.

| Enforced when hardened | Setting | Default |
| --- | --- | --- |
| TLS required | `Hardening:RequireTls` | `true` |
| Callers authenticated | `Hardening:RequireAuthentication` | `true` |
| Rate limit per caller | `Hardening:RateLimit` | 600 / minute |
| Batch cap | `MaxBatchSize` | 128 |
| Request size cap | `Hardening:MaxReceiveMessageSizeBytes` | 64 KiB |
| gRPC reflection | `Hardening:EnableReflection` | `false` |
| Exception detail to callers | `Hardening:EnableDetailedErrors` | `false` |
| Ephemeral keys | `Key:Source: Ephemeral` | refused |

Health checks (`/healthz` and `grpc.health.v1.Health`) are always anonymous and exempt from
the TLS check, so a probe on the pod network still works.

`RequireTls` is independent of the rest: turning it off serves gRPC over plaintext with
authentication, rate limiting and everything else still enforced, for a deployment behind a mesh
that terminates TLS. The one pairing refused is plaintext with `Mode: ClientCertificate` - a
client certificate is presented during the TLS handshake, so no caller could ever authenticate
and every request would be refused with a 403 indistinguishable from bad credentials.

### Why this needs protecting at all

Evaluation reveals nothing about any input, so the service leaks nothing by answering. What it
does is answer *with the key*: anyone who can reach it can pseudonymize anything they can
guess. Where the input space is enumerable - national IDs, email addresses, dates of birth -
that is the whole of the confidentiality the pseudonyms have. Reaching the service is therefore
the security boundary, which is why authentication is on by default and the rate limit exists
to make bulk enumeration slow enough to notice.

### Authentication

`Authentication:Mode` is `ClientCertificate` (default) or `Jwt`.

**Mutual TLS** suits a service called by other services: no identity provider to depend on, and
the caller is identified before the first byte of the body.

```jsonc
"Authentication": {
  "Mode": "ClientCertificate",
  "ClientCertificate": { "AllowedThumbprints": ["A1B2…"], "CheckRevocation": false }
}
```

Leaving `AllowedThumbprints` empty accepts any certificate chaining to a trusted root, which is
only as narrow as that root. Name a thumbprint per caller unless the root exists solely to issue
certificates for this service.

**OIDC bearer tokens** match what the main vfps service already uses:

```jsonc
"Authentication": {
  "Mode": "Jwt",
  "Jwt": { "Authority": "https://idp/realms/vfps", "Audience": "vfps-voprf", "RequiredScope": "voprf:evaluate" }
}
```

Set `Audience` - leaving it empty disables audience validation, which makes every token the
issuer hands out, including ones minted for unrelated applications, a licence to use this key.
`RequiredScope` narrows it further.

## The key

Generate a seed with OpenSSL and mount it:

```bash
openssl rand -out voprf.seed 32
```

```jsonc
"Key": { "Source": "Seed", "FilePath": "/run/secrets/voprf.seed", "KeyId": "v1" }
```

That is the whole of it. `Source` defaults to `Seed`, and any 32 random bytes are a valid seed.

### Why a seed rather than a key

A private key is a ristretto255 *scalar* - an integer below the group order, which sits just
above 2^252. No ordinary tool emits one: OpenSSL has no ristretto255 support at all, WireGuard's
`wg genkey` applies X25519 clamping that lands far above the order, and about fifteen of every
sixteen values `openssl rand 32` produces are simply out of range.

A seed has no such constraint. RFC 9497 defines `DeriveKeyPair` precisely to turn arbitrary
random bytes into a valid key, and that is what `Source: Seed` runs. So the standard tool is the
right tool - it just generates the seed rather than the key.

Seeds must be at least 32 bytes: the derived key is only as unguessable as the seed behind it,
and a short seed is enumerable no matter how sound the derivation.

## Rotation

**Read this first: a VOPRF key cannot be rotated the way a TLS certificate can.** A pseudonym is
`H(identifier, k·HashToGroup(identifier))`. Change `k` and every pseudonym changes, and a stored
pseudonym cannot be re-keyed — the only way to produce the new one is to evaluate the *original
identifier* again. Rotation is therefore a data migration, not a key swap, and it is only
possible for data whose identifiers you can still reach.

**One server holds exactly one key.** A generation is a deployment, not a row in a key table, and
the overlap window a migration needs is two deployments running side by side. Clients select a
generation by address.

```
                     ┌────────────────────────┐
  clients ──────────▶│ voprf-v1     KeyId: v1 │   the generation being migrated away from
                     └────────────────────────┘
                     ┌────────────────────────┐
  clients ──────────▶│ voprf-v2     KeyId: v2 │   the generation new pseudonyms belong to
                     └────────────────────────┘
                              one seed
```

Both derive from the **same seed**: `KeyId` is mixed into the derivation as RFC 9497's `info`
label, so a second generation is a different `KeyId` — not a second secret:

```jsonc
// voprf-v1                                    // voprf-v2
"Key": { "Source": "Seed",                     "Key": { "Source": "Seed",
         "FilePath": "/run/secrets/voprf.seed",          "FilePath": "/run/secrets/voprf.seed",
         "KeyId": "v1" }                                 "KeyId": "v2" }
```

### The procedure

| Step | What happens | What is true during it |
| --- | --- | --- |
| 1. Deploy | Stand up `voprf-v2` alongside `voprf-v1` | Nothing routes to v2 yet. Fetch its public key with `GetPublicKey` and pin it in the clients that will need it. |
| 2. Cut over | Point clients writing *new* pseudonyms at `voprf-v2` | New pseudonyms are v2. Anything still reading or writing v1 keeps its old endpoint and keeps working. Per-client, at whatever pace suits. |
| 3. Migrate | Re-pseudonymize the store from the original identifiers against `voprf-v2` | Both endpoints serve. Rows carry `key_id` so you can tell which are done. |
| 4. Retire | Delete the `voprf-v1` deployment | Anything still pointing at v1 fails to connect rather than silently getting a different pseudonym. |

Step 1 exists so no client is asked to trust a key it has not seen: it pins v2's public key while
v1 is still doing the work. Step 4 is deliberately a hard break — a retired generation that
quietly fell through to the current one would mint pseudonyms matching nothing.

### Why by address rather than a key id in the request

The alternative is one server holding every generation and a `key_id` on each call. It was built
that way first and then taken out, because separate deployments are better on the things that
matter here:

- **Blast radius.** One process, one key. A memory disclosure leaks one generation rather than
  every live one — which is the point of all the pinning and zeroing in `Vfps.Voprf`.
- **Authorization.** "This client may use v1 but not v2" is a NetworkPolicy or an mTLS trust
  boundary, rather than per-key authorization logic inside the application. Rate limits and audit
  logs separate per generation for free too.
- **Less to get wrong.** No key table, no id resolution, no fallback-to-active behaviour to
  reason about.

The cost is one deployment per live generation. That is fine for the one-active-plus-one-during-
rotation case this is built for. A deployment needing *many* concurrent keys — a key per tenant,
say — would be better served by putting the key table back in the server; at that point the
per-key authorization above becomes something the application has to implement.

### Store the key id with the pseudonym

`key_id` comes back on every answer for one reason: a pseudonym is meaningless without knowing
which key produced it. A store that does not record it cannot be migrated, because there is no
way to tell which rows are done.

`Vfps.Voprf.Client` does this by default by prefixing each pseudonym with `<keyId>.`, so every
stored value is self-describing without a column of its own. A key id containing `.` would make
those two halves ambiguous, so this server refuses to start with one.

`KeyId` is both the name and, for a seed, the selector - so a new key and a new name arrive
together and a stored row can never claim a generation that no longer means anything. RFC 9497
takes the two as independent values and `VoprfKeyPair.Derive` still does; this server's
configuration deliberately does not expose them separately.

### What rotation does not fix

If a key leaked, every pseudonym ever issued under it stays re-identifiable by whoever holds it,
for any identifier they can guess. Rotating protects future pseudonyms only. The old ones are
compromised the moment the key is, and the remedy is re-pseudonymizing the store and destroying
the old values — the same migration, run urgently.

### Separating domains

The same mechanism keeps data that should not link from linking: give each domain its own
`KeyId` and deployment. Pseudonyms from different keys are unrelatable, so the same
person in two systems keyed separately cannot be matched across them.

### The other sources

| Source | Reads | Use when |
| --- | --- | --- |
| `Seed` | `Key:FilePath` or `Key:Base64`, selected by `Key:KeyId` | **the default** |
| `File` | `Key:FilePath` | importing a key that already exists, e.g. from another RFC 9497 implementation |
| `Base64` | `Key:Base64` | the same, from an environment variable |
| `Ephemeral` | nothing | development only; refused while hardened |

Prefer a file to `Base64` either way: a mounted secret does not appear in the process
environment or in `kubectl describe`, and a base64 secret arrives as a `string`, which is
immutable and so cannot be erased from memory afterwards.

A key that cannot be read, or is not a valid scalar, fails the process at startup rather than on
the first call, so a broken deployment never reports healthy.

## Clients must pin the public key

`GetPublicKey` exists to bootstrap, not to be trusted. A client that verifies against whatever
public key the server just handed it has verified only that the server can do arithmetic
consistently - a server answering under a substituted key would hand over the matching public
key and the proof would check out.

Pin the key in the client's own configuration, or fetch it once through a channel the server
does not control and compare it across clients. The server logs its public key at startup so an
operator can confirm the two match.

## Container

[`voprf-server.Dockerfile`](../../voprf-server.Dockerfile) publishes this server ahead-of-time
(`PublishAot`, see the csproj) into a single native executable, and runs it non-root on
`runtime-deps`, the chiseled image that is just the OS and libc/libssl - no CLR, no ASP.NET Core
runtime, none of the managed dependencies a framework-dependent deployment would need, because
none of them exist at runtime here.

## What is deliberately absent

- **No persistence.** The service is stateless beyond the key and scales by running more copies
  against the same one.
- **No method taking an identifier**, for the reason at the top of this file.
- **No batching across callers.** A proof covers one caller's batch; mixing callers into one
  proof would tell each of them how many others were in it.
- **No request logging of elements.** Blinded elements are unlinkable by construction, and
  writing them down is the one way to produce a record that ties requests together. The audit
  log records who asked, when, how many, and under which key.
