# Namespaces

A namespace is an independent mapping between original values and their pseudonyms. Each one
fixes, at creation, how its pseudonyms are generated: the generation method, the pseudonym length,
an optional prefix and suffix, and an optional regular expression every original value has to
match. Namespaces are immutable once created.

## Pseudonym generation methods

| Method                                                        | Pseudonyms                                                                                                                                                                                                                               |
| ------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `PSEUDONYM_GENERATION_METHOD_SECURE_RANDOM_BASE64URL_ENCODED` | The default. The first 4 bytes of the SHA-256-hashed host name, the 4 least significant bytes of the millisecond tick count since system start, and `pseudonymLength` cryptographically secure random bytes, encoded as URL-safe Base64. |
| `PSEUDONYM_GENERATION_METHOD_FULL_RANDOM_HEX_ENCODED`         | A cryptographically secure random string, hex-encoded.                                                                                                                                                                                   |
| `PSEUDONYM_GENERATION_METHOD_FULL_RANDOM_BASE62_ENCODED`      | A cryptographically secure random string, Base62-encoded.                                                                                                                                                                                |
| `PSEUDONYM_GENERATION_METHOD_FULL_RANDOM_BASE32_ENCODED`      | A cryptographically secure random string, Base32-encoded with the [RFC 4648 §6](https://datatracker.ietf.org/doc/html/rfc4648#section-6) alphabet.                                                                                       |
| `PSEUDONYM_GENERATION_METHOD_UUID4`                           | A version 4 UUID ([RFC 4122](https://datatracker.ietf.org/doc/html/rfc4122#section-4.4)).                                                                                                                                                |
| `PSEUDONYM_GENERATION_METHOD_UUID7`                           | A version 7 UUID ([RFC 9562](https://datatracker.ietf.org/doc/html/rfc9562)).                                                                                                                                                            |
| `PSEUDONYM_GENERATION_METHOD_VOPRF`                           | Derived deterministically from the original value by a separate key-holding service that never sees it. See [VOPRF pseudonyms](voprf.md).                                                                                                |

Every method except VOPRF is random: the same original value only maps to the same pseudonym
because vfps stores the pair. The full set of request fields is documented in
[`namespaces.proto`](https://github.com/miracum/vfps/blob/master/src/Vfps/Protos/vfps/api/v1/namespaces.proto).

## Multiple pseudonyms per original value

A namespace created with `"allowsMultiplePseudonyms": true` lets a single `Create` call store more
than one distinct pseudonym for the same original value, via the request's optional `count` field
(omitted or `1` preserves the default single-pseudonym behavior for every other namespace):

```sh
grpcurl \
  -plaintext \
  -import-path src/Vfps/ \
  -proto src/Vfps/Protos/vfps/api/v1/namespaces.proto \
  -d '{"name": "multi-psn-example", "pseudonymGenerationMethod": "PSEUDONYM_GENERATION_METHOD_FULL_RANDOM_HEX_ENCODED", "pseudonymLength": 32, "allowsMultiplePseudonyms": true}' \
  127.0.0.1:8081 \
  vfps.api.v1.NamespaceService/Create

grpcurl \
  -plaintext \
  -import-path src/Vfps/ \
  -proto src/Vfps/Protos/vfps/api/v1/pseudonyms.proto \
  -d '{"namespace": "multi-psn-example", "originalValue": "to be pseudonymized", "count": 3}' \
  127.0.0.1:8081 \
  vfps.api.v1.PseudonymService/Create
```

The response's `pseudonyms` array holds the full set (`pseudonym` is kept, populated with the
first one, for callers that only read a single value). The stored set for a given original value
only ever grows: calling `Create` again with a `count` at or below what's already stored returns
the existing set unchanged, while a larger `count` adds exactly the missing pseudonyms, up to a
maximum `count` of 10,000. CSV pseudonymization jobs and the FHIR `$create-pseudonym` operation
don't support requesting more than one pseudonym - both always operate on the first
(`sequenceNumber: 0`) pseudonym for a multi-psn namespace.

## Multi-level namespaces

Namespaces can form a hierarchy: a namespace created with a `parentName` is a _child_ namespace,
whose original values are pseudonym values produced by its parent. This is how multiple levels of
pseudonymization are built - e.g. an MPI is pseudonymized in a root namespace, and that pseudonym
is then re-pseudonymized in a per-study child namespace, so a study never sees a value that
resolves directly to the MPI.

Each namespace keeps its own generation configuration; nothing is inherited from the parent.
Setting `parentValidationMode` to `PARENT_VALIDATION_MODE_ENSURE_EXISTS` additionally requires
every original value to already exist as a pseudonym in the parent namespace, rejecting anything
else with a `FAILED_PRECONDITION` error:

```sh
grpcurl \
  -plaintext \
  -import-path src/Vfps/ \
  -proto src/Vfps/Protos/vfps/api/v1/namespaces.proto \
  -d '{"name": "study-a", "pseudonymGenerationMethod": "PSEUDONYM_GENERATION_METHOD_FULL_RANDOM_HEX_ENCODED", "pseudonymLength": 32, "parentName": "test", "parentValidationMode": "PARENT_VALIDATION_MODE_ENSURE_EXISTS"}' \
  127.0.0.1:8081 \
  vfps.api.v1.NamespaceService/Create
```

Chaining a pseudonym into the child namespace is an ordinary `Create` call whose `originalValue`
is the parent's pseudonym value. A namespace's direct children can be listed with `ListChildren`
(`GET /v1/namespaces/{name}/children`), which is deliberately non-recursive - call it once per
level to walk a whole tree.

The parent link is set at creation and can't be changed afterwards, like every other namespace
field, which also makes hierarchy cycles impossible. A namespace that still has children can't be
deleted; delete the children first. Deleting a pseudonym level doesn't cascade to any other level.
