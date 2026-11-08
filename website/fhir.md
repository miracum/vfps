# FHIR API

vfps implements the [MII pseudonymization interface](https://medizininformatik-initiative.github.io/mii-interface-module-pseudonymization/),
the FHIR API the Medizininformatik-Initiative specifies for pseudonymization services. Its
operations work on a **pseudonymization context**, which in vfps is a [namespace](namespaces.md):
the context's identifier value is the namespace name.

## Base URL and versioning

Version 2026 of the interface is served at the FHIR base URL `/fhir/mii/2026`:

| Request                            | Purpose                                                   |
| ---------------------------------- | --------------------------------------------------------- |
| `POST /fhir/mii/2026/$<operation>` | Invoke one [operation](#operations) on a single value     |
| `POST /fhir/mii/2026`              | Process a [batch Bundle](#processing-lists) of operations |
| `GET /fhir/mii/2026/metadata`      | The CapabilityStatement                                   |

The `2026` is the interface's major version, which it raises for every breaking change. Minor and
patch releases of the implementation guide stay compatible, so they are served from the same base
URL, and only the CapabilityStatement names the exact release, as
`implementationGuide: https://www.medizininformatik-initiative.de/fhir/ext/mii-interface-module-pseudonymization|2026.1.0`.
A later major version will get a base URL of its own, served next to this one.

Requests and responses are FHIR R4 JSON (`application/fhir+json`). With authorization enabled,
every request, `metadata` included, needs a bearer token - see
[Authenticating API calls](access-control.md#authenticating-api-calls).

## Operations

| Operation                | Does                                                                                                    | Context          | Requires       |
| ------------------------ | ------------------------------------------------------------------------------------------------------- | ---------------- | -------------- |
| `$pseudonymize`          | Returns the pseudonym of an original value, creating it if there is none yet                            | Single-pseudonym | Write          |
| `$get-pseudonym`         | Returns the pseudonym of an original value, creating nothing                                            | Single-pseudonym | Write          |
| `$pseudonymize-multiple` | With a `count` above 0, adds that many new pseudonyms and returns them; with 0, returns all stored ones | Multi-pseudonym  | Write          |
| `$de-pseudonymize`       | Returns the original value of a pseudonym                                                               | Either           | Reverse-lookup |
| `$delete-pseudonym`      | Not implemented yet - answers `501 Not Implemented`                                                     | -                | -              |
| `$anonymize-original`    | Not implemented yet - answers `501 Not Implemented`                                                     | -                | -              |

A context is a multi-pseudonym context when its namespace
[allows multiple pseudonyms per original value](namespaces.md#multiple-pseudonyms-per-original-value),
and a single-pseudonym context otherwise. Calling an operation on a context of the other kind is
answered with a `422` - the single-pseudonym operations don't fall back to the first of several
pseudonyms, the way the [`/v1/fhir` operations](#the-v1fhir-operations) do. `$pseudonymize-multiple`
adds at most 100 pseudonyms per call, and every call with a `count` above 0 adds new ones - it is
not safe to retry.

`$get-pseudonym` needs write access, not read access, for the same reason the REST API's
`:resolve` does: telling whether a value already has a pseudonym reveals whether it was ever in
the namespace. The CapabilityStatement lists the four implemented operations only.

### Parameters

Every operation takes a `Parameters` resource whose inputs are `Identifier`s, each given exactly
once - lists are processed as [batch Bundles](#processing-lists):

- `context` - its `value` is the namespace name.
- `original` - its `value` is the original value.
- `pseudonym` (`$de-pseudonymize`) - its `value` is the pseudonym.
- `count` (`$pseudonymize-multiple`) - an `integer`.

The `system` of each Identifier is optional and doesn't affect what is looked up or stored. Where a
response repeats an input parameter, it is returned exactly as it was sent. A pseudonym that
`$pseudonymize`, `$get-pseudonym` or `$pseudonymize-multiple` returns takes the `system` of the
context, and the original value `$de-pseudonymize` returns has none, because vfps stores the value
alone.

```sh
curl -X POST 'http://localhost:8080/fhir/mii/2026/$pseudonymize' \
  -H "Content-Type: application/fhir+json" \
  -d '{
    "resourceType": "Parameters",
    "parameter": [
      { "name": "context", "valueIdentifier": { "system": "https://example.org/psn", "value": "test" } },
      { "name": "original", "valueIdentifier": { "value": "to be pseudonymized" } }
    ]
  }'
```

```json
{
  "resourceType": "Parameters",
  "parameter": [
    {
      "name": "context",
      "valueIdentifier": {
        "system": "https://example.org/psn",
        "value": "test"
      }
    },
    {
      "name": "original",
      "valueIdentifier": { "value": "to be pseudonymized" }
    },
    {
      "name": "pseudonym",
      "valueIdentifier": {
        "system": "https://example.org/psn",
        "value": "fk6r7kQ1..."
      }
    }
  ]
}
```

## Processing lists

To process several values in one request, post a `batch` Bundle to the base URL, with one entry per
value. Each entry's `request` names the operation, relative to the base URL, and its `resource` is
that operation's `Parameters`:

```json
{
  "resourceType": "Bundle",
  "type": "batch",
  "entry": [
    {
      "resource": {
        "resourceType": "Parameters",
        "parameter": [
          { "name": "context", "valueIdentifier": { "value": "test" } },
          { "name": "original", "valueIdentifier": { "value": "first value" } }
        ]
      },
      "request": { "method": "POST", "url": "$pseudonymize" }
    }
  ]
}
```

The response is a `batch-response` Bundle with one entry per request entry, in the same order. Every
entry is processed on its own and carries its own `response.status`, so one value that fails
doesn't stop the others: a successful entry holds the operation's `Parameters`, a failed one an
`OperationOutcome` in `entry.resource`, where the implementation guide's examples put it. A batch
holds at most 10,000 entries.

Entries run concurrently and in no particular order, so - as FHIR requires of a batch anyway - no
entry may depend on another: a `$get-pseudonym` for a value that an earlier entry of the same batch
pseudonymizes may or may not find it. An entry that isn't a `POST` gets a `405` of its own, one naming no
known operation a `404`. `transaction` Bundles, which would process the list all or nothing, are not
supported and are answered with a `400`.

## Errors

Every error is answered with an `OperationOutcome`, except a request without a bearer token, which
the request pipeline refuses before the API sees it.

| Status | Meaning                                                                                                                                                                                                     |
| ------ | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `400`  | The body isn't a valid resource of the expected type, a parameter is missing, of another type, blank or repeated, or a Bundle is a `transaction` or holds too many entries                                  |
| `401`  | No bearer token, with no body                                                                                                                                                                               |
| `403`  | The token holds no grant for the permission the operation needs                                                                                                                                             |
| `404`  | The context doesn't exist, or holds no pseudonym for the value. `$de-pseudonymize` doesn't tell the two apart                                                                                               |
| `422`  | The namespace refuses the request: the value fails its validation pattern or is missing from its parent namespace, the context is of the wrong kind, or the namespace uses the `External` generation method |
| `500`  | An unexpected error, such as a pseudonym that couldn't be stored after several retries                                                                                                                      |
| `501`  | `$delete-pseudonym` and `$anonymize-original`, which are not implemented yet                                                                                                                                |

An `OperationOutcome` never contains an original value, so it is safe to log.

## The `/v1/fhir` operations

Before the MII interface, vfps offered two operations of its own at `/v1/fhir`, which are still
served: `$create-pseudonym` and `$resolve-pseudonym` take string `namespace` and `originalValue`
parameters and answer with a `pseudonymValue`, and always work on the first pseudonym of a
multi-pseudonym namespace. New clients should use the MII interface.
