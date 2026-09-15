using Vfps.Voprf;

namespace Vfps.Tests.VoprfTests;

/// <summary>
/// The official test vectors for VOPRF(ristretto255, SHA-512) from RFC 9497 appendix A.1.2.
/// </summary>
/// <remarks>
/// These pin the whole construction - hash-to-group, the expander underneath it, the domain
/// separation tags, the transcript framing, and the DLEQ proof - to the standard rather than
/// to itself. A wrong implementation still emits plausible pseudorandom bytes and still
/// verifies its own proofs, so agreeing with the specification is the only check that
/// distinguishes the two.
/// </remarks>
public class Rfc9497VectorTests
{
    private const string Seed = "a3a3a3a3a3a3a3a3a3a3a3a3a3a3a3a3a3a3a3a3a3a3a3a3a3a3a3a3a3a3a3a3";
    private const string KeyInfo = "74657374206b6579";
    private const string SkSm = "e6f73f344b79b379f1a0dd37e07ff62e38d9f71345ce62ae3a9bc60b04ccd909";
    private const string PkSm = "c803e2cc6b05fc15064549b5920659ca4a77b2cca6f04f6b357009335476ad4e";

    // A.1.2.1, batch size 1
    private const string Input1 = "00";
    private const string Blind1 =
        "64d37aed22a27f5191de1c1d69fadb899d8862b58eb4220029e036ec4c1f6706";
    private const string Blinded1 =
        "863f330cc1a1259ed5a5998a23acfd37fb4351a793a5b3c090b642ddc439b945";
    private const string Evaluated1 =
        "aa8fa048764d5623868679402ff6108d2521884fa138cd7f9c7669a9a014267e";
    private const string Nonce1 =
        "222a5e897cf59db8145db8d16e597e8facb80ae7d4e26d9881aa6f61d645fc0e";
    private const string Proof1 =
        "ddef93772692e535d1a53903db24367355cc2cc78de93b3be5a8ffcc6985dd06"
        + "6d4346421d17bf5117a2a1ff0fcb2a759f58a539dfbe857a40bce4cf49ec600d";
    private const string Output1 =
        "b58cfbe118e0cb94d79b5fd6a6dafb98764dff49c14e1770b566e42402da1a7d"
        + "a4d8527693914139caee5bd03903af43a491351d23b430948dd50cde10d32b3c";

    // A.1.2.2, batch size 1
    private const string Input2 = "5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a";
    private const string Blind2 = Blind1;
    private const string Blinded2 =
        "cc0b2a350101881d8a4cba4c80241d74fb7dcbfde4a61fde2f91443c2bf9ef0c";
    private const string Evaluated2 =
        "60a59a57208d48aca71e9e850d22674b611f752bed48b36f7a91b372bd7ad468";
    private const string Nonce2 = Nonce1;
    private const string Proof2 =
        "401a0da6264f8cf45bb2f5264bc31e109155600babb3cd4e5af7d181a2c9dc0a"
        + "67154fabf031fd936051dec80b0b6ae29c9503493dde7393b722eafdf5a50b02";
    private const string Output2 =
        "8a9a2f3c7f085b65933594309041fc1898d42d0858e59f90814ae90571a6df60"
        + "356f4610bf816f27afdd84f47719e480906d27ecd994985890e5f539e7ea74b6";

    // A.1.2.3, batch size 2: the same two inputs under one proof. The second input is blinded
    // with a different scalar here than in A.1.2.2, so its blinded element and evaluation
    // differ from that vector's; the outputs are the same, as the blind cancels either way.
    private const string BatchBlind2 = Nonce1;
    private const string BatchBlinded2 =
        "90a0145ea9da29254c3a56be4fe185465ebb3bf2a1801f7124bbbadac751e654";
    private const string BatchEvaluated2 =
        "cc5ac221950a49ceaa73c8db41b82c20372a4c8d63e5dded2db920b7eee36a2a";
    private const string BatchNonce =
        "419c4f4f5052c53c45f3da494d2b67b220d02118e0857cdbcf037f9ea84bbe0c";
    private const string BatchProof =
        "cc203910175d786927eeb44ea847328047892ddf8590e723c37205cb74600b0a"
        + "5ab5337c8eb4ceae0494c2cf89529dcf94572ed267473d567aeed6ab873dee08";

    /// <summary>Input, Blind, BlindedElement, EvaluationElement, ProofRandomScalar, Proof, Output.</summary>
    public static TheoryData<string, string, string, string, string, string, string> Vectors =>
        new()
        {
            { Input1, Blind1, Blinded1, Evaluated1, Nonce1, Proof1, Output1 },
            { Input2, Blind2, Blinded2, Evaluated2, Nonce2, Proof2, Output2 },
        };

    [Fact]
    public void DeriveKeyPair_reproduces_the_specification_key_pair()
    {
        using var keyPair = VoprfKeyPair.Derive(
            Convert.FromHexString(Seed),
            Convert.FromHexString(KeyInfo)
        );

        Span<byte> privateKey = stackalloc byte[VoprfKeyPair.PrivateKeyLength];
        keyPair.ExportPrivateKey(privateKey);

        Convert.ToHexStringLower(privateKey).Should().Be(SkSm);
        Convert.ToHexStringLower(keyPair.PublicKey).Should().Be(PkSm);
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Blind_reproduces_the_specification_blinded_element(
        string input,
        string blind,
        string blindedElement,
        string evaluationElement,
        string proofRandomScalar,
        string proof,
        string output
    )
    {
        _ = (evaluationElement, proofRandomScalar, proof, output);

        using var request = VoprfClient.Blind(
            Convert.FromHexString(input),
            Convert.FromHexString(blind)
        );

        Convert.ToHexStringLower(request.BlindedElement).Should().Be(blindedElement);
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void BlindEvaluate_reproduces_the_specification_evaluation_and_proof(
        string input,
        string blind,
        string blindedElement,
        string evaluationElement,
        string proofRandomScalar,
        string proof,
        string output
    )
    {
        _ = (input, blind, output);

        using var keyPair = VoprfKeyPair.Import(Convert.FromHexString(SkSm));

        var (evaluated, generated) = VoprfServer.BlindEvaluate(
            keyPair,
            [Convert.FromHexString(blindedElement)],
            Convert.FromHexString(proofRandomScalar)
        );

        Convert.ToHexStringLower(evaluated[0]).Should().Be(evaluationElement);
        Convert.ToHexStringLower(generated.ToBytes()).Should().Be(proof);
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Finalize_verifies_the_proof_and_reproduces_the_specification_output(
        string input,
        string blind,
        string blindedElement,
        string evaluationElement,
        string proofRandomScalar,
        string proof,
        string output
    )
    {
        _ = (blindedElement, proofRandomScalar);

        using var request = VoprfClient.Blind(
            Convert.FromHexString(input),
            Convert.FromHexString(blind)
        );

        var result = VoprfClient.Finalize(
            request,
            Convert.FromHexString(evaluationElement),
            VoprfProof.Parse(Convert.FromHexString(proof)),
            Convert.FromHexString(PkSm)
        );

        Convert.ToHexStringLower(result).Should().Be(output);
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Evaluate_reproduces_the_specification_output_without_blinding(
        string input,
        string blind,
        string blindedElement,
        string evaluationElement,
        string proofRandomScalar,
        string proof,
        string output
    )
    {
        _ = (blind, blindedElement, evaluationElement, proofRandomScalar, proof);

        using var keyPair = VoprfKeyPair.Import(Convert.FromHexString(SkSm));

        var evaluated = VoprfServer.Evaluate(keyPair, Convert.FromHexString(input));

        Convert.ToHexStringLower(evaluated).Should().Be(output);
    }

    [Fact]
    public void BlindEvaluate_reproduces_the_specification_batched_proof()
    {
        using var keyPair = VoprfKeyPair.Import(Convert.FromHexString(SkSm));

        var (evaluated, proof) = VoprfServer.BlindEvaluate(
            keyPair,
            [Convert.FromHexString(Blinded1), Convert.FromHexString(BatchBlinded2)],
            Convert.FromHexString(BatchNonce)
        );

        Convert.ToHexStringLower(evaluated[0]).Should().Be(Evaluated1);
        Convert.ToHexStringLower(evaluated[1]).Should().Be(BatchEvaluated2);
        Convert.ToHexStringLower(proof.ToBytes()).Should().Be(BatchProof);
    }

    [Fact]
    public void Finalize_verifies_one_proof_over_a_batch_and_reproduces_both_outputs()
    {
        using var first = VoprfClient.Blind(
            Convert.FromHexString(Input1),
            Convert.FromHexString(Blind1)
        );
        using var second = VoprfClient.Blind(
            Convert.FromHexString(Input2),
            Convert.FromHexString(BatchBlind2)
        );

        Convert.ToHexStringLower(first.BlindedElement).Should().Be(Blinded1);
        Convert.ToHexStringLower(second.BlindedElement).Should().Be(BatchBlinded2);

        var outputs = VoprfClient.Finalize(
            [first, second],
            [Convert.FromHexString(Evaluated1), Convert.FromHexString(BatchEvaluated2)],
            VoprfProof.Parse(Convert.FromHexString(BatchProof)),
            Convert.FromHexString(PkSm)
        );

        Convert.ToHexStringLower(outputs[0]).Should().Be(Output1);
        Convert.ToHexStringLower(outputs[1]).Should().Be(Output2);
    }
}
