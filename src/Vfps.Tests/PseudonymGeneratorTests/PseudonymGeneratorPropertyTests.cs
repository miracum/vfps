using FakeItEasy;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Vfps.Protos;
using Vfps.PseudonymGenerators;

namespace Vfps.Tests.PseudonymGeneratorTests;

/// <summary>
/// The generators and the lookup over generated lengths and methods.
/// </summary>
/// <remarks>
/// A namespace's PseudonymLength is chosen by whoever creates it, so every generator is asked
/// for lengths nobody picked in advance. The existing tests name three or four each; these state
/// the same guarantees for all of them.
/// </remarks>
public class PseudonymGeneratorPropertyTests
{
    /// <summary>
    /// A generator that returns exactly the requested number of characters, and the alphabet it
    /// draws them from.
    /// </summary>
    /// <remarks>
    /// <see cref="CryptoRandomBase64UrlEncodedGenerator"/> is deliberately absent: it treats its
    /// argument as a count of random bytes to mix with a machine fingerprint and a timestamp, so
    /// its output is longer than the number it was given. That is long-standing behaviour rather
    /// than a bug to catch here, but it is the reason this list is explicit rather than every
    /// registered generator.
    /// </remarks>
    private sealed record ExactLengthGenerator(
        string Name,
        IPseudonymGenerator Generator,
        string Alphabet
    );

    private static readonly ExactLengthGenerator[] ExactLength =
    [
        new(
            nameof(FullRandomHexEncodedGenerator),
            new FullRandomHexEncodedGenerator(),
            "0123456789abcdef"
        ),
        new(
            nameof(FullRandomBase32EncodedGenerator),
            new FullRandomBase32EncodedGenerator(),
            "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567"
        ),
        new(
            nameof(FullRandomBase62EncodedGenerator),
            new FullRandomBase62EncodedGenerator(),
            "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz"
        ),
    ];

    [Property]
    public Property Every_length_produces_exactly_that_many_characters_from_the_alphabet() =>
        Prop.ForAll(
            Arb.From(Gen.Elements(ExactLength)),
            Arb.From(Gen.Choose(1, 512)),
            (ExactLengthGenerator subject, int length) =>
            {
                var generated = subject.Generator.GeneratePseudonym((uint)length);

                // The hex generator is the one with something to get wrong: it draws ceil(n/2)
                // bytes and trims a digit for odd lengths, so every odd length exercises a branch
                // that no even one does.
                return generated.Length == length
                    && generated.All(character => subject.Alphabet.Contains(character));
            }
        );

    [Property]
    public Property A_uuid_generator_refuses_every_length_but_its_own() =>
        Prop.ForAll(
            Arb.From(
                Gen.Elements<IPseudonymGenerator>([new Uuid4Generator(), new Uuid7Generator()])
            ),
            Arb.From(Gen.Choose(0, 128)),
            (IPseudonymGenerator generator, int length) =>
            {
                var fixedLength = ((IHasFixedPseudonymLength)generator).FixedPseudonymLength;

                try
                {
                    // Refusing is the point: a namespace declaring some other length would
                    // otherwise store values that do not match what it says it holds.
                    return generator.GeneratePseudonym((uint)length).Length == (int)fixedLength
                        && length == (int)fixedLength;
                }
                catch (ArgumentOutOfRangeException)
                {
                    return length != (int)fixedLength;
                }
            }
        );

    /// <summary>
    /// Every method the lookup could be asked about, including values outside the enum - an
    /// existing namespace can hold a method a later version no longer defines.
    /// </summary>
    private static Arbitrary<PseudonymGenerationMethod> AnyMethod =>
        Arb.From(Gen.Choose(-2, 12).Select(value => (PseudonymGenerationMethod)value));

    [Property]
    public Property Asking_about_any_method_answers_rather_than_throwing() =>
        Prop.ForAll(
            AnyMethod,
            Arb.From(Gen.Elements([true, false])),
            (PseudonymGenerationMethod method, bool voprfConfigured) =>
            {
                // These three are called while the admin UI renders its namespace form, and an
                // exception there tears down the Blazor circuit: the page falls back to static
                // rendering and the next post fails with a message about @formname that says
                // nothing about the real cause. That happened once, for exactly one method, on a
                // deployment where these answers were never exercised together.
                var lookup = new PseudonymizationMethodsLookup(
                    voprfConfigured ? A.Fake<IValueDependentPseudonymGenerator>() : null
                );

                PseudonymizationMethodsLookup.IsValueDependent(method);
                var supported = lookup.IsSupported(method);
                var length = lookup.GetFixedPseudonymLength(method);

                // And an unusable method has no length to declare, rather than some default one
                // a namespace could then be created against.
                return supported || length is null;
            }
        );

    [Property]
    public Property Voprf_is_available_exactly_when_a_server_is_configured() =>
        Prop.ForAll(
            Arb.From(Gen.Elements([true, false])),
            (bool voprfConfigured) =>
            {
                var lookup = new PseudonymizationMethodsLookup(
                    voprfConfigured ? A.Fake<IValueDependentPseudonymGenerator>() : null
                );

                // With no server, VOPRF is not an available method at all - the same way a
                // removed method isn't - and nothing else changes with it.
                return lookup.IsSupported(PseudonymGenerationMethod.Voprf) == voprfConfigured
                    && lookup.IsSupported(PseudonymGenerationMethod.Uuid4)
                    && lookup.IsSupported(PseudonymGenerationMethod.FullRandomBase62Encoded);
            }
        );
}
