using System.Text;
using Vfps.Voprf;

namespace Vfps.Tests.VoprfTests;

/// <summary>
/// The protocol's own guarantees, on top of the specification vectors: that the exchange
/// round-trips, that blinding hides the input, and that verification catches a server which
/// did not use the key it published.
/// </summary>
public class VoprfProtocolTests
{
    private static readonly byte[] Identifier = Encoding.UTF8.GetBytes("alice@example.com");

    [Fact]
    public void The_blinded_exchange_agrees_with_direct_evaluation()
    {
        using var keyPair = VoprfKeyPair.Generate();
        using var request = VoprfClient.Blind(Identifier);

        var (evaluated, proof) = VoprfServer.BlindEvaluate(keyPair, request.BlindedElement);
        var viaExchange = VoprfClient.Finalize(request, evaluated, proof, keyPair.PublicKey);

        var directly = VoprfServer.Evaluate(keyPair, Identifier);

        viaExchange.Should().Equal(directly);
    }

    [Fact]
    public void The_same_input_gives_the_same_output_under_the_same_key()
    {
        using var keyPair = VoprfKeyPair.Generate();

        VoprfServer
            .Evaluate(keyPair, Identifier)
            .Should()
            .Equal(VoprfServer.Evaluate(keyPair, Identifier));
    }

    [Fact]
    public void Different_keys_give_unrelated_outputs_for_the_same_input()
    {
        using var first = VoprfKeyPair.Generate();
        using var second = VoprfKeyPair.Generate();

        VoprfServer
            .Evaluate(first, Identifier)
            .Should()
            .NotEqual(VoprfServer.Evaluate(second, Identifier));
    }

    [Fact]
    public void Two_requests_for_the_same_input_are_unlinkable_but_agree_on_the_output()
    {
        using var keyPair = VoprfKeyPair.Generate();
        using var first = VoprfClient.Blind(Identifier);
        using var second = VoprfClient.Blind(Identifier);

        // This is what blinding buys: the server sees two unrelated elements and cannot tell
        // that they carry the same identifier.
        first.BlindedElement.Should().NotEqual(second.BlindedElement);

        var (firstEvaluated, firstProof) = VoprfServer.BlindEvaluate(keyPair, first.BlindedElement);
        var (secondEvaluated, secondProof) = VoprfServer.BlindEvaluate(
            keyPair,
            second.BlindedElement
        );

        VoprfClient
            .Finalize(first, firstEvaluated, firstProof, keyPair.PublicKey)
            .Should()
            .Equal(VoprfClient.Finalize(second, secondEvaluated, secondProof, keyPair.PublicKey));
    }

    [Fact]
    public void A_server_answering_under_a_substituted_key_is_caught()
    {
        // The attack the verifiable variant exists to stop: the server answers with a key of
        // its own choosing, so this client's outputs are unlinkable to everyone else's and
        // its records can be singled out. The answer is well formed and the output would look
        // perfectly ordinary; only the proof gives it away.
        using var published = VoprfKeyPair.Generate();
        using var substituted = VoprfKeyPair.Generate();
        using var request = VoprfClient.Blind(Identifier);

        var (evaluated, proof) = VoprfServer.BlindEvaluate(substituted, request.BlindedElement);

        var finalize = () => VoprfClient.Finalize(request, evaluated, proof, published.PublicKey);

        finalize.Should().Throw<VoprfVerificationException>();
    }

    [Fact]
    public void A_tampered_evaluated_element_is_caught()
    {
        using var keyPair = VoprfKeyPair.Generate();
        using var request = VoprfClient.Blind(Identifier);

        var (evaluated, proof) = VoprfServer.BlindEvaluate(keyPair, request.BlindedElement);
        evaluated[0] ^= 0x01;

        var finalize = () => VoprfClient.Finalize(request, evaluated, proof, keyPair.PublicKey);

        finalize.Should().Throw<VoprfVerificationException>();
    }

    [Fact]
    public void A_tampered_proof_is_caught()
    {
        using var keyPair = VoprfKeyPair.Generate();
        using var request = VoprfClient.Blind(Identifier);

        var (evaluated, proof) = VoprfServer.BlindEvaluate(keyPair, request.BlindedElement);

        var bytes = proof.ToBytes();
        bytes[0] ^= 0x01;

        var finalize = () =>
            VoprfClient.Finalize(request, evaluated, VoprfProof.Parse(bytes), keyPair.PublicKey);

        finalize.Should().Throw<VoprfVerificationException>();
    }

    [Fact]
    public void A_proof_whose_response_is_not_a_canonical_scalar_is_refused()
    {
        // Found by VoprfProtocolPropertyTests sweeping every bit of a proof: this one bit, and
        // only this one, used to survive. libsodium's scalar multiplication reads 255 bits and
        // ignores the top one, so s and s + 2^255 take every element to the same place and the
        // recomputed challenge came out identical - the proof verified in a form the server had
        // not sent. RFC 9497's DeserializeScalar rules out the non-canonical encoding, and
        // VerifyProof now enforces that.
        using var keyPair = VoprfKeyPair.Generate();
        using var request = VoprfClient.Blind(Identifier);

        var (evaluated, proof) = VoprfServer.BlindEvaluate(keyPair, request.BlindedElement);

        var malleated = proof.ToBytes();
        malleated[^1] |= 0x80;

        var finalize = () =>
            VoprfClient.Finalize(
                request,
                evaluated,
                VoprfProof.Parse(malleated),
                keyPair.PublicKey
            );

        finalize.Should().Throw<VoprfVerificationException>();
    }

    [Fact]
    public void A_proof_from_one_batch_does_not_verify_against_another()
    {
        using var keyPair = VoprfKeyPair.Generate();
        using var first = VoprfClient.Blind(Identifier);
        using var second = VoprfClient.Blind(Encoding.UTF8.GetBytes("bob@example.com"));

        var (evaluated, _) = VoprfServer.BlindEvaluate(
            keyPair,
            [first.BlindedElement, second.BlindedElement]
        );

        // A proof covering only the first element must not pass for the pair.
        var (_, narrowProof) = VoprfServer.BlindEvaluate(keyPair, first.BlindedElement);

        var finalize = () =>
            VoprfClient.Finalize([first, second], evaluated, narrowProof, keyPair.PublicKey);

        finalize.Should().Throw<VoprfVerificationException>();
    }

    [Fact]
    public void Reordering_a_batch_is_caught()
    {
        using var keyPair = VoprfKeyPair.Generate();
        using var first = VoprfClient.Blind(Identifier);
        using var second = VoprfClient.Blind(Encoding.UTF8.GetBytes("bob@example.com"));

        var (evaluated, proof) = VoprfServer.BlindEvaluate(
            keyPair,
            [first.BlindedElement, second.BlindedElement]
        );

        var finalize = () =>
            VoprfClient.Finalize(
                [second, first],
                [evaluated[0], evaluated[1]],
                proof,
                keyPair.PublicKey
            );

        finalize.Should().Throw<VoprfVerificationException>();
    }

    [Fact]
    public void A_batch_round_trips_under_one_proof()
    {
        using var keyPair = VoprfKeyPair.Generate();

        var inputs = Enumerable
            .Range(0, 8)
            .Select(i => Encoding.UTF8.GetBytes($"subject-{i}"))
            .ToArray();
        var requests = inputs.Select(input => VoprfClient.Blind(input)).ToArray();

        try
        {
            var (evaluated, proof) = VoprfServer.BlindEvaluate(
                keyPair,
                requests.Select(r => r.BlindedElement).ToArray()
            );

            var outputs = VoprfClient.Finalize(requests, evaluated, proof, keyPair.PublicKey);

            outputs.Should().HaveCount(inputs.Length);
            for (var i = 0; i < inputs.Length; i++)
            {
                outputs[i].Should().Equal(VoprfServer.Evaluate(keyPair, inputs[i]));
            }
        }
        finally
        {
            foreach (var request in requests)
            {
                request.Dispose();
            }
        }
    }

    [Fact]
    public void An_imported_key_recomputes_the_public_key_it_was_generated_with()
    {
        using var generated = VoprfKeyPair.Generate();

        Span<byte> privateKey = stackalloc byte[VoprfKeyPair.PrivateKeyLength];
        generated.ExportPrivateKey(privateKey);

        using var imported = VoprfKeyPair.Import(privateKey);

        imported.PublicKey.ToArray().Should().Equal(generated.PublicKey.ToArray());
    }

    [Fact]
    public void Deriving_the_same_seed_and_key_info_gives_the_same_key()
    {
        var seed = new byte[32];
        Random.Shared.NextBytes(seed);

        using var first = VoprfKeyPair.Derive(seed, "column-a"u8);
        using var second = VoprfKeyPair.Derive(seed, "column-a"u8);
        using var other = VoprfKeyPair.Derive(seed, "column-b"u8);

        first.PublicKey.ToArray().Should().Equal(second.PublicKey.ToArray());
        first.PublicKey.ToArray().Should().NotEqual(other.PublicKey.ToArray());
    }

    [Fact]
    public void A_seed_shorter_than_the_specification_allows_is_rejected()
    {
        // The derivation would happily accept it and produce a key with four bytes of entropy.
        var derive = () => VoprfKeyPair.Derive(new byte[4], "column-a"u8);

        derive.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void The_server_rejects_a_blinded_element_that_is_not_a_valid_encoding()
    {
        using var keyPair = VoprfKeyPair.Generate();

        // All zeroes is the identity: a canonical encoding, but one whose answer would carry
        // no key at all.
        var evaluate = () => VoprfServer.BlindEvaluate(keyPair, new byte[32]);

        evaluate.Should().Throw<VoprfException>();
    }

    [Fact]
    public void A_disposed_key_pair_cannot_be_used()
    {
        var keyPair = VoprfKeyPair.Generate();
        keyPair.Dispose();

        var evaluate = () => VoprfServer.Evaluate(keyPair, Identifier);

        evaluate.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void An_input_too_long_to_frame_is_rejected_rather_than_truncated()
    {
        using var keyPair = VoprfKeyPair.Generate();

        var evaluate = () => VoprfServer.Evaluate(keyPair, new byte[VoprfSuite.MaxInputLength + 1]);

        evaluate.Should().Throw<ArgumentOutOfRangeException>();
    }
}
