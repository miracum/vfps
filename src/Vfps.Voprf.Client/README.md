# Vfps.Voprf.Client

The value-holding half of the exchange. A string goes in, a pseudonym comes out, and the key
holder never sees the string.

```csharp
builder.Services.AddVoprfPseudonymizer(builder.Configuration);

// wherever a pseudonym is needed
var pseudonym = await pseudonymizer.PseudonymizeAsync("alice@example.com");
// pseudonym.Value → "K0eKs9wFGjTEXRH1XufqABf1..."
// pseudonym.KeyId → "v2"
```

```jsonc
"Voprf": {
  "Address": "https://voprf-v2:8081",
  "PublicKey": "d4063c33c520eba5d61c1bd2d553be6d2ddf840598af6f7db1821c54ef5ef119",
  "ExpectedKeyId": "v2"
}
```

## What happens per call

1. The value is normalized (NFC by default) and encoded as UTF-8.
2. It is hashed into the group and multiplied by a **fresh random scalar**. What goes on the wire
   is a uniformly random group element — the server cannot recover the value, and cannot tell that
   two requests carried the same one.
3. The server applies its key and returns a DLEQ proof that it used the key it published.
4. The proof is verified **against the pinned public key** before anything else happens.
5. The blind is removed and the result hashed together with the original value.

The original never leaves the process. Steps 4 and 5 are in that order on purpose: an unverified
answer is not a weaker pseudonym, it is a value of unknown provenance.

## Pin the public key

`PublicKey` is the one setting that cannot be taken from the server. A client that verifies
against whatever key the server just offered has verified only that the server can do arithmetic
consistently — a server evaluating under a substituted key would hand over the matching public key
and every proof would check out, while quietly producing pseudonyms that single this client's
records out from everyone else's.

Get it out of band: the server logs its public key at startup, and `GetPublicKey` will tell you
once so you can put it in configuration. `AllowUnpinnedPublicKey` skips the requirement for
development — it logs a warning on every fetch saying exactly what was given up.

`ExpectedKeyId` is optional but worth setting. A mismatch fails verification anyway, but failing
on the id first says plainly that the address was re-pointed at another deployment rather than
reporting a proof failure that reads like tampering.

## Store `KeyId` with the pseudonym

`PseudonymizeAsync` returns a `Pseudonym`, not a `string`, because a pseudonym is meaningless
without knowing which key produced it. Rotating a key does not re-key a store — the old pseudonyms
simply belong to the old key — so a store that did not record the generation cannot be migrated:
there is no way to tell which rows are done. It costs one column. See the server's README for the
rotation procedure.

## Batch when you can

```csharp
var pseudonyms = await pseudonymizer.PseudonymizeAsync(values);
```

One request, one proof, one verification, and the results follow the input order. The server
cannot tell a batch of related values from a batch of unrelated ones. The server caps batch size
(128 by default) and rejects anything larger.

## Settings that change the output

| Setting | Default | Effect |
| --- | --- | --- |
| `Format` | `Base64Url` | Unpadded base64url, or lowercase hex |
| `Length` | 64 | Bytes kept, 16–64. Truncation is sound the way SHA-512/256 is; what shrinks is collision resistance, near 2^64 at 16 bytes |
| `Normalization` | `FormC` | Unicode normalization applied before UTF-8 encoding |

**Every one of these changes the pseudonym**, so they are a contract between the systems sharing a
store rather than a local preference. Changing one is as disruptive as changing the key — and
unlike the key it leaves no `KeyId` behind to notice it by.

Normalization is where deployments quietly fail. A name whose accent is one code point and the
same name whose accent is a combining mark render identically, arrive from different platforms,
and are different byte strings; without NFC one person is filed twice and nothing detects it. Case
and whitespace are deliberately left alone — whether `Alice@Example.com` is the same subject as
`alice@example.com` is a question about your data, not about the cryptography, so fold upstream if
you need it.

## Failure modes

| Exception | Meaning |
| --- | --- |
| `VoprfVerificationException` | The server did not use the pinned key. **Abort** — the answer is not a valid pseudonym. |
| `VoprfException` | The server answered under an unexpected `KeyId`, or returned the wrong number of elements. |
| `VoprfClientConfigurationException` | The options cannot produce a usable client (no address, no pinned key, bad length). Thrown at registration. |
| `ArgumentException` | An empty value, or one over 65535 bytes of UTF-8. |
| `RpcException` | Transport or server-side rejection — including `NOT_FOUND` if the address now points at a retired generation. |

An empty value is refused rather than pseudonymized: it is almost always a missing field, and
giving it a stable pseudonym makes every record missing that field look like one very busy person.

## Layout

| | |
| --- | --- |
| [`Vfps.Voprf`](../Vfps.Voprf/README.md) | the protocol — blind, verify, unblind |
| [`Vfps.Voprf.Contracts`](../Vfps.Voprf.Contracts/) | the `.proto`, compiled once for both sides |
| [`Vfps.Voprf.Server`](../Vfps.Voprf.Server/README.md) | the key holder |
| this project | normalization, encoding, pinning, and the gRPC round trip |
