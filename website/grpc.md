# gRPC API

vfps's API is defined in Protocol Buffers, and every operation is served over gRPC as well as the
JSON-transcoded [REST API](getting-started.md#using-the-rest-api). The definitions are in
[`src/Vfps/Protos/vfps/api/v1`](https://github.com/miracum/vfps/tree/master/src/Vfps/Protos/vfps/api/v1):

| Service                        | RPCs                                                |
| ------------------------------ | --------------------------------------------------- |
| `vfps.api.v1.NamespaceService` | `Create`, `Get`, `GetAll`, `Delete`, `ListChildren` |
| `vfps.api.v1.PseudonymService` | `Create`, `Resolve`, `Get`, `List`                  |

Plaintext gRPC is served on port **8081**, which accepts HTTP/2 only - the `HttpGrpc` endpoint in
`Kestrel__Endpoints`, and the `grpc` port of the Helm chart's Service. Port 8080 serves the REST API
and the admin UI.

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
docker run --rm --network=host ghcr.io/miracum/vfps/grpc-utils:v1.22.3 \
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
  --image=ghcr.io/miracum/vfps/grpc-utils:v1.22.3 \
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
