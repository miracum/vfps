namespace Vfps.Config;

/// <summary>
/// How the ASP.NET Core Data Protection key ring is protected at rest.
///
/// The key ring holds the master keys that encrypt this deployment's auth cookies and antiforgery
/// tokens, and it is persisted to the same PostgreSQL database as the pseudonyms themselves (see
/// <see cref="Data.DataProtectionKeyContext"/> and the ConnectionStrings:PostgreSQL note in the
/// README). Left unconfigured, those keys are stored as plaintext XML: anyone able to read the
/// database can decrypt or forge cookies for any user, including an admin. Configuring a
/// certificate here encrypts the key ring with it before it ever reaches a row.
///
/// Deliberately not derived from anything already in configuration: protecting keys with a secret
/// that lives in the same database - or with the database password, which the database obviously
/// already knows - would be circular. The certificate has to come from somewhere the database
/// cannot reach.
/// </summary>
public class DataProtectionConfig
{
    /// <summary>
    /// Certificates used to encrypt the key ring. Empty (the default) leaves it unencrypted,
    /// matching this codebase's optional-feature idiom - with a startup warning, since unlike the
    /// other optional features the safe choice here is the one that costs something.
    ///
    /// The <em>first</em> entry encrypts newly created keys; <em>every</em> entry can decrypt
    /// existing ones. That asymmetry is what makes certificate rotation possible without a
    /// flag day: prepend the new certificate, leave the old one in the list until every key it
    /// encrypted has aged out of the ring (90 days by default), then drop it. Removing a
    /// certificate that still protects a live key makes that key - and every cookie under it -
    /// permanently unreadable.
    /// </summary>
    public List<DataProtectionCertificateConfig> Certificates { get; set; } = [];
}

/// <summary>
/// One certificate for <see cref="DataProtectionConfig.Certificates"/>. Both PKCS#12 (a single
/// .pfx/.p12 file) and PEM (separate certificate and key files, as cert-manager and most
/// Kubernetes tooling produce) are supported; which one is used depends on whether
/// <see cref="KeyPath"/> is set.
/// </summary>
public class DataProtectionCertificateConfig
{
    /// <summary>
    /// Path to the certificate. A PKCS#12 archive when <see cref="KeyPath"/> is empty, a PEM
    /// certificate when it isn't.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Path to the PEM private key belonging to <see cref="Path"/>. Leave empty when
    /// <see cref="Path"/> is a PKCS#12 archive, which carries its own key.
    /// </summary>
    public string KeyPath { get; set; } = string.Empty;

    /// <summary>
    /// Password for the PKCS#12 archive, or for an encrypted PEM private key. Empty means the
    /// file is not password-protected - which is the norm for a certificate mounted from a
    /// Kubernetes Secret, where the mount is already the access control.
    /// </summary>
    public string Password { get; set; } = string.Empty;
}
