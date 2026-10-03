# Getting started

## Run it

!!! warning

    This stack is for trying vfps out, not for keeping data: it uses a well-known database
    password, stores everything in an anonymous volume, and has authentication turned off. To run
    vfps for real, see [Docker Compose](deployment/compose.md) for a single host or
    [Production deployment](deployment/production.md) for Kubernetes.

With [Docker Compose](https://docs.docker.com/compose/install/) 2.34 or later, start vfps and a
PostgreSQL database straight from the container registry. There's nothing to clone:

<!-- x-release-please-start-version -->

```sh
docker compose -f oci://ghcr.io/miracum/vfps/compose/getting-started:v1.22.1 up
```

<!-- x-release-please-end -->

Compose first lists the variables the stack reads and asks before it starts anything. Pass
`--yes` to `up` to skip the question.

Visit <http://localhost:8080/swagger/> to view the OpenAPI specification of the Vfps API, and
<http://localhost:8080/ui> for the [admin UI](admin-ui.md). Both ports are only bound to
`127.0.0.1`.

To stop the stack and delete everything it stored:

```sh
docker compose -p vfps-getting-started down --volumes
```

## Using the REST API

Every gRPC operation is also available as a JSON-transcoded REST endpoint. To create a new
namespace and a pseudonym inside it:

```sh
curl -X POST http://localhost:8080/v1/namespaces \
  -H "Content-Type: application/json" \
  -d '{"name": "test", "pseudonymGenerationMethod": "PSEUDONYM_GENERATION_METHOD_SECURE_RANDOM_BASE64URL_ENCODED", "pseudonymLength": 32}'

curl -X POST http://localhost:8080/v1/namespaces/test/pseudonyms \
  -H "Content-Type: application/json" \
  -d '{"originalValue": "to be pseudonymized"}'
```

## Using gRPC

You can use the JSON-transcoded REST API described via OpenAPI or interact with the service using gRPC.
For example, using [grpcurl](https://github.com/fullstorydev/grpcurl) from a checkout of the
repository to create a new namespace:

```sh
grpcurl \
  -plaintext \
  -import-path src/Vfps/ \
  -proto src/Vfps/Protos/vfps/api/v1/namespaces.proto \
  -d '{"name": "test", "pseudonymGenerationMethod": "PSEUDONYM_GENERATION_METHOD_SECURE_RANDOM_BASE64URL_ENCODED", "pseudonymLength": 32}' \
  127.0.0.1:8081 \
  vfps.api.v1.NamespaceService/Create
```

And to create a new pseudonym inside this namespace:

```sh
grpcurl \
  -plaintext \
  -import-path src/Vfps/ \
  -proto src/Vfps/Protos/vfps/api/v1/pseudonyms.proto \
  -d '{"namespace": "test", "originalValue": "to be pseudonymized"}' \
  127.0.0.1:8081 \
  vfps.api.v1.PseudonymService/Create
```

The service definitions are in
[`src/Vfps/Protos/vfps/api/v1`](https://github.com/miracum/vfps/tree/master/src/Vfps/Protos/vfps/api/v1).

## Next steps

- Choose a pseudonym format and structure your data in [namespaces](namespaces.md).
- Turn on [access control](access-control.md) before exposing the service to anyone.
- Deploy it to Kubernetes with the Helm chart: [Production deployment](deployment/production.md).
