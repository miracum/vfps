namespace Vfps.Voprf.Server.Config;

/// <summary>
/// Everything this server needs, under the <c>VoprfServer</c> configuration section.
/// </summary>
/// <remarks>
/// Every default here is the safe one. A server started with no configuration at all refuses
/// to come up rather than quietly serving an ephemeral key over plaintext - see
/// <see cref="HardeningConfig"/>.
/// </remarks>
public class VoprfServerConfig
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "VoprfServer";

    /// <summary>
    /// The key this server evaluates under. Exactly one - a deployment running two generations
    /// at once runs two servers; see the rotation section of the README.
    /// </summary>
    public KeyConfig Key { get; set; } = new();

    /// <summary>The protections that make this safe to expose.</summary>
    public HardeningConfig Hardening { get; set; } = new();

    /// <summary>How callers prove who they are.</summary>
    public AuthenticationConfig Authentication { get; set; } = new();

    /// <summary>
    /// Most blinded elements accepted in one call. Each one costs a scalar multiplication, so
    /// an unbounded batch is a cheap way to spend the server's CPU; the proof is over the whole
    /// batch either way, so a caller gains nothing from an enormous one.
    /// </summary>
    public int MaxBatchSize { get; set; } = 128;
}

/// <summary>
/// Where the private key is read from. The key is the whole secret: anyone holding it can
/// compute every pseudonym this deployment has ever issued.
/// </summary>
public class KeyConfig
{
    /// <summary>Which of the fields below is used.</summary>
    public KeySource Source { get; set; } = KeySource.Seed;

    /// <summary>
    /// Path to the file holding the secret: the seed for <see cref="KeySource.Seed"/>, the key
    /// itself for <see cref="KeySource.File"/>. Raw bytes or base64, with trailing whitespace
    /// tolerated - what a Kubernetes secret mount and <c>openssl rand</c> both produce.
    /// </summary>
    /// <remarks>
    /// For the default <see cref="KeySource.Seed"/>, generate it with
    /// <c>openssl rand -out voprf.seed 32</c>; any 32 random bytes are a valid seed.
    /// </remarks>
    /// <remarks>
    /// Preferred over <see cref="Base64"/>: a mounted file does not appear in the process
    /// environment, in <c>kubectl describe</c>, or in a crash dump of the configuration tree.
    /// </remarks>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// The key inline, base64 encoded, for <see cref="KeySource.Base64"/> and
    /// <see cref="KeySource.Seed"/>. Convenient for an environment variable; see the caveat on
    /// <see cref="FilePath"/>.
    /// </summary>
    public string Base64 { get; set; } = string.Empty;

    /// <summary>
    /// Public label separating this key from others derived from the same seed, for
    /// <see cref="KeySource.Seed"/> - a tenant, a column, a key generation. Not a secret.
    /// </summary>
    public string KeyInfo { get; set; } = string.Empty;

    /// <summary>
    /// Names the generation of the key this server holds. Returned alongside every answer so a
    /// store of pseudonyms can record which key produced them.
    /// </summary>
    /// <remarks>
    /// Must change whenever the key material behind it changes. Two generations sharing an id
    /// leaves a store with no way to say which one produced a given pseudonym, which is the one
    /// thing that makes a migration unrecoverable - and no server can catch that for you, since
    /// each one only ever sees its own key.
    /// </remarks>
    public string KeyId { get; set; } = "v1";
}

/// <summary>Where <see cref="KeyConfig"/> reads the key from.</summary>
public enum KeySource
{
    /// <summary>Read the key from <see cref="KeyConfig.FilePath"/>.</summary>
    File,

    /// <summary>Read the key from <see cref="KeyConfig.Base64"/>.</summary>
    Base64,

    /// <summary>
    /// Derive the key from a seed (RFC 9497 <c>DeriveKeyPair</c>) held in
    /// <see cref="KeyConfig.FilePath"/> or <see cref="KeyConfig.Base64"/>, labelled with
    /// <see cref="KeyConfig.KeyInfo"/>. The default, and the one to reach for.
    /// </summary>
    /// <remarks>
    /// A private key is a scalar below the group order, which no ordinary tool emits: the order
    /// sits just above 2^252, so about fifteen of every sixteen values <c>openssl rand 32</c>
    /// produces are out of range, and neither OpenSSL nor <c>wg genkey</c> can generate one
    /// (OpenSSL has no ristretto255 at all, and WireGuard's X25519 clamping lands far above the
    /// order). A <em>seed</em> has no such constraint: any 32 random bytes will do, and
    /// <c>DeriveKeyPair</c> is the specification's own way of turning them into a key.
    ///
    /// It also makes rotation cheap - a new <see cref="KeyConfig.KeyInfo"/> against the same
    /// seed is a new, unrelated key - and lets one seed in a secret store back several.
    /// </remarks>
    Seed,

    /// <summary>
    /// Generate a fresh key at startup. Every restart invalidates every pseudonym issued
    /// before it, so this is a development convenience and is refused unless
    /// <see cref="HardeningConfig.IsEnabled"/> is false.
    /// </summary>
    Ephemeral,
}

/// <summary>
/// The protections that make the service safe to expose, and the single switch that turns them
/// off for local development.
/// </summary>
/// <remarks>
/// <para>
/// This server is an oracle: it answers with the key to whoever can reach it. That is fine -
/// evaluation reveals nothing about any input - but it means anyone who can call it can
/// pseudonymize anything, and a pseudonym is only as private as the key behind it. Reaching the
/// service at all is therefore the security boundary, which is why authentication and transport
/// security are on by default rather than opt-in.
/// </para>
/// <para>
/// Setting <see cref="IsEnabled"/> to false relaxes all of it at once. The application refuses
/// to start that way outside the Development environment, so an insecure configuration cannot
/// reach production by being forgotten - it has to be carried there deliberately, along with
/// the environment setting.
/// </para>
/// </remarks>
public class HardeningConfig
{
    /// <summary>
    /// Whether the protections below are enforced. Development only when false; startup fails
    /// otherwise.
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Reject requests that did not arrive over TLS. Leave this on even behind a mesh or an
    /// ingress that terminates TLS for you unless the hop between it and this process is itself
    /// trusted - the blinded elements are not secret, but the answers are what a caller is
    /// authenticated to receive.
    /// </summary>
    public bool RequireTls { get; set; } = true;

    /// <summary>Reject unauthenticated callers. See <see cref="AuthenticationConfig"/>.</summary>
    public bool RequireAuthentication { get; set; } = true;

    /// <summary>
    /// Serve gRPC server reflection. Off by default: it enumerates the service surface to
    /// anyone who can connect, which is a convenience for <c>grpcurl</c> and a map for everyone
    /// else.
    /// </summary>
    public bool EnableReflection { get; set; }

    /// <summary>
    /// Return exception detail to callers. Off by default - a failure that distinguishes "not a
    /// valid element" from "not a canonical encoding" tells a caller probing the group more
    /// than it needs to know.
    /// </summary>
    public bool EnableDetailedErrors { get; set; }

    /// <summary>Largest request accepted, in bytes.</summary>
    /// <remarks>
    /// A batch of <see cref="VoprfServerConfig.MaxBatchSize"/> 32-byte elements is a few
    /// kilobytes; the default leaves generous headroom while keeping a single call from
    /// buffering something enormous.
    /// </remarks>
    public int MaxReceiveMessageSizeBytes { get; set; } = 64 * 1024;

    /// <summary>How many calls a single caller may make, and how often.</summary>
    public RateLimitConfig RateLimit { get; set; } = new();
}

/// <summary>
/// A fixed-window limit per authenticated caller, so one client cannot exhaust the service for
/// everyone else - and so that a caller enumerating a guessable input space has to do it slowly
/// enough to be noticed.
/// </summary>
public class RateLimitConfig
{
    /// <summary>Whether the limiter is applied at all.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>Calls allowed per caller per <see cref="Window"/>.</summary>
    /// <remarks>
    /// This counts calls, not elements: a caller batching the maximum gets far more evaluations
    /// than one sending them singly, which is deliberate - batching is the behaviour worth
    /// encouraging, since it costs one proof instead of many.
    /// </remarks>
    public int PermitsPerWindow { get; set; } = 600;

    /// <summary>Length of the window.</summary>
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How many calls may wait for the next window rather than being rejected outright. Zero
    /// fails fast, which is usually what a caller with a retry policy wants.
    /// </summary>
    public int QueueLimit { get; set; }
}

/// <summary>How callers prove who they are.</summary>
public class AuthenticationConfig
{
    /// <summary>Which scheme is used.</summary>
    public AuthenticationMode Mode { get; set; } = AuthenticationMode.ClientCertificate;

    /// <summary>Settings for <see cref="AuthenticationMode.ClientCertificate"/>.</summary>
    public ClientCertificateConfig ClientCertificate { get; set; } = new();

    /// <summary>Settings for <see cref="AuthenticationMode.Jwt"/>.</summary>
    public JwtConfig Jwt { get; set; } = new();
}

/// <summary>The supported authentication schemes.</summary>
public enum AuthenticationMode
{
    /// <summary>
    /// Mutual TLS. The natural fit for a service called by other services: no identity provider
    /// to depend on, and the caller's identity is established before the first byte of the
    /// request body.
    /// </summary>
    ClientCertificate,

    /// <summary>
    /// OIDC bearer tokens, matching the scheme the main vfps service already uses. Choose this
    /// when callers already hold tokens from the same issuer.
    /// </summary>
    Jwt,
}

/// <summary>Mutual TLS settings.</summary>
public class ClientCertificateConfig
{
    /// <summary>
    /// SHA-256 thumbprints of the client certificates allowed to call, hex encoded and case
    /// insensitive. Empty accepts any certificate that chains to a trusted root, which is only
    /// as narrow as that root - name a thumbprint per caller unless the root exists solely to
    /// issue certificates for this service.
    /// </summary>
    public List<string> AllowedThumbprints { get; set; } = [];

    /// <summary>
    /// Whether to check certificate revocation online. Off by default because an unreachable
    /// CRL or OCSP responder otherwise takes the service down with it; turn it on where the
    /// responder is as available as this service needs to be.
    /// </summary>
    public bool CheckRevocation { get; set; }
}

/// <summary>OIDC bearer token settings.</summary>
public class JwtConfig
{
    /// <summary>The OIDC issuer/authority, for example a Keycloak realm URL.</summary>
    public string Authority { get; set; } = string.Empty;

    /// <summary>Audience the token must carry.</summary>
    public string Audience { get; set; } = string.Empty;

    /// <summary>
    /// Scope a token must carry to call <c>BlindEvaluate</c>, or empty to accept any valid
    /// token from the authority. A dedicated scope is what keeps every token the issuer hands
    /// out - including ones minted for unrelated applications - from also being a licence to
    /// use this key.
    /// </summary>
    public string RequiredScope { get; set; } = string.Empty;
}
