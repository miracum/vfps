# Encrypting the connection to PostgreSQL

Nothing here sets `SSL Mode` by default, which leaves Npgsql's own default in place: it encrypts
opportunistically, verifies nothing, and **silently falls back to plaintext** against a server
that offers no TLS. A misconfigured server downgrades the connection with no error and no log
line, so this is worth setting explicitly rather than assuming.

The Helm chart configures it through `database.tls`, which applies to every component that opens a
database connection - the API Deployment, the worker Deployment, and the migrations Job:

```yaml
database:
  tls:
    mode: VerifyFull
    certificateAuthority:
      existingSecret:
        name: "vfps-db-ca"
        key: "ca.crt"
```

`VerifyFull` verifies the server's certificate chain _and_ that its name matches the host being
connected to. `VerifyCA` verifies only the chain; every other mode verifies nothing. The CA secret
can be omitted when the server's certificate is signed by a CA already in the container's trust
store - typical for a managed cloud database, never the case for a cluster-internal one. Setting
`SSL Mode` in `database.additionalConnectionStringParameters` as well is rejected at template
time, so the connection string and the mounted certificates cannot drift apart.

## CloudNativePG

A [CloudNativePG](https://cloudnative-pg.io/) `Cluster` serves TLS out of the box and issues its
own CA, so there is nothing to enable on the database side - only a trust anchor to hand over.
The operator creates these Secrets per cluster:

| Secret             | Contents                                                | Used for                                        |
| ------------------ | ------------------------------------------------------- | ----------------------------------------------- |
| `<cluster>-ca`     | `ca.crt`, `ca.key`                                      | `database.tls.certificateAuthority`             |
| `<cluster>-server` | `tls.crt`, `tls.key`                                    | the server's own identity; vfps never reads it  |
| `<cluster>-app`    | `username`, `password`, `dbname`, `host`, `port`, `uri` | `database.username` / `database.existingSecret` |

```yaml
database:
  host: "vfps-db-rw" # the cluster's read-write Service
  port: 5432
  database: "app"
  username: "app" # must match the -app Secret's `username`
  existingSecret: "vfps-db-app"
  existingSecretKey: "password"
  tls:
    mode: VerifyFull
    certificateAuthority:
      existingSecret:
        name: "vfps-db-ca"
```

The chart projects `ca.crt` out of that Secret key by key rather than mounting it whole. This is
deliberate: `<cluster>-ca` also contains `ca.key`, the key that signs every certificate in the
cluster, and mounting the Secret wholesale would hand the vfps pod the ability to mint credentials
for its own database.

**Check the certificate's SAN before rolling out.** `VerifyFull` matches the host you connect to
against the server certificate's Subject Alternative Names, and a mismatch fails the handshake
while the chain itself verifies perfectly - a confusing failure if you are not expecting it:

```sh
kubectl get secret vfps-db-server -o jsonpath='{.data.tls\.crt}' | base64 -d \
  | openssl x509 -noout -ext subjectAltName
```

Set `database.host` to a name that actually appears there. If the form your DNS resolves is not
listed, either use one that is, or drop to `VerifyCA` - which still verifies the chain, and inside
a cluster whose DNS you trust is a defensible trade, though it does concede an attacker who can
both spoof DNS and obtain a certificate from that CA.

## Mutual TLS

PostgreSQL can authenticate clients by certificate instead of by password, which removes the
database password from the deployment entirely. Point `database.tls.clientCertificate` at a Secret
holding a client certificate and key:

```yaml
database:
  tls:
    mode: VerifyFull
    certificateAuthority:
      existingSecret:
        name: "vfps-db-ca"
    clientCertificate:
      existingSecret:
        name: "vfps-db-client"
        certificateKey: "tls.crt"
        privateKeyKey: "tls.key"
```

Two requirements are easy to miss:

- **The certificate's Common Name must equal the database role** vfps connects as
  (`database.username`), because that CN is what PostgreSQL authenticates as the user. A
  certificate with any other CN is rejected as the wrong user rather than as an invalid
  certificate.
- **The private key must not be passphrase-protected.** Npgsql takes the passphrase from the
  connection string, which the chart renders as a plain value in the pod spec - so a passphrase
  there would be readable by anyone who can read the Deployment.

## Generating a client certificate

The certificate has to be signed by a CA the server trusts for client authentication. For
CloudNativePG that is the cluster CA (`<cluster>-ca`), which the operator also uses as the client
CA unless `.spec.certificates.clientCASecret` says otherwise.

The `cnpg` kubectl plugin does the whole thing in one command, and is the way to prefer - it signs
with the cluster's own client CA and writes a ready-to-mount `kubernetes.io/tls` Secret:

```sh
kubectl cnpg certificate vfps-db-client \
  --cnpg-cluster vfps-db \
  --cnpg-user app \
  -n vfps
```

`--cnpg-user` is what ends up as the certificate's CN, so it must be the database role - the same
value as `database.username`. Check `kubectl cnpg certificate --help` for the flags your plugin
version accepts.

Without the plugin - or against a PostgreSQL that is not CloudNativePG - sign one with `openssl`.
This extracts the CA's **private key**, so do it somewhere you are willing to have that key exist,
and remove it afterwards:

```sh
kubectl get secret vfps-db-ca -o jsonpath='{.data.ca\.crt}' | base64 -d > ca.crt
kubectl get secret vfps-db-ca -o jsonpath='{.data.ca\.key}' | base64 -d > ca.key

# CN must be the database role vfps connects as; the key stays unencrypted on purpose
openssl genrsa -out tls.key 2048
openssl req -new -key tls.key -out client.csr -subj "/CN=app"
openssl x509 -req -in client.csr -CA ca.crt -CAkey ca.key -CAcreateserial \
  -out tls.crt -days 365 \
  -extfile <(printf "extendedKeyUsage=clientAuth")

kubectl create secret tls vfps-db-client --cert=tls.crt --key=tls.key -n vfps
shred -u ca.key client.csr
```

Verify before rolling it out - `openssl verify` catches a wrong CA or a missing client-auth
extension without having to watch a pod fail:

```sh
openssl verify -CAfile ca.crt -purpose sslclient tls.crt
openssl x509 -in tls.crt -noout -subject     # must print the database role
```

Renewal is a re-run of the same command plus a pod restart, so keep `-days` short enough to stay
honest and long enough not to page anyone. The plugin's certificates and the manual ones expire
silently - the failure mode is a pod that stops being able to connect, so it is worth an alert on
the certificate's expiry rather than discovering it during an unrelated rollout.

## Making the server require it

A client certificate that the server never asks for is verified as part of the TLS handshake and
then simply not used to authenticate - so the password stays live and nothing has actually been
tightened. The `pg_hba` rule is what makes it the credential. On a CloudNativePG cluster:

```yaml
spec:
  postgresql:
    pg_hba:
      - hostssl app app all cert
```

## A note on the migrations Job

Two implementation details, in case a change to the Job ever surprises you. Neither needs
configuring - `database.tls` drives both - but both are non-obvious enough to be worth writing
down.

The Job's second container runs `psql` to print the resulting schema, and libpq reads its own
environment rather than the connection string, so the chart derives `PGSSLMODE`/`PGSSLROOTCERT`/
`PGSSLCERT` from `database.tls` - spelling the modes libpq's way (`verify-full`, `verify-ca`)
rather than Npgsql's.

And libpq refuses a private key readable by group or world, which every Secret mount is: the files
are owned by root while the container runs as a non-root user, so the mode cannot simply be
tightened. The container therefore copies the key to a `0600` file and points libpq at the copy -
which is why it receives `VFPS_PGSSLKEY_SOURCE` rather than `PGSSLKEY` directly. Npgsql enforces
no such rule, so the application and the migration itself read the mounted key as-is.
