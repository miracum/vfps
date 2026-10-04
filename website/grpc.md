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

The server doesn't offer gRPC reflection outside the `Development` environment, so a client needs
the `.proto` files. They're in any checkout of the repository, and in the gRPC utils image below.

## The gRPC utils image

`ghcr.io/miracum/vfps-grpc-utils` is a small image for talking to vfps from the command line, or from
inside a cluster where nothing else is installed. It contains:

- [grpcurl](https://github.com/fullstorydev/grpcurl), for making individual calls
- [ghz](https://ghz.sh/), for load testing
- `curl` and `jq`
- vfps's `.proto` files, under `/tmp/protos/Protos`

Every vfps release has a matching image tag, whose `.proto` files are that release's - use the one
for the version you run. `latest` follows the latest release and `master` the default branch. The
image runs as `nobody` (UID 65534).

### From Docker

With vfps running locally - for example the [getting started](getting-started.md) stack, which
binds port 8081 on `127.0.0.1` - share the host's network so `127.0.0.1` reaches it, and point
grpcurl at the image's copy of the `.proto` files:

<!-- x-release-please-start-version -->

```sh
docker run --rm --network=host ghcr.io/miracum/vfps-grpc-utils:v1.22.2 \
  grpcurl -plaintext \
  -import-path /tmp/protos/ \
  -proto Protos/vfps/api/v1/pseudonyms.proto \
  -d '{"namespace": "test", "originalValue": "to be pseudonymized"}' \
  127.0.0.1:8081 \
  vfps.api.v1.PseudonymService/Create
```

<!-- x-release-please-end -->

`--network=host` is what lets the container reach a port the host only binds on `127.0.0.1`. Docker
Desktop supports it only once host networking is enabled in its settings.

### Inside Kubernetes

The plaintext gRPC port is usually reachable only from inside the cluster. Start a throwaway pod
from the image in vfps's namespace and call the chart's Service, which is named after the release:

<!-- x-release-please-start-version -->

```sh
kubectl run --namespace=vfps --rm -i --tty --restart=Never \
  --image=ghcr.io/miracum/vfps-grpc-utils:v1.22.2 \
  vfps-grpc-utils -- bash

nobody@vfps-grpc-utils:/$ grpcurl -plaintext \
  -import-path /tmp/protos/ \
  -proto Protos/vfps/api/v1/namespaces.proto \
  vfps:8081 \
  vfps.api.v1.NamespaceService/GetAll
```

<!-- x-release-please-end -->

If the chart's NetworkPolicy is enabled with `networkPolicy.allowExternal: false`, only pods
labelled `<release>-client: "true"` may reach vfps - add `--labels=vfps-client=true` to
`kubectl run`.

## Authenticating

With [access control](access-control.md) turned on, every call needs a bearer token - from the
identity provider, or a [vfps access token](access-control.md#access-tokens) created on the admin
UI's _Access tokens_ page, which also shows these calls ready to copy. Pass it as gRPC metadata:

<!-- x-release-please-start-version -->

```sh
export VFPS_TOKEN="vfps_pat_..."
export VFPS_NAMESPACE="..."

docker run --rm --network=host ghcr.io/miracum/vfps-grpc-utils:v1.22.2 \
  grpcurl -plaintext \
  -H "authorization: Bearer $VFPS_TOKEN" \
  -import-path /tmp/protos/ \
  -proto Protos/vfps/api/v1/pseudonyms.proto \
  -d "{\"namespace\": \"$VFPS_NAMESPACE\", \"originalValue\": \"to be pseudonymized\"}" \
  127.0.0.1:8081 \
  vfps.api.v1.PseudonymService/Create
```

<!-- x-release-please-end -->

Your own shell expands `$VFPS_TOKEN` and `$VFPS_NAMESPACE` before Docker starts, so neither needs
passing into the container. A call without a valid token fails with `Unauthenticated`; one with a
token that holds no grant on the namespace, with `PermissionDenied`.

## Load testing

ghz takes the same `.proto` arguments. For example, from the pod above, a minute of pseudonym
creation against the chart's headless Service, which lets ghz spread the load across every replica:

```sh
ghz --duration=1m \
  --connections=3 \
  --lb-strategy=round_robin \
  --insecure \
  --import-paths=/tmp/protos/ \
  --proto=Protos/vfps/api/v1/pseudonyms.proto \
  --call=vfps.api.v1.PseudonymService/Create \
  -d '{"originalValue": "{{ randomString 32 }}", "namespace": "test"}' \
  dns:///vfps-headless:8081
```

With access control on, pass the token with `--metadata='{"authorization": "Bearer ..."}'`.

See [Performance](benchmarks.md) for measured numbers.
