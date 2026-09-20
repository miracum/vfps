# Vfps.Voprf

**VOPRF(ristretto255, SHA-512)** — the verifiable oblivious pseudorandom function of
[RFC 9497](https://www.rfc-editor.org/rfc/rfc9497.html), in verifiable mode, over
[libsodium](https://doc.libsodium.org/)'s ristretto255 arithmetic.

A client and a server jointly compute `PRF(key, input)`:

- the **server** holds the key and never learns the input;
- the **client** holds the input and never learns the key;
- the client can **verify** that the server used the key it published, rather than a
  different one chosen to single that client out.

One round trip, no interaction beyond it.

```csharp
// server, once
using var keyPair = VoprfKeyPair.Generate();
byte[] publicKey = keyPair.PublicKey.ToArray();   // published to clients

// client: holds the identifier, never sees the key
using var request = VoprfClient.Blind("alice@example.com"u8);

// server: holds the key, never sees the identifier
var (evaluated, proof) = VoprfServer.BlindEvaluate(keyPair, request.BlindedElement);

// client: verifies the proof, then unblinds
byte[] output = VoprfClient.Finalize(request, evaluated, proof, publicKey);
```

The same input under the same key always gives the same 64-byte output, so outputs join
across tables and across runs. Different keys give unrelated outputs, so two datasets keyed
separately cannot be matched against each other.

## The protocol

| Step | Side | Computation |
| --- | --- | --- |
| Blind | client | `P = HashToGroup(input)`, pick random `r`, send `B = r·P` |
| BlindEvaluate | server | `Z = k·B`, prove `log_G(pkS) = log_B(Z)`, send `Z` and the proof |
| Finalize | client | verify the proof, compute `N = r⁻¹·Z`, output `Hash(input, N)` |

Blinding is what hides the input: `r·P` is uniformly distributed for random `r`, so the
server sees a value independent of the input and cannot link two requests carrying the same
one. Unblinding works because `r⁻¹·(k·(r·P)) = k·P`, which is what the server would have
computed had it known the input.

The proof is a Chaum-Pedersen proof of discrete logarithm equality, made non-interactive by
Fiat-Shamir. It convinces the client that the same `k` behind the published `pkS` was
applied to its element, while revealing nothing about `k`.

## Batching

A batch collapses into one pair by a random linear combination whose coefficients are
derived from the elements themselves, so a request with fifty inputs still carries a single
proof — and the server cannot choose the coefficients, because they depend on what it is
proving.

```csharp
var (evaluated, proof) = VoprfServer.BlindEvaluate(keyPair, blindedElements);
byte[][] outputs = VoprfClient.Finalize(requests, evaluated, proof, publicKey);
```

Verification is all-or-nothing: the client learns that every element was answered under the
published key, or that some element was not — never which. Order matters, and the client
must verify against the same sequence it sent.

## Getting the public key right

Verification means nothing unless the public key reaches the client through a channel the
server cannot vary per client — pinned in configuration, or fetched once and compared across
clients. A client that accepts whatever public key arrives alongside the answer has verified
only that the server can do arithmetic consistently.

## The local shortcut

`VoprfServer.Evaluate(keyPair, input)` computes the output directly for a party holding both
the key and the input. It is byte-for-byte what the blinded exchange produces, because the
blind cancels exactly.

In that arrangement the protocol buys nothing over an HMAC — neither the blinding nor the
proof has anyone to protect against. The reason to start here anyway is that the key can
move to a separate service later without changing a single stored value, so the move is a
deployment change rather than a data migration.

## The native dependency

libsodium arrives through the [`libsodium`](https://www.nuget.org/packages/libsodium) NuGet
package, published by libsodium's own author. It ships `runtimes/<rid>/native/` binaries for
every runtime identifier, and the .NET host resolves them from `deps.json` — so there is no
native library to build, commit, or copy into the container image, and Renovate bumps it
like any other dependency.

Only curve arithmetic goes to libsodium: `expand_message_xmd`, the transcript framing, and
the final hash are managed code over `System.Security.Cryptography.SHA512`.

## Testing

`src/Vfps.Tests/VoprfTests` reproduces every RFC 9497 appendix A.1.2 vector — the derived key
pair, the blinded element, the evaluated element, **the proof bytes themselves**, the
output, and the batch-size-2 proof. A wrong implementation still emits plausible
pseudorandom bytes and still verifies its own proofs, so matching the specification's proofs
byte-for-byte is the only check that tells the two apart.

Alongside them are the attacks the mode exists to catch: a server answering under a
substituted key, a tampered evaluated element, a tampered proof, a proof that covers a
different batch, and a reordered batch.

### Property tests

Those state each guarantee once, for one identifier and one batch size. `VoprfProtocolPropertyTests`
and `VoprfInternalsPropertyTests` state them for generated inputs instead, using
[FsCheck](https://fscheck.github.io/FsCheck/): the exchange round-trips for any input including
the empty one, a batch of any size agrees with the same inputs one at a time, **every** bit of a
proof, an evaluated element and a pinned public key is caught when flipped, and substituting the
key for any single element of a batch is caught.

That last pair earns its place. The bit sweep found that flipping the top bit of a proof's
response scalar produced a proof that still verified — libsodium's scalar multiplication reads
255 bits and ignores the top one, so `s` and `s + 2^255` take every element to the same place.
RFC 9497's `DeserializeScalar` rules out the non-canonical encoding and `VerifyProof` now
enforces it. The example-based tests could not have found it: they flip the first bit, and the
first bit was never the problem.

## Limitations

- **A pseudonym is not anonymity.** Where the input space is enumerable — email addresses,
  phone numbers, national IDs — anyone with the key can evaluate every candidate and match.
  The key is what stands between an output and the subject, which is why it belongs
  somewhere other than beside the outputs.
- **Key rotation invalidates every output** it ever produced, and a store of outputs cannot
  be re-keyed without the original values. Use `VoprfKeyPair.Derive` with a versioned
  `keyInfo` if you intend to rotate (the server exposes that as its key id).
- **The server is an oracle.** Evaluation leaks nothing about the input, but a server that
  answers without limit lets anyone who can reach it evaluate anything. Rate limit and
  authenticate callers as you would any other use of the key.
- **Secrets are not reliably erased.** `VoprfKeyPair` and `VoprfRequest` hold their material
  in pinned buffers and zero them on `Dispose`, which shortens the window rather than
  closing it; the runtime may have copied them already.
- **No Unicode normalization.** Inputs are bytes. "café" composed and "café" decomposed are
  different inputs and get unrelated outputs, so normalize upstream (NFC) before calling in.
- **Base mode and POPRF are not implemented.** Only `modeVOPRF` (0x01). Outputs are
  domain-separated by mode, so they are not interchangeable with base-mode OPRF outputs even
  under the same key.
