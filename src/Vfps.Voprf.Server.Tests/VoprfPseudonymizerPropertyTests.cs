using System.Text;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Vfps.Voprf.Client;

namespace Vfps.Voprf.Server.Tests;

/// <summary>
/// The client against a real server, over generated inputs rather than named ones.
/// </summary>
/// <remarks>
/// <para>
/// What this reaches that <see cref="VoprfPseudonymizerTests"/> does not is the two places where
/// the client makes a decision based on a number: how it splits a list across round trips, and
/// how it truncates and encodes the output. Both were written against one configuration and are
/// correct for every one of them or none - the chunking in particular went in only after a
/// thousand-row CSV chunk met a server cap of 128, and a test naming one list length would not
/// have found that.
/// </para>
/// <para>
/// Each case here is a real round trip through a real server, so the case counts are lower than
/// the default. That is the right trade: these properties are about boundaries, and the
/// generators are small enough to cover them.
/// </para>
/// </remarks>
public sealed class VoprfPseudonymizerPropertyTests : IDisposable
{
    /// <summary>Largest list any case sends, and so how many subjects need an expected value.</summary>
    private const int MaxSubjects = 140;

    private static readonly string[] Subjects =
    [
        .. Enumerable.Range(0, MaxSubjects).Select(i => $"subject-{i}"),
    ];

    /// <summary>
    /// What the key holder would have produced for each subject, computed once. The comparison
    /// has to be against this rather than against the client's own unchunked answer: an
    /// unchunked run of 140 values would itself exceed the server's batch cap, so the reference
    /// would be produced by the very code under test.
    /// </summary>
    private static readonly string[] Expected =
    [
        .. Subjects.Select(value =>
            $"{VoprfTestServer.KeyId}.{Base64Url.EncodeToString(VoprfTestServer.Locally(value))}"
        ),
    ];

    private readonly VoprfTestServer server = new();

    public void Dispose() => server.Dispose();

    [Property(MaxTest = 12)]
    public Property Any_list_is_split_to_the_cap_without_changing_what_comes_back() =>
        Prop.ForAll(
            Arb.From(Gen.Choose(1, MaxSubjects)),
            Arb.From(Gen.Choose(1, 40)),
            (int count, int maxBatchSize) =>
            {
                var pseudonymizer = server.CreatePseudonymizer(options =>
                    options.MaxBatchSize = maxBatchSize
                );

                var pseudonyms = pseudonymizer
                    .PseudonymizeAsync(Subjects[..count], TestContext.Current.CancellationToken)
                    .GetAwaiter()
                    .GetResult();

                // Every chunk is a separate request under a separate proof, so the risk is not
                // that a value comes back wrong but that the chunks are reassembled in the wrong
                // order - which would silently file each subject under someone else's pseudonym.
                return pseudonyms.Count == count
                    && !pseudonyms.Where((psn, i) => psn.Value != Expected[i]).Any();
            }
        );

    [Property(MaxTest = 20)]
    public Property Any_supported_length_and_format_truncates_the_same_output() =>
        Prop.ForAll(
            Arb.From(Gen.Choose(VoprfClientOptions.MinimumLength, VoprfSuite.OutputLength)),
            Arb.From(Gen.Elements([PseudonymFormat.Base64Url, PseudonymFormat.Hex])),
            (int length, PseudonymFormat format) =>
            {
                var pseudonymizer = server.CreatePseudonymizer(options =>
                {
                    options.Length = length;
                    options.Format = format;
                });

                var pseudonym = pseudonymizer
                    .PseudonymizeAsync("alice@example.com", TestContext.Current.CancellationToken)
                    .GetAwaiter()
                    .GetResult();

                // Length and Format are as fixed as the key: every deployment sharing a key has
                // to agree on them, and a length where the encoding did something slightly
                // different would make one deployment's pseudonyms silently unjoinable.
                var full = VoprfTestServer.Locally("alice@example.com").AsSpan(0, length);
                var expected =
                    format == PseudonymFormat.Base64Url
                        ? Base64Url.EncodeToString(full)
                        : Convert.ToHexStringLower(full);

                return pseudonym.Value == $"{VoprfTestServer.KeyId}.{expected}";
            }
        );

    /// <summary>
    /// Characters that make the same text more than one byte string: precomposed forms, the
    /// combining marks that decompose to them, and enough ordinary characters to put them in
    /// context.
    /// </summary>
    private static readonly char[] Confusables =
    [
        'a',
        'e',
        'n',
        'o',
        'u',
        '@',
        '.',
        '-',
        ' ',
        '́', // combining acute
        '̈', // combining diaeresis
        '̃', // combining tilde
        'é', // precomposed e-acute
        'ü', // precomposed u-diaeresis
        'ñ', // precomposed n-tilde
    ];

    [Property(MaxTest = 25)]
    public Property Any_value_gives_the_same_pseudonym_as_its_normalized_form() =>
        Prop.ForAll(
            Arb.From(
                Gen.Choose(1, 24)
                    .SelectMany(length => Gen.ArrayOf(Gen.Elements(Confusables), length))
                    .Select(characters => new string(characters))
            ),
            (string value) =>
            {
                var pseudonymizer = server.CreatePseudonymizer();

                // The same name typed on two platforms arrives as two byte strings. Without
                // normalization each gets its own pseudonym and the person is filed twice - and
                // which characters are affected is a property of Unicode, not of the examples
                // someone happened to think of.
                var direct = Pseudonymize(pseudonymizer, value);
                var normalized = Pseudonymize(
                    pseudonymizer,
                    value.Normalize(NormalizationForm.FormC)
                );

                return direct == normalized;
            }
        );

    private static string Pseudonymize(IVoprfPseudonymizer pseudonymizer, string value) =>
        pseudonymizer
            .PseudonymizeAsync(value, TestContext.Current.CancellationToken)
            .GetAwaiter()
            .GetResult()
            .Value;
}
