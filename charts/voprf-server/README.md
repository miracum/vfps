# voprf-server

![Type: application](https://img.shields.io/badge/Type-application-informational?style=flat-square)

The key-holding half of vfps's verifiable oblivious pseudonymization (RFC 9497).

**Homepage:** <https://github.com/miracum/vfps>

The key-holding half of [RFC 9497](https://www.rfc-editor.org/rfc/rfc9497.html) VOPRF
pseudonymization. It holds the key and **never sees an identifier**; clients blind their values,
this service evaluates them and proves it used the key it published, and the client unblinds the
result. It therefore does not, and cannot, return pseudonyms.

Deploy it alongside [`vfps`](../vfps) and point that at it — see
[`src/Vfps.Voprf.Server/README.md`](../../src/Vfps.Voprf.Server/README.md) for the server itself
and [`src/Vfps.Voprf.Client/README.md`](../../src/Vfps.Voprf.Client/README.md) for the client.

## Installation

```sh
# 1. a seed - any 32 random bytes; the key is derived from it (RFC 9497 DeriveKeyPair)
openssl rand -out voprf.seed 32
kubectl create secret generic voprf-key --from-file=seed=voprf.seed -n vfps

# 2. a server certificate, e.g. from cert-manager, with the usual tls.crt / tls.key

helm install voprf-server oci://ghcr.io/miracum/vfps/charts/voprf-server -n vfps \
  --set key.existingSecret=voprf-key \
  --set tls.existingSecret=voprf-tls
```

`openssl rand 32` is **not** a key: a private key is a reduced ristretto255 scalar, and roughly
fifteen of every sixteen random 32-byte values are out of range. It is a perfectly good *seed*,
which is why `key.source` defaults to `Seed`.

## Pin the public key

Clients must pin it out of band. A server evaluating under a substituted key would report the
matching public key and every proof would still verify — so a client that trusts whatever the
server offers has verified only that the server can do arithmetic consistently.

```sh
kubectl logs -n vfps deploy/voprf-server | grep 'public key'
```

That value goes into vfps's `Voprf:PublicKey`.

## Hardening

Everything is on by default, and the chart refuses to render a configuration that would be
unsafe or would not start — with a message naming the value to set, rather than a
`CrashLoopBackOff` and a container log.

| Default | |
| --- | --- |
| TLS required | `hardening.requireTls` |
| Callers authenticated (mTLS or OIDC) | `hardening.requireAuthentication` |
| Per-caller rate limit, 600/min | `hardening.rateLimit` |
| gRPC reflection off, no error detail | `hardening.enableReflection`, `hardening.enableDetailedErrors` |
| Default-deny NetworkPolicy | `networkPolicy` |
| Non-root, read-only rootfs, all capabilities dropped, `RuntimeDefault` seccomp | `securityContext` |
| No service account token mounted | `serviceAccount.automountServiceAccountToken` |
| Ephemeral keys refused | `key.source` |

`hardening.enabled: false` relaxes all of it at once, and the **server refuses to start that way
unless `environment: Development`** — so a development configuration cannot reach a cluster by
being forgotten.

### Running without TLS

`hardening.requireTls: false` serves gRPC over plaintext while **everything else stays
enforced** - authentication, the rate limit, the batch caps, no reflection, no ephemeral keys.
It is for a deployment where a mesh or ingress terminates TLS in front of the pod and the hop
from there is itself trusted.

It cannot be combined with `auth.mode: ClientCertificate`: a client certificate is presented
during the TLS handshake, so over plaintext no caller could ever authenticate and every request
would be refused with 403 - a symptom that reads exactly like an ordinary credentials problem.
The chart and the server both refuse that pairing outright. Use `auth.mode: Jwt`, which a mesh
can carry, or leave TLS on.

### Mutual TLS with cert-manager

Give the pair their own CA. It is then the trust boundary, and because it issues for nothing
else, "chains to this CA" is as narrow as naming individual certificates - while surviving
renewal, which naming them does not.

```yaml
apiVersion: cert-manager.io/v1
kind: Issuer
metadata: { name: voprf-selfsign, namespace: vfps }
spec: { selfSigned: {} }
---
apiVersion: cert-manager.io/v1
kind: Certificate            # the CA itself
metadata: { name: voprf-ca, namespace: vfps }
spec:
  isCA: true
  commonName: voprf-ca
  secretName: voprf-ca
  privateKey: { algorithm: ECDSA, size: 256 }
  issuerRef: { name: voprf-selfsign, kind: Issuer }
---
apiVersion: cert-manager.io/v1
kind: Issuer                 # everything below is signed by it
metadata: { name: voprf-ca, namespace: vfps }
spec:
  ca: { secretName: voprf-ca }
---
apiVersion: cert-manager.io/v1
kind: Certificate            # what the server presents
metadata: { name: voprf-server-tls, namespace: vfps }
spec:
  secretName: voprf-server-tls
  issuerRef: { name: voprf-ca, kind: Issuer }
  usages: [server auth]
  dnsNames:
    - voprf-server
    - voprf-server.vfps.svc
    - voprf-server.vfps.svc.cluster.local
---
apiVersion: cert-manager.io/v1
kind: Certificate            # what vfps presents
metadata: { name: voprf-client-tls, namespace: vfps }
spec:
  secretName: voprf-client-tls
  issuerRef: { name: voprf-ca, kind: Issuer }
  usages: [client auth]
  commonName: vfps
```

Then point the chart at the server certificate, and at the CA so client certificates can be
validated against it:

```yaml
tls:
  existingSecret: voprf-server-tls

auth:
  mode: ClientCertificate
  clientCertificate:
    allowedThumbprints: []   # the CA is the boundary - see below

# The container trusts the public roots; a private CA has to be added. .NET on Linux honours
# SSL_CERT_FILE for building the client certificate's chain.
extraVolumes:
  - name: client-ca
    secret:
      secretName: voprf-server-tls
      items: [{ key: ca.crt, path: ca.crt }]
extraVolumeMounts:
  - name: client-ca
    mountPath: /secrets/client-ca
    readOnly: true
extraEnv:
  - name: SSL_CERT_FILE
    value: /secrets/client-ca/ca.crt
```

`SSL_CERT_FILE` *replaces* the trust store rather than adding to it. That is fine here because
the server dials nothing outbound - but if you later switch to `auth.mode: Jwt`, its OIDC
discovery call needs the public roots too, so supply a bundle containing both.

#### Do not pin thumbprints against rotating certificates

`allowedThumbprints` narrows beyond the CA, and it does work - but a renewed certificate has the
same issuer and the same subject with a **new thumbprint**, so the first cert-manager renewal
locks every caller out. With a CA issued for this service alone you gain nothing by pinning:
leave the list empty and let the CA be the boundary. Keep thumbprints for long-lived certificates
you rotate by hand.

#### The client side

vfps presents its certificate through the gRPC client's handler:

```csharp
builder.Services.AddVoprfPseudonymizer(builder.Configuration);

builder.Services
    .AddGrpcClient<Vfps.Voprf.Protos.VoprfService.VoprfServiceClient>()
    .ConfigurePrimaryHttpMessageHandler(() =>
    {
        var handler = new HttpClientHandler();
        handler.ClientCertificates.Add(
            X509CertificateLoader.LoadPkcs12FromFile("/secrets/voprf-client/client.pfx", null));
        return handler;
    });
```

cert-manager writes `tls.crt` and `tls.key`; .NET wants one PKCS#12. Either add
`keystores: { pkcs12: { create: true, passwordSecretRef: ... } }` to the client `Certificate` and
mount the resulting `keystore.p12`, or combine the two files at startup.

### Reaching the service is the security boundary

Evaluation reveals nothing about any input, so the service leaks nothing by answering. What it
does is answer *with the key*: anyone who can call it can pseudonymize anything they can guess,
and where the input space is enumerable — national IDs, dates of birth — that is the whole of the
confidentiality the pseudonyms have.

Hence the default-deny NetworkPolicy. Callers opt in by carrying the label:

```yaml
metadata:
  labels:
    voprf-server-client: "true"
```

### The plaintext health port

Kubernetes' own gRPC probes cannot speak TLS, so a hardened deployment would otherwise have no
way to be probed. The chart runs a second, HTTP/1.1, plaintext listener on
`service.healthPort` for exactly that.

Nothing can be evaluated through it, for two independent reasons: the server's TLS check exempts
only the health endpoints and answers everything else on a plaintext connection with `421`, and
the listener speaks HTTP/1.1, which gRPC cannot use at all.

## Rotation

A VOPRF key cannot be rotated like a certificate: a pseudonym is a function of the key, so
changing it changes every pseudonym, and a stored one cannot be re-keyed without the original
value. Rotation is a data migration, and it needs two generations serving at once — which here
means **two releases of this chart**, since a server holds exactly one key.

Both can derive from the same seed: `key.keyId` names the generation *and* selects which key is
derived from that seed, so a second generation is one setting.

```sh
helm install voprf-server-v2 oci://ghcr.io/miracum/vfps/charts/voprf-server -n vfps \
  --set key.existingSecret=voprf-key --set key.keyId=v2 \
  --set tls.existingSecret=voprf-tls
```

Then move clients over, re-pseudonymize, and delete the old release. vfps carries `keyId` in each
pseudonym as `v1.<pseudonym>`, so you can tell which rows still belong to the old generation.

RFC 9497 actually takes two values here - a label mixed into the key derivation, and the id
reported alongside answers - and the server exposes both. The chart deliberately does not. Their
only interesting combination is a new key under an old id, which leaves every stored row claiming
a generation that no longer means anything; one setting makes that unreachable.

## Maintainers

| Name | Email | Url |
| ---- | ------ | --- |
| miracum |  |  |

## Requirements

Kubernetes: `>= 1.21.0`

## Values

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| affinity | object | `{}` |  |
| auth.clientCertificate.allowedThumbprints | list | `[]` | SHA-256 thumbprints allowed to call, hex encoded. Empty accepts any certificate chaining to a trusted root, which is only as narrow as that root.  Leave it empty when the root is a CA issued for this service alone - a dedicated cert-manager CA issuer, say. **A thumbprint list does not survive certificate rotation**: a renewed certificate has the same issuer and subject but a new thumbprint, so cert-manager renewing behind your back would lock every caller out. Pin thumbprints only against long-lived, manually managed certificates. |
| auth.clientCertificate.checkRevocation | bool | `false` | check revocation online. Off by default: an unreachable CRL or OCSP responder would otherwise take this service down with it. |
| auth.jwt.audience | string | `""` | audience the token must carry. Leaving it empty disables audience validation, which makes every token the issuer hands out a licence to use this key. |
| auth.jwt.authority | string | `""` | OIDC issuer, e.g. a Keycloak realm URL |
| auth.jwt.requiredScope | string | `""` | scope a token must carry to call BlindEvaluate |
| auth.mode | string | `"ClientCertificate"` | `ClientCertificate` (mutual TLS) or `Jwt`.  Mutual TLS suits a service called by other services: no identity provider to depend on, and the caller is identified before the first byte of the body. It needs the issuing CA in the container's trust store - mount it and point `SSL_CERT_FILE` at it via `extraEnv`. |
| autoscaling | object | `{"enabled":false,"maxReplicas":10,"minReplicas":2,"targetCPUUtilizationPercentage":80}` | kept modest on purpose. An evaluation is one scalar multiplication plus a proof, so this saturates CPU long before memory; a CPU limit would throttle bursts of exactly the work the service exists to do. |
| environment | string | `"Production"` | ASPNETCORE_ENVIRONMENT. Anything but `Development` refuses to start with `hardening.enabled=false`, which is what keeps a development configuration from reaching a cluster by being forgotten. |
| extraEnv | list | `[]` | extra environment variables, e.g. `SSL_CERT_FILE` pointing at a mounted CA bundle for mTLS |
| extraVolumeMounts | list | `[]` |  |
| extraVolumes | list | `[]` |  |
| fullnameOverride | string | `""` |  |
| hardening.enableDetailedErrors | bool | `false` | return exception detail to callers. Off: a failure distinguishing "not a valid element" from "not a canonical encoding" tells a caller probing the group more than it needs. |
| hardening.enableReflection | bool | `false` | serve gRPC server reflection. Off: it maps the service surface to anyone who can connect. |
| hardening.enabled | bool | `true` | master switch. `false` relaxes TLS, authentication, reflection and error detail all at once; the server refuses to start that way unless `environment` is Development. |
| hardening.maxBatchSize | int | `128` | most blinded elements accepted per call. Each costs a scalar multiplication. |
| hardening.maxReceiveMessageSizeBytes | int | `65536` | largest request accepted, in bytes |
| hardening.rateLimit.enabled | bool | `true` |  |
| hardening.rateLimit.permitsPerWindow | int | `600` | calls allowed per caller per window. Counts calls, not elements - batching is the behaviour worth encouraging, since it costs one proof instead of many. |
| hardening.rateLimit.queueLimit | int | `0` |  |
| hardening.rateLimit.window | string | `"0.00:01:00"` |  |
| hardening.requireAuthentication | bool | `true` | reject unauthenticated callers |
| hardening.requireTls | bool | `true` | reject requests that did not arrive over TLS. Leave on even behind a mesh unless the hop between it and this pod is itself trusted. |
| image.pullPolicy | string | `"IfNotPresent"` |  |
| image.registry | string | `"ghcr.io"` |  |
| image.repository | string | `"miracum/vfps/voprf-server"` |  |
| image.tag | string | `""` | overrides the image tag, which defaults to the chart's appVersion |
| imagePullSecrets | list | `[]` |  |
| key.existingSecret | string | `""` | name of an existing Secret holding the key material. Preferred over `value`: a mounted secret does not appear in the pod's environment or in `kubectl describe`. |
| key.existingSecretKey | string | `"seed"` | key within `existingSecret` holding the seed (or the private key, for source File/Base64) |
| key.keyId | string | `"v1"` | names this key generation, and selects it.  One setting doing both jobs on purpose. It is mixed into RFC 9497's DeriveKeyPair, so against a given seed it picks which key is derived; and it is returned with every answer and carried in each pseudonym as `<keyId>.<pseudonym>`, so a store can tell which key produced which row.  Tying them together is what makes a rotation one edit and makes it impossible for a stored row to claim a generation that no longer means anything. (The server underneath exposes the derivation label and the id separately; the chart deliberately does not, because a new key wearing an old label leaves a store that cannot be migrated.)  Must not contain `.` - that is the separator between the id and the pseudonym. |
| key.source | string | `"Seed"` | `Seed` (recommended), `File`, `Base64`, or `Ephemeral`.  A private key is a reduced ristretto255 scalar, which no ordinary tool emits - roughly fifteen of every sixteen values `openssl rand 32` produces are out of range. A *seed* has no such constraint, and RFC 9497's DeriveKeyPair turns it into a key. So generate a seed:      openssl rand -out voprf.seed 32     kubectl create secret generic voprf-key --from-file=seed=voprf.seed |
| key.value | string | empty | the seed inline, base64 encoded. Convenient for a quick trial; prefer `existingSecret`. |
| nameOverride | string | `""` |  |
| networkPolicy.allowExternal | bool | `false` | allow ingress from anywhere. Off by default: reaching this service at all is the security boundary, because anyone who can call it can pseudonymize anything they can guess. |
| networkPolicy.allowExternalEgress | bool | `false` | allow all egress. Off by default; the server calls nothing outbound except an OIDC authority when `auth.mode: Jwt`. |
| networkPolicy.enabled | bool | `true` |  |
| networkPolicy.extraEgress | list | `[]` |  |
| networkPolicy.extraIngress | list | `[]` |  |
| networkPolicy.ingressNSMatchLabels | object | `{}` | pods in these namespaces may call the service |
| networkPolicy.ingressNSPodMatchLabels | object | `{}` |  |
| nodeSelector | object | `{}` |  |
| podAnnotations | object | `{}` |  |
| podDisruptionBudget.enabled | bool | `true` |  |
| podDisruptionBudget.minAvailable | int | `1` |  |
| podLabels | object | `{}` |  |
| podSecurityContext.fsGroup | int | `65534` |  |
| podSecurityContext.runAsGroup | int | `65534` |  |
| podSecurityContext.runAsNonRoot | bool | `true` |  |
| podSecurityContext.runAsUser | int | `65534` |  |
| podSecurityContext.seccompProfile.type | string | `"RuntimeDefault"` |  |
| replicaCount | int | `2` | number of replicas. The server is stateless beyond its key, so scaling it is horizontal and needs no coordination - every replica must be given the same key. |
| resources.limits.memory | string | `"256Mi"` |  |
| resources.requests.cpu | string | `"100m"` |  |
| resources.requests.memory | string | `"96Mi"` |  |
| securityContext.allowPrivilegeEscalation | bool | `false` |  |
| securityContext.capabilities.drop[0] | string | `"ALL"` |  |
| securityContext.privileged | bool | `false` |  |
| securityContext.readOnlyRootFilesystem | bool | `true` | nothing is ever written to disk: the key is read once at startup and the process is stateless afterwards |
| securityContext.runAsGroup | int | `65534` |  |
| securityContext.runAsNonRoot | bool | `true` |  |
| securityContext.runAsUser | int | `65534` |  |
| securityContext.seccompProfile.type | string | `"RuntimeDefault"` |  |
| service.annotations | object | `{}` |  |
| service.grpcPort | int | `8081` | the gRPC port. TLS terminates here when `tls.existingSecret` is set. |
| service.healthPort | int | `8080` | plaintext port serving only the health checks, for kubelet probes.  Kubernetes' own gRPC probes cannot speak TLS, so a hardened deployment has no way to probe the real port. This listener exists for that: the server's TLS check exempts the health endpoints and rejects everything else on a plaintext connection with 421, so no evaluation can be performed through it. |
| service.type | string | `"ClusterIP"` |  |
| serviceAccount.annotations | object | `{}` |  |
| serviceAccount.automountServiceAccountToken | bool | `false` | the server never calls the Kubernetes API |
| serviceAccount.create | bool | `true` |  |
| serviceAccount.name | string | `""` |  |
| tls.certFileName | string | `"tls.crt"` |  |
| tls.existingSecret | string | `""` | name of a Secret with `tls.crt` and `tls.key` (cert-manager's layout). Required unless `hardening.requireTls` is false. |
| tls.keyFileName | string | `"tls.key"` |  |
| tolerations | list | `[]` |  |
| topologySpreadConstraints | list | `[]` | spread replicas across nodes so a single node taking the whole service out is not the default posture |
