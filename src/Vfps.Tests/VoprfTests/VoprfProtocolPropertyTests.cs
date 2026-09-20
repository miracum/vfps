using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Vfps.Voprf;

namespace Vfps.Tests.VoprfTests;

/// <summary>
/// The protocol's guarantees stated as properties over generated inputs, rather than over the
/// handful of identifiers <see cref="VoprfProtocolTests"/> names explicitly.
/// </summary>
/// <remarks>
/// <para>
/// The specification vectors in <see cref="Rfc9497VectorTests"/> pin the arithmetic to fixed
/// values, and the example-based tests state each guarantee once. What neither reaches is the
/// input space: empty inputs, single bytes, batches of every size, and every bit position of a
/// proof. Those are where an implementation error hides, because they are the cases nobody
/// thinks to write down - the batch composites are derived per index, so a batch of eight says
/// little about a batch of one, and a tamper test that flips the first bit says nothing about
/// the other 511.
/// </para>
/// <para>
/// Each property here is a statement that should hold for every input, and FsCheck's job is to
/// look for the one that does not. A failure is reported with the shrunk counterexample, so a
/// broken case arrives as the smallest input that breaks it.
/// </para>
/// </remarks>
public sealed class VoprfProtocolPropertyTests : IDisposable
{
    /// <summary>
    /// One key pair per test method - xunit constructs the class per test - so the cost of
    /// generating it is not paid per generated case.
    /// </summary>
    private readonly VoprfKeyPair keyPair = VoprfKeyPair.Generate();

    public void Dispose() => keyPair.Dispose();

    /// <summary>
    /// Byte strings between <paramref name="minimum"/> and <paramref name="maximum"/> bytes.
    /// The lower bound is usually zero: RFC 9497 admits the empty input, and it is precisely the
    /// case a length-framing mistake would mishandle.
    /// </summary>
    private static Gen<byte[]> Bytes(int minimum, int maximum) =>
        Gen.Choose(minimum, maximum)
            .SelectMany(length => Gen.ArrayOf(ArbMap.Default.GeneratorFor<byte>(), length));

    private static Arbitrary<byte[]> AnyInput => Arb.From(Bytes(0, 256));

    [Property]
    public Property The_blinded_exchange_agrees_with_direct_evaluation_for_any_input() =>
        Prop.ForAll(
            AnyInput,
            (byte[] input) =>
            {
                using var request = VoprfClient.Blind(input);

                var (evaluated, proof) = VoprfServer.BlindEvaluate(keyPair, request.BlindedElement);
                var viaExchange = VoprfClient.Finalize(
                    request,
                    evaluated,
                    proof,
                    keyPair.PublicKey
                );

                // The whole point of the blinding: it cancels exactly, whatever the input was.
                return viaExchange.SequenceEqual(VoprfServer.Evaluate(keyPair, input));
            }
        );

    [Property]
    public Property Evaluation_is_a_function_of_the_input_and_nothing_else() =>
        Prop.ForAll(
            AnyInput,
            AnyInput,
            (byte[] first, byte[] second) =>
                // Both directions at once: equal inputs must agree (determinism, which is what
                // makes a pseudonym join across runs) and different inputs must not (no
                // collisions, which is what keeps two subjects from sharing one).
                first.SequenceEqual(second)
                == VoprfServer
                    .Evaluate(keyPair, first)
                    .SequenceEqual(VoprfServer.Evaluate(keyPair, second))
        );

    [Property]
    public Property A_batch_of_any_size_agrees_with_the_same_inputs_one_at_a_time() =>
        Prop.ForAll(
            Arb.From(Gen.Choose(1, 12).SelectMany(count => Gen.ArrayOf(Bytes(0, 64), count))),
            (byte[][] inputs) =>
            {
                var requests = Array.ConvertAll(inputs, input => VoprfClient.Blind(input));
                try
                {
                    var (evaluated, proof) = VoprfServer.BlindEvaluate(
                        keyPair,
                        Array.ConvertAll(requests, request => request.BlindedElement)
                    );

                    var outputs = VoprfClient.Finalize(
                        requests,
                        evaluated,
                        proof,
                        keyPair.PublicKey
                    );

                    // Batching is an optimisation over the round trips, not over the result: the
                    // composites are derived per index, so a batch of n must still say exactly
                    // what n separate evaluations would.
                    return outputs.Length == inputs.Length
                        && !inputs
                            .Where(
                                (input, i) =>
                                    !outputs[i].SequenceEqual(VoprfServer.Evaluate(keyPair, input))
                            )
                            .Any();
                }
                finally
                {
                    foreach (var request in requests)
                    {
                        request.Dispose();
                    }
                }
            }
        );

    [Property]
    public Property Flipping_any_single_bit_of_a_proof_is_caught() =>
        Prop.ForAll(
            Arb.From(Gen.Choose(0, (VoprfProof.Length * 8) - 1)),
            AnyInput,
            (int bit, byte[] input) =>
            {
                using var request = VoprfClient.Blind(input);
                var (evaluated, proof) = VoprfServer.BlindEvaluate(keyPair, request.BlindedElement);

                var tampered = proof.ToBytes();
                tampered[bit / 8] ^= (byte)(1 << (bit % 8));

                // Every one of the 512 bits, not only the first. Both halves of the proof feed
                // the recomputed challenge, so there is no bit a forger could move for free.
                return Rejects(request, evaluated, VoprfProof.Parse(tampered), keyPair.PublicKey);
            }
        );

    [Property]
    public Property Flipping_any_single_bit_of_an_evaluated_element_is_caught() =>
        Prop.ForAll(
            Arb.From(Gen.Choose(0, (VoprfSuite.ElementLength * 8) - 1)),
            AnyInput,
            (int bit, byte[] input) =>
            {
                using var request = VoprfClient.Blind(input);
                var (evaluated, proof) = VoprfServer.BlindEvaluate(keyPair, request.BlindedElement);

                evaluated[bit / 8] ^= (byte)(1 << (bit % 8));

                // A flip may leave a valid encoding or an invalid one. Both must be refused, and
                // indistinguishably so - VerifyProof answers false for either.
                return Rejects(request, evaluated, proof, keyPair.PublicKey);
            }
        );

    [Property]
    public Property Flipping_any_single_bit_of_the_pinned_public_key_is_caught() =>
        Prop.ForAll(
            Arb.From(Gen.Choose(0, (VoprfKeyPair.PublicKeyLength * 8) - 1)),
            AnyInput,
            (int bit, byte[] input) =>
            {
                using var request = VoprfClient.Blind(input);
                var (evaluated, proof) = VoprfServer.BlindEvaluate(keyPair, request.BlindedElement);

                // The client's side of the same coin: a pin that does not match the key the
                // answer was produced under must fail, however close the two are.
                var publicKey = keyPair.PublicKey.ToArray();
                publicKey[bit / 8] ^= (byte)(1 << (bit % 8));

                return Rejects(request, evaluated, proof, publicKey);
            }
        );

    [Property]
    public Property Substituting_the_key_for_any_one_element_of_a_batch_is_caught() =>
        Prop.ForAll(
            Arb.From(Gen.Choose(1, 12)),
            Arb.From(Gen.Choose(0, 255)),
            (int count, int which) =>
            {
                // The batch proof is all-or-nothing, and this is the claim that makes it worth
                // having: a server cannot answer most of a batch honestly and slip one element
                // in under another key. The existing tests only substitute the whole batch.
                var target = which % count;

                using var other = VoprfKeyPair.Generate();
                var requests = new VoprfRequest[count];
                try
                {
                    for (var i = 0; i < count; i++)
                    {
                        requests[i] = VoprfClient.Blind(
                            System.Text.Encoding.UTF8.GetBytes($"subject-{i}")
                        );
                    }

                    var blinded = Array.ConvertAll(requests, request => request.BlindedElement);
                    var (evaluated, proof) = VoprfServer.BlindEvaluate(keyPair, blinded);

                    var (substituted, _) = VoprfServer.BlindEvaluate(other, blinded[target]);
                    evaluated[target] = substituted;

                    try
                    {
                        VoprfClient.Finalize(requests, evaluated, proof, keyPair.PublicKey);
                        return false;
                    }
                    catch (VoprfVerificationException)
                    {
                        return true;
                    }
                }
                finally
                {
                    foreach (var request in requests)
                    {
                        request?.Dispose();
                    }
                }
            }
        );

    [Property]
    public Property Any_derived_key_survives_a_round_trip_through_a_secret_store() =>
        Prop.ForAll(
            Arb.From(Bytes(VoprfKeyPair.MinimumSeedLength, 64)),
            Arb.From(Bytes(0, 16)),
            (byte[] seed, byte[] label) =>
            {
                using var derived = VoprfKeyPair.Derive(seed, label);

                Span<byte> exported = stackalloc byte[VoprfKeyPair.PrivateKeyLength];
                derived.ExportPrivateKey(exported);

                using var imported = VoprfKeyPair.Import(exported);

                // What Key:Source File and Base64 do at startup. If any derivable key could
                // survive export but not import, that deployment would fail only on that key.
                return imported.PublicKey.SequenceEqual(derived.PublicKey);
            }
        );

    [Property]
    public Property Derivation_depends_on_the_seed_and_the_label_and_nothing_else() =>
        Prop.ForAll(
            Arb.From(Bytes(VoprfKeyPair.MinimumSeedLength, 64)),
            Arb.From(Bytes(0, 16)),
            Arb.From(Bytes(0, 16)),
            (byte[] seed, byte[] label, byte[] otherLabel) =>
            {
                using var first = VoprfKeyPair.Derive(seed, label);
                using var again = VoprfKeyPair.Derive(seed, label);
                using var under = VoprfKeyPair.Derive(seed, otherLabel);

                // Reproducibility is what lets two deployments share a seed and agree on the
                // pseudonyms; separation by label is what makes a rotation - and a per-domain
                // key - a new KeyId rather than a second secret.
                return first.PublicKey.SequenceEqual(again.PublicKey)
                    && label.SequenceEqual(otherLabel)
                        == first.PublicKey.SequenceEqual(under.PublicKey);
            }
        );

    [Property]
    public Property Any_seed_shorter_than_the_specification_allows_is_refused() =>
        Prop.ForAll(
            Arb.From(Gen.Choose(0, VoprfKeyPair.MinimumSeedLength - 1)),
            (int length) =>
            {
                // Nothing in the derivation would fail on a short seed - it would hand back a key
                // with as little entropy as the seed had - so this has to be refused explicitly,
                // at every length below the bound rather than at one sampled one.
                try
                {
                    VoprfKeyPair.Derive(new byte[length], "v1"u8).Dispose();
                    return false;
                }
                catch (ArgumentException)
                {
                    return true;
                }
            }
        );

    /// <summary>
    /// Whether finalising refuses the answer. Any refusal is the same refusal: the client is
    /// told the batch was not answered under the pinned key, never which element or why.
    /// </summary>
    private static bool Rejects(
        VoprfRequest request,
        byte[] evaluatedElement,
        VoprfProof proof,
        ReadOnlySpan<byte> publicKey
    )
    {
        try
        {
            VoprfClient.Finalize(request, evaluatedElement, proof, publicKey);
            return false;
        }
        catch (VoprfVerificationException)
        {
            return true;
        }
    }
}
