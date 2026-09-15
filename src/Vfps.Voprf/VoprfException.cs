namespace Vfps.Voprf;

/// <summary>
/// Thrown when the protocol cannot proceed: a malformed element, an unusable scalar, or an
/// input the encoding cannot carry.
/// </summary>
public class VoprfException : Exception
{
    /// <summary>Creates the exception with a message.</summary>
    public VoprfException(string message)
        : base(message) { }

    /// <summary>Creates the exception with a message and an inner cause.</summary>
    public VoprfException(string message, Exception innerException)
        : base(message, innerException) { }
}

/// <summary>
/// Thrown when a server's proof does not verify against its published public key.
/// </summary>
/// <remarks>
/// This is the failure the verifiable variant exists to produce. It means the answer was
/// not computed with the key the server published - whether through corruption on the wire,
/// the wrong key, or a key chosen for this one client so that its outputs can be told apart
/// from everyone else's. The output is not merely unverified but unusable, so callers must
/// abort rather than fall back.
/// </remarks>
public sealed class VoprfVerificationException : VoprfException
{
    /// <summary>Creates the exception with the default message.</summary>
    public VoprfVerificationException()
        : base(
            "The server's proof did not verify against the published public key: the answer was "
                + "not computed with that key. Discard it - it is not a valid protocol output."
        ) { }
}
