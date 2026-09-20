using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Vfps.Voprf;
using Vfps.Voprf.Internal;

namespace Vfps.Tests.VoprfTests;

/// <summary>
/// The primitives underneath the protocol, stated as properties.
/// </summary>
/// <remarks>
/// These are reachable only through <c>InternalsVisibleTo</c>, and they are worth reaching
/// directly because the protocol tests can only observe them through their combined effect: a
/// mistake here shows up there as "the exchange does not round-trip", with no indication of
/// which of four layers is at fault. Each of the following is a standalone claim about one of
/// them, so a failure names the layer.
/// </remarks>
public class VoprfInternalsPropertyTests
{
    private static Gen<byte[]> Bytes(int minimum, int maximum) =>
        Gen.Choose(minimum, maximum)
            .SelectMany(length => Gen.ArrayOf(ArbMap.Default.GeneratorFor<byte>(), length));

    [Property]
    public Property Expansion_produces_exactly_the_requested_number_of_bytes() =>
        Prop.ForAll(
            Arb.From(Bytes(0, 128)),
            Arb.From(Bytes(0, 255)),
            Arb.From(Gen.Choose(1, 4000)),
            (byte[] message, byte[] domainSeparationTag, int length) =>
                // The block loop takes ceil(length / 64) hashes and copies a partial block last.
                // Every length that is not a multiple of 64 exercises that truncation, and the
                // protocol only ever asks for 64 - so nothing else here would notice.
                ExpandMessage.Xmd(message, domainSeparationTag, length).Length == length
        );

    [Property]
    public Property Expansion_is_a_function_of_the_message_the_tag_and_the_length() =>
        Prop.ForAll(
            Arb.From(Bytes(0, 64)),
            Arb.From(Bytes(1, 32)),
            Arb.From(Bytes(1, 32)),
            (byte[] message, byte[] tag, byte[] otherTag) =>
            {
                var expanded = ExpandMessage.Xmd(message, tag, 64);

                return expanded.SequenceEqual(ExpandMessage.Xmd(message, tag, 64))
                    && tag.SequenceEqual(otherTag)
                        == expanded.SequenceEqual(ExpandMessage.Xmd(message, otherTag, 64));
            }
        );

    [Property]
    public Property A_shorter_expansion_is_not_a_prefix_of_a_longer_one() =>
        Prop.ForAll(
            Arb.From(Bytes(0, 64)),
            Arb.From(Gen.Choose(1, 200)),
            Arb.From(Gen.Choose(1, 200)),
            (byte[] message, int shorter, int longer) =>
            {
                // The requested length is hashed into msg_prime, so expansions of different
                // lengths are unrelated. Worth pinning: the obvious "optimisation" is to expand
                // once at the longest length and truncate, and it would be wrong in a way that
                // nothing else in this codebase would detect - the protocol only ever asks for
                // 64 bytes, so every test would still pass.
                if (shorter >= longer)
                {
                    return true;
                }

                return !ExpandMessage
                    .Xmd(message, "test"u8, longer)
                    .Take(shorter)
                    .SequenceEqual(ExpandMessage.Xmd(message, "test"u8, shorter));
            }
        );

    [Property]
    public Property Framing_makes_a_transcript_readable_only_one_way() =>
        Prop.ForAll(
            Arb.From(Bytes(0, 32)),
            Arb.From(Bytes(0, 32)),
            Arb.From(Gen.Choose(0, 255)),
            (byte[] first, byte[] second, int which) =>
            {
                // This is the property length framing exists for. Take the same concatenated
                // bytes and split them somewhere else: the transcript must differ, because
                // otherwise a server could answer one question with the proof for another.
                byte[] joined = [.. first, .. second];
                var at = which % (joined.Length + 1);

                var honest = new Transcript().AddFramed(first).AddFramed(second).Span.ToArray();
                var alternative = new Transcript()
                    .AddFramed(joined.AsSpan(0, at))
                    .AddFramed(joined.AsSpan(at))
                    .Span.ToArray();

                return honest.SequenceEqual(alternative) == (at == first.Length);
            }
        );

    [Property]
    public Property Unblinding_recovers_the_element_that_was_blinded() =>
        Prop.ForAll(
            Arb.From(Bytes(0, 128)),
            (byte[] input) =>
            {
                // The algebraic identity the whole client side rests on: multiplying by the
                // blind and then by its inverse is the identity map on the group.
                var element = VoprfSuite.HashToGroup(input);
                var blind = Ristretto.RandomScalar();

                var blinded = Ristretto.ScalarMultiply(blind, element);
                var inverse = Ristretto.ScalarInverse(blind);

                return Ristretto.ScalarMultiply(inverse, blinded).SequenceEqual(element);
            }
        );

    [Property]
    public Property Hashing_to_the_group_never_lands_on_something_the_server_would_refuse() =>
        Prop.ForAll(
            Arb.From(Bytes(0, 512)),
            (byte[] input) =>
                // The server rejects the identity and non-canonical encodings before touching the
                // key. If HashToGroup could produce either, some identifier - not necessarily one
                // anybody would think to write a test for - would be unpseudonymizable.
                Ristretto.IsValidElement(VoprfSuite.HashToGroup(input))
        );
}
