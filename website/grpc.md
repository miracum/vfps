# gRPC API

vfps's API is defined in Protocol Buffers, and every operation is served over gRPC as well as the
JSON-transcoded REST API. The definitions are in
[`src/Vfps/Protos/vfps/api/v1`](https://github.com/miracum/vfps/tree/master/src/Vfps/Protos/vfps/api/v1).

The [API reference](api-reference.md) lists every RPC, REST route, message and enum, generated from
these files.

## Getting the `.proto` files

Every [release](https://github.com/miracum/vfps/releases) has the `.proto` files attached as
`vfps-protos-<tag>.tar.gz`, together with a `.sha256` checksum and a build provenance attestation
(`gh attestation verify <file> --repo miracum/vfps`). The archive is rooted at the include path,
so one `-I` resolves every import, and it contains the `google/api` files the REST annotations
need. Use it to generate a client for your language, e.g. with `protoc` or `buf generate`.

## API conventions

The API follows Google's [API Improvement Proposals](https://google.aip.dev/) where they apply:
`List` and `Get` for reads, `Create`, `Update` and `Delete` for the standard writes, and
[custom methods](https://google.aip.dev/136) with a `:verb` suffix (`:append`, `:import`,
`:resolve`) for the operations on pseudonyms that don't fit those. No operation takes an original
value in a URL: one that needs an original value takes it in the request body, which is why
`Resolve` is a `POST` even though it only reads - a URL ends up in access logs, proxies and browser
histories.

`PseudonymService.Get` is the **reverse lookup**: it reveals the original value behind a pseudonym.
It is the one operation that de-pseudonymizes, so it needs its own reverse-lookup permission -
neither read nor write access grants it.

### Retrying safely

Whether a call can be retried after a timeout or a dropped connection - when the caller can't tell
whether it went through - depends on the operation:

| RPC                                              | REST                                                     | Safe to retry                    | When it already exists                                  |
| ------------------------------------------------ | -------------------------------------------------------- | -------------------------------- | ------------------------------------------------------- |
| `NamespaceService.Create`, `ProjectService.Create` | `POST /v1/namespaces`, `POST /v1/projects`               | No                               | `ALREADY_EXISTS`                                        |
| `NamespaceService.Update`, `ProjectService.Update` | `PATCH /v1/namespaces/{name}`, `PATCH /v1/projects/{name}` | Yes                              | -                                                       |
| `NamespaceService.Delete`, `ProjectService.Delete` | `DELETE /v1/namespaces/{name}`, `DELETE /v1/projects/{name}` | Yes - a repeat is `NOT_FOUND`    | -                                                       |
| `ProjectService.AddNamespace`, `RemoveNamespace` | `PUT`, `DELETE /v1/projects/{name}/namespaces/{namespace}` | Yes                              | Succeeds without changes                                |
| `PseudonymService.Create`                        | `POST /v1/namespaces/{namespace}/pseudonyms`             | Yes - get-or-create              | Returns the stored pseudonym(s)                         |
| `PseudonymService.Append`                        | `POST …/pseudonyms:append`                               | **Only with a `requestId`**      | Without one, stores another pseudonym on every call     |
| `PseudonymService.Import`                        | `POST …/pseudonyms:import`                               | Yes, for the same pair           | `ALREADY_EXISTS` if the pair conflicts with what's stored |
| `PseudonymService.Resolve`                       | `POST …/pseudonyms:resolve`                              | Yes (read-only)                  | `NOT_FOUND` if none is stored                           |
| `Get`, `List`, `ListChildren`                    | `GET`                                                    | Yes (read-only)                  | -                                                       |

Pseudonym writes are keyed by the original value, and are idempotent - writing
the same thing twice returns what the first call stored. `Append` is the one exception, since
storing another pseudonym is its whole point. It follows
[AIP-155](https://google.aip.dev/155) instead: give each append a `requestId`, a UUID generated once
for that append and sent again only when retrying it. A call repeating the `requestId` of one that
already succeeded in the namespace stores nothing and returns the pseudonym that call stored.
Reusing a `requestId` for a different original value, or sending one that isn't a UUID, is
`INVALID_ARGUMENT`. See [Appending a pseudonym](namespaces.md#appending-a-pseudonym) for an example.

### Updating a namespace or project

`Update` is a partial update following [AIP-134](https://google.aip.dev/134): the request carries
the resource with the new values, and `updateMask` names the fields to change. A field named in the
mask but absent from the request is cleared; one not named is kept. Without a mask, every updatable
field that is set in the request is changed, and `*` replaces every updatable field. Naming a field
that can't change is `INVALID_ARGUMENT`.

Only a namespace's `description` and `originalValueValidationRegex`, and a project's `description`,
can change. A project's namespaces change through `AddNamespace` and `RemoveNamespace`. Over REST
the mask is a query parameter:

```sh
curl -X PATCH 'http://127.0.0.1:8080/v1/namespaces/test?updateMask=description' \
  -H 'content-type: application/json' \
  -d '{"description": "Patient IDs of study A"}'
```

## Server reflection

The server offers gRPC server reflection, so clients such as
[grpcurl](https://github.com/fullstorydev/grpcurl), [ghz](https://ghz.sh/), grpcui or Postman need
no copy of the `.proto` files - and always see the API the server actually runs. To explore it:

```sh
grpcurl -plaintext 127.0.0.1:8081 list
grpcurl -plaintext 127.0.0.1:8081 describe vfps.api.v1.PseudonymService
grpcurl -plaintext 127.0.0.1:8081 describe vfps.api.v1.PseudonymServiceCreateRequest
```

And to call it:

```sh
grpcurl -plaintext \
  -d '{"namespace": "test", "originalValue": "to be pseudonymized"}' \
  127.0.0.1:8081 \
  vfps.api.v1.PseudonymService/Create
```

## Authenticating

With [access control](access-control.md) turned on, every call needs a bearer token - from the
identity provider, or a [vfps access token](access-control.md#access-tokens) created on the admin
UI's _Access tokens_ page, which also shows these calls ready to copy. Reflection is part of the API
and takes the same token, so without one even `list` fails. grpcurl's `-H` sends the header with
reflection and the call alike:

```sh
export VFPS_TOKEN="vfps_pat_..."
export VFPS_NAMESPACE="..."

grpcurl -plaintext \
  -H "authorization: Bearer $VFPS_TOKEN" \
  -d "{\"namespace\": \"$VFPS_NAMESPACE\", \"originalValue\": \"to be pseudonymized\"}" \
  127.0.0.1:8081 \
  vfps.api.v1.PseudonymService/Create
```

A call without a valid token fails with `Unauthenticated`; one with a token that holds no grant on
the namespace, with `PermissionDenied`.

## The gRPC utils image

`ghcr.io/miracum/vfps/grpc-utils` is a small image for talking to vfps where nothing is installed,
such as from inside a cluster. It contains:

- [grpcurl](https://github.com/fullstorydev/grpcurl), for making individual calls
- [ghz](https://ghz.sh/), for load testing
- `curl` and `jq`
- vfps's `.proto` files, under `/tmp/protos` - for a tool that can't use reflection, pass
  `-import-path /tmp/protos -proto vfps/api/v1/pseudonyms.proto`

Every vfps release has a matching image tag, and `latest` follows the latest release and `master`
the default branch. The image runs as `nobody` (UID 65534). Releases up to v1.22.2 published it as
`ghcr.io/miracum/vfps-grpc-utils`.

### From Docker

With vfps running locally - for example the [getting started](getting-started.md) stack, which
binds port 8081 on `127.0.0.1` - share the host's network so `localhost` reaches it:

<!-- x-release-please-start-version -->

```sh
docker run --rm --network=host ghcr.io/miracum/vfps/grpc-utils:v1.26.0 \
  grpcurl -plaintext \
  -H "authorization: Bearer $VFPS_TOKEN" \
  -d "{\"namespace\": \"$VFPS_NAMESPACE\", \"originalValue\": \"to be pseudonymized\"}" \
  localhost:8081 \
  vfps.api.v1.PseudonymService/Create
```

<!-- x-release-please-end -->

Your own shell expands `$VFPS_TOKEN` and `$VFPS_NAMESPACE` before Docker starts, so neither needs
passing into the container - leave out the `-H` line when access control is off. `--network=host`
is what lets the container reach a port the host only binds on `127.0.0.1`; Docker Desktop supports
it only once host networking is enabled in its settings.

### Inside Kubernetes

The plaintext gRPC port is usually reachable only from inside the cluster. Start a throwaway pod
from the image in vfps's namespace and call the chart's Service, which is named after the release:

<!-- x-release-please-start-version -->

```sh
kubectl run --namespace=vfps --rm -i --tty --restart=Never \
  --image=ghcr.io/miracum/vfps/grpc-utils:v1.26.0 \
  vfps-grpc-utils -- bash

nobody@vfps-grpc-utils:/$ grpcurl -plaintext vfps:8081 list
```

<!-- x-release-please-end -->

If the chart's NetworkPolicy is enabled with `networkPolicy.allowExternal: false`, only pods
labelled `<release>-client: "true"` may reach vfps - add `--labels=vfps-client=true` to
`kubectl run`.

## Load testing

ghz uses reflection too. For example, from the pod above, a minute of pseudonym creation against the
chart's headless Service, which lets ghz spread the load across every replica:

```sh
ghz --duration=1m \
  --connections=3 \
  --lb-strategy=round_robin \
  --insecure \
  --call=vfps.api.v1.PseudonymService/Create \
  -d '{"originalValue": "{{ randomString 32 }}", "namespace": "test"}' \
  dns:///vfps-headless:8081
```

Unlike grpcurl, ghz keeps the metadata for its calls and for reflection apart - with access control
on, pass the token to both:

```sh
--metadata="{\"authorization\": \"Bearer $VFPS_TOKEN\"}" \
--reflect-metadata="{\"authorization\": \"Bearer $VFPS_TOKEN\"}"
```

See [Performance](benchmarks.md) for measured numbers.
