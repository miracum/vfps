# Encrypting the Data Protection key ring

The ASP.NET Core Data Protection key ring holds the master keys that encrypt this deployment's
auth cookies and antiforgery tokens. It is persisted to the same PostgreSQL database as the
pseudonyms (see `ConnectionStrings__PostgreSQL` in the [configuration reference](../configuration.md)), and **by default it is stored there
unencrypted** - anyone able to read that database can decrypt or forge a session for any user,
including an admin. The application logs a warning at every startup while this is the case.

Point `DataProtection__Certificates__0__*` at an X.509 certificate to encrypt the ring before it
reaches a row. The certificate has to come from somewhere the database cannot reach - protecting
the keys with a secret the database already holds would be circular - so it is mounted from a
file rather than read from configuration:

```sh
DataProtection__Certificates__0__Path=/etc/vfps/dataprotection/0/tls.crt
DataProtection__Certificates__0__KeyPath=/etc/vfps/dataprotection/0/tls.key
```

Both a PEM pair (what cert-manager produces) and a single PKCS#12 archive work; which one is used
depends on whether `__KeyPath` is set. A bad path, an unreadable file or a certificate without a
private key fails startup rather than falling back to plaintext - a deployment that looks
configured and protects nothing is the one outcome worth refusing outright.

The Helm chart wires this up via `dataProtection.certificates`, which mounts a Secret per entry
and sets the variables above. It applies the certificate to the worker Deployment as well as the
API one: the worker shares the key ring and will create a key in it if the ring is empty or fully
expired, so a worker left unconfigured would silently write an unencrypted key into an otherwise
encrypted ring.

## Generating a certificate

This certificate is never presented to anything and is not a TLS certificate. Data Protection uses
it purely as an RSA keypair: the public key encrypts the key ring on the way into the database, the
private key decrypts it on the next startup. There is no CA, no hostname, no chain validation and
no revocation check - so a self-signed certificate is exactly right here, and reusing your ingress
or database certificate is not.

```sh
openssl req -x509 -newkey rsa:4096 -nodes \
  -keyout tls.key -out tls.crt \
  -days 3650 -subj "/CN=vfps-dataprotection"

kubectl create secret tls vfps-dataprotection --cert=tls.crt --key=tls.key -n vfps
```

```yaml
dataProtection:
  certificates:
    - existingSecret:
        name: "vfps-dataprotection"
        certificateKey: "tls.crt"
        privateKeyKey: "tls.key"
```

Three things that recipe encodes on purpose:

- **`rsa`.** The key ring is encrypted with the XML encryption standard, which wraps its content
  key with RSA. An **ECDSA certificate is accepted at startup and then fails on the first request**
  that needs a cookie, with a `CryptographicException` from deep inside the key ring - so the
  mistake surfaces well away from the thing that caused it.
- **`-nodes`**, meaning no passphrase on the private key. Add one if you want, and name the Secret
  key holding it in `passwordKey`; the chart passes it through
  `DataProtection__Certificates__0__Password`.
- **A long `-days`.** Nothing enforces the validity period: Data Protection does not check it, and
  an expired certificate keeps protecting and unprotecting the key ring indefinitely. That cuts
  both ways - expiry will never cause an outage, and it will never prompt you to rotate either, so
  treat [rotation](#rotation) as something you schedule rather than something the certificate
  reminds you about.

For a single PKCS#12 file instead of a PEM pair, leave `privateKeyKey` empty - the archive carries
its own key:

```sh
openssl pkcs12 -export -inkey tls.key -in tls.crt -out keyring.pfx -passout pass:hunter2

kubectl create secret generic vfps-dataprotection -n vfps \
  --from-file=keyring.pfx=keyring.pfx \
  --from-literal=password=hunter2
```

```yaml
dataProtection:
  certificates:
    - existingSecret:
        name: "vfps-dataprotection"
        certificateKey: "keyring.pfx"
        privateKeyKey: ""
        passwordKey: "password"
```

**Back the private key up somewhere outside this cluster and outside this database.** Losing it
costs everyone a sign-in rather than any data - the key ring only protects auth cookies and
antiforgery tokens, so an unreadable one means new keys and a fresh login, not lost pseudonyms.
But it is unrecoverable in the sense that matters: nothing can decrypt those rows again, and
storing the only copy next to the thing it protects defeats the point of encrypting it.

## Rotation

Index `0` encrypts newly created keys; **every** configured index can decrypt existing ones. To
rotate, prepend the new certificate and leave the outgoing one in place until every key it
protected has aged out of the ring (90 days by default), then drop it. Removing a certificate that
still protects a live key makes that key - and every cookie under it - permanently unreadable.

## Turning it on for an existing deployment

Two things are worth knowing before the first rollout:

- **Existing plaintext keys stay plaintext.** Turning encryption on protects newly created keys;
  it does not rewrite the rows already in `data_protection_keys`. Those remain readable to anyone
  with database access until they expire out of the ring. Deleting them immediately is safe in the
  sense that nothing is lost permanently - it invalidates open sessions, so everyone signs in
  again - and is the only way to close that window early.
- **Everyone signs in again once.** The Data Protection application name is now pinned to a
  constant rather than derived from the container's content root path, so that a change to where
  the application is unpacked can never silently invalidate every cookie. That pinning is itself a
  one-time change of the isolation boundary, so cookies minted by earlier versions stop being
  readable on the first start after upgrading, regardless of whether you configure a certificate.
