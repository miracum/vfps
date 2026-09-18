{{/*
Expand the name of the chart.
*/}}
{{- define "vfps.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" }}
{{- end }}

{{/*
Create a default fully qualified app name.
We truncate at 63 chars because some Kubernetes name fields are limited to this (by the DNS naming spec).
If release name contains chart name it will be used as a full name.
*/}}
{{- define "vfps.fullname" -}}
{{- if .Values.fullnameOverride }}
{{- .Values.fullnameOverride | trunc 63 | trimSuffix "-" }}
{{- else }}
{{- $name := default .Chart.Name .Values.nameOverride }}
{{- if contains $name .Release.Name }}
{{- .Release.Name | trunc 63 | trimSuffix "-" }}
{{- else }}
{{- printf "%s-%s" .Release.Name $name | trunc 63 | trimSuffix "-" }}
{{- end }}
{{- end }}
{{- end }}

{{/*
Create chart name and version as used by the chart label.
*/}}
{{- define "vfps.chart" -}}
{{- printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" | trunc 63 | trimSuffix "-" }}
{{- end }}

{{/*
Common labels
*/}}
{{- define "vfps.labels" -}}
helm.sh/chart: {{ include "vfps.chart" . }}
{{ include "vfps.selectorLabels" . }}
{{- if .Chart.AppVersion }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
{{- end }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
{{- end }}

{{/*
Selector labels
*/}}
{{- define "vfps.selectorLabels" -}}
app.kubernetes.io/name: {{ include "vfps.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end }}

{{/*
Create the name of the service account to use
*/}}
{{- define "vfps.serviceAccountName" -}}
{{- if (or .Values.serviceAccount.create .Values.migrationsJob.enabled) }}
{{- default (include "vfps.fullname" .) .Values.serviceAccount.name }}
{{- else }}
{{- default "default" .Values.serviceAccount.name }}
{{- end }}
{{- end }}

{{/*
Create the name of the service account to use
*/}}
{{- define "vfps.migrationsJob.serviceAccountName" -}}
{{- if .Values.migrationsJob.serviceAccount.create }}
{{- default (include "vfps.migrationsJob.resourceName" .) .Values.migrationsJob.serviceAccount.name }}
{{- else }}
{{- default "default" .Values.migrationsJob.serviceAccount.name }}
{{- end }}
{{- end }}

{{/*
Get the container image for the wait-for-db init container
*/}}
{{- define "vfps.waitForDatabaseInitContainerImage" -}}
{{- $registry := .Values.waitForDatabaseInitContainer.image.registry -}}
{{- $repository := .Values.waitForDatabaseInitContainer.image.repository -}}
{{- $tag := .Values.waitForDatabaseInitContainer.image.tag -}}
{{ printf "%s/%s:%s" $registry $repository $tag}}
{{- end -}}

{{/*
Create a default fully qualified postgresql name. TODO: could we use SubChart template rendering to render this?
We truncate at 63 chars because some Kubernetes name fields are limited to this (by the DNS naming spec).
*/}}
{{- define "vfps.postgresql.fullname" -}}
{{- $name := default "postgres" .Values.postgres.nameOverride -}}
{{- printf "%s-%s" .Release.Name $name | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{/*
Add environment variables to configure database values
*/}}
{{- define "vfps.database.host" -}}
{{- ternary (include "vfps.postgresql.fullname" .) .Values.database.host .Values.postgres.enabled -}}
{{- end -}}

{{/*
Add environment variables to configure database values
*/}}
{{- define "vfps.database.user" -}}
{{- if .Values.postgres.enabled -}}
    {{- if .Values.postgres.auth.username -}}
        {{ .Values.postgres.auth.username | quote }}
    {{- else -}}
        {{ "postgres" }}
    {{- end -}}
{{- else -}}
    {{ .Values.database.username}}
{{- end -}}
{{- end -}}

{{/*
Add environment variables to configure database values
*/}}
{{- define "vfps.database.name" -}}
{{- ternary .Values.postgres.auth.database .Values.database.database .Values.postgres.enabled -}}
{{- end -}}

{{/*
Add environment variables to configure database values
*/}}
{{- define "vfps.database.port" -}}
{{- ternary "5432" .Values.database.port .Values.postgres.enabled -}}
{{- end -}}

{{/*
Get the name of the secret containing the DB password
*/}}
{{- define "vfps.database.db-secret-name" -}}
{{- if .Values.postgres.enabled -}}
    {{- if .Values.postgres.auth.existingSecret -}}
        {{ .Values.postgres.auth.existingSecret | quote }}
    {{- else -}}
        {{ ( include "vfps.postgresql.fullname" . ) }}
    {{- end -}}
{{- else if .Values.database.existingSecret -}}
    {{ .Values.database.existingSecret | quote }}
{{- else -}}
    {{- $fullname := ( include "vfps.fullname" . ) -}}
    {{ printf "%s-%s" $fullname "db-secret" }}
{{- end -}}
{{- end -}}

{{/*
Get the key inside the secret containing the DB user's password
*/}}
{{- define "vfps.database.db-secret-key" -}}
{{- if .Values.postgres.enabled -}}
    {{- if .Values.postgres.auth.existingSecret -}}
        {{ default "postgres-password" .Values.postgres.auth.secretKeys.passwordKey }}
    {{- else -}}
        {{ "postgres-password" }}
    {{- end -}}
{{- else if .Values.database.existingSecret -}}
    {{ .Values.database.existingSecretKey | quote }}
{{- else -}}
    {{ "postgres-password" }}
{{- end -}}
{{- end -}}

{{/*
The PGPASSWORD environment entry, which Npgsql and libpq both read.

Emits nothing when a client certificate is configured. `cert` authentication in pg_hba.conf
replaces the password rather than supplementing it - the server never issues a password
challenge, so the variable would be a credential that exists only to go unused. Worse, the
chart-created Secret would then be holding `database.password`, whose default is the literal
string "postgres": a weak password that nobody chose, sitting in the namespace, that would
become a real way in the moment the server's pg_hba is loosened.

This is what `database.tls.clientCertificate` already documents ("replaces the password
entirely").
*/}}
{{- define "vfps.database.password-env" -}}
{{- if not .Values.database.tls.clientCertificate.existingSecret.name -}}
- name: PGPASSWORD
  valueFrom:
    secretKeyRef:
      name: {{ include "vfps.database.db-secret-name" . }}
      key: {{ include "vfps.database.db-secret-key" . }}
{{- end -}}
{{- end -}}

{{/*
Create the connection string from the host, port and database name.
*/}}
{{- define "vfps.database.connection-string" -}}
{{- $host := (include "vfps.database.host" .) -}}
{{- $port := (include "vfps.database.port" .) -}}
{{- $databaseName := (include "vfps.database.name" .) -}}
{{- $releaseName := (include "vfps.fullname" .) -}}
{{- $appName := printf "%s" $releaseName -}}
{{- $additionalConnectionStringParameters := printf "%s" .Values.database.additionalConnectionStringParameters -}}
{{- $schema := printf "%s" .Values.database.schema -}}
{{- $tlsParameters := (include "vfps.database.tls.connection-string-parameters" .) -}}
{{ (printf "Host=%s:%d;Database=%s;Application Name=%s;%s%s%s" $host (int $port) $databaseName $appName (empty $schema | ternary "" (printf "Search Path=%s;" $schema)) $additionalConnectionStringParameters $tlsParameters) | quote }}
{{- end -}}

{{/*
get the version of the image tag to use as an identifier. Default to .Chart.Version to handle things like tags used for testing.
*/}}
{{- define "vfps.migrationsJob.versionIdentifier" -}}
{{- $imageTagVersion := (default .Chart.Version (first (splitList "@" .Values.image.tag))) | replace "." "-" -}}
{{- $imageTagVersion }}
{{- end -}}

{{/*
get the name of the migrations Job resource
*/}}
{{- define "vfps.migrationsJob.resourceName" -}}
{{ include "vfps.fullname" . }}-migrations-{{ include "vfps.migrationsJob.versionIdentifier" . }}
{{- end -}}

{{/*
Name of the secret holding the S3 credentials
*/}}
{{- define "vfps.s3.secret-name" -}}
{{- if .Values.s3.existingSecret.name -}}
{{ .Values.s3.existingSecret.name }}
{{- else -}}
{{ include "vfps.fullname" . }}-s3-secret
{{- end -}}
{{- end -}}

{{/*
Keys within the S3 credentials secret. The configurable names apply only to an existing secret; the
chart-created one always uses the defaults, so these settings can't accidentally point at keys that
were never written. Both paths use the same names, so a secret shaped like the one this chart
creates works as an existing secret with no further configuration.
*/}}
{{- define "vfps.s3.access-key-key" -}}
{{- ternary .Values.s3.existingSecret.accessKeyIdKey "AWS_ACCESS_KEY_ID" (not (empty .Values.s3.existingSecret.name)) -}}
{{- end -}}

{{- define "vfps.s3.secret-key-key" -}}
{{- ternary .Values.s3.existingSecret.secretAccessKeyKey "AWS_SECRET_ACCESS_KEY" (not (empty .Values.s3.existingSecret.name)) -}}
{{- end -}}

{{/*
S3 configuration as environment variables, shared by the API and worker Deployments (the
migrations job never touches object storage). Emits nothing at all when s3.enabled is false, so a
deployment still configuring S3 through extraEnv keeps working unchanged.
*/}}
{{- define "vfps.s3.env" -}}
{{- if .Values.s3.enabled -}}
{{- if not .Values.s3.bucket }}
{{- fail "s3.enabled requires s3.bucket to be set" }}
{{- end }}
{{- if not .Values.s3.serviceUrl }}
{{- fail "s3.enabled requires s3.serviceUrl to be set (the S3-compatible endpoint URL)" }}
{{- end }}
{{- if not (or .Values.s3.existingSecret.name (and .Values.s3.accessKey .Values.s3.secretKey)) }}
{{- fail "s3.enabled requires either s3.existingSecret.name, or both s3.accessKey and s3.secretKey" }}
{{- end }}
- name: S3__IsEnabled
  value: "true"
- name: S3__ServiceUrl
  value: {{ .Values.s3.serviceUrl | quote }}
- name: S3__Bucket
  value: {{ .Values.s3.bucket | quote }}
- name: S3__Region
  value: {{ .Values.s3.region | quote }}
- name: S3__ForcePathStyle
  value: {{ .Values.s3.forcePathStyle | quote }}
- name: S3__PresignedUrlExpiry
  value: {{ .Values.s3.presignedUrlExpiry | quote }}
- name: S3__ObjectRetentionDays
  value: {{ .Values.s3.objectRetentionDays | quote }}
{{- range $index, $origin := .Values.s3.allowedOrigins }}
- name: S3__AllowedOrigins__{{ $index }}
  value: {{ $origin | quote }}
{{- end }}
- name: S3__AccessKey
  valueFrom:
    secretKeyRef:
      name: {{ include "vfps.s3.secret-name" . }}
      key: {{ include "vfps.s3.access-key-key" . }}
- name: S3__SecretKey
  valueFrom:
    secretKeyRef:
      name: {{ include "vfps.s3.secret-name" . }}
      key: {{ include "vfps.s3.secret-key-key" . }}
{{- end -}}
{{- end -}}

{{/*
Environment variables pointing the application at the mounted key-protection certificates.

The first entry encrypts newly created keys and every entry can decrypt existing ones, so the
list order is load-bearing during a rotation - see DataProtectionConfig.Certificates.

Applied to the worker Deployment as well as the API one: the worker shares the key ring and will
create a key in it if the ring is empty or fully expired, so a worker without the certificate
would silently write an unencrypted key into an otherwise encrypted ring.
*/}}
{{- define "vfps.dataProtection.env" -}}
{{- range $index, $certificate := .Values.dataProtection.certificates }}
{{- if not $certificate.existingSecret.name }}
{{- fail (printf "dataProtection.certificates[%d] requires existingSecret.name" $index) }}
{{- end }}
- name: DataProtection__Certificates__{{ $index }}__Path
  value: "/etc/vfps/dataprotection/{{ $index }}/{{ $certificate.existingSecret.certificateKey | default "tls.crt" }}"
{{- with $certificate.existingSecret.privateKeyKey }}
- name: DataProtection__Certificates__{{ $index }}__KeyPath
  value: "/etc/vfps/dataprotection/{{ $index }}/{{ . }}"
{{- end }}
{{- with $certificate.existingSecret.passwordKey }}
- name: DataProtection__Certificates__{{ $index }}__Password
  valueFrom:
    secretKeyRef:
      name: {{ $certificate.existingSecret.name | quote }}
      key: {{ . | quote }}
{{- end }}
{{- end }}
{{- end -}}

{{/*
volumeMounts for the key-protection certificates. One directory per configured certificate, so a
rotation that keeps two around never has to reconcile two Secrets with the same file names.
*/}}
{{- define "vfps.dataProtection.volumeMounts" -}}
{{- range $index, $certificate := .Values.dataProtection.certificates }}
- name: dataprotection-cert-{{ $index }}
  mountPath: "/etc/vfps/dataprotection/{{ $index }}"
  readOnly: true
{{- end }}
{{- end -}}

{{/*
volumes for the key-protection certificates.

defaultMode is 0444 rather than something tighter because the container runs as a non-root user
(securityContext.runAsUser) while Secret volumes are owned by root unless a pod-level fsGroup is
set - 0400 would leave the application unable to read its own certificate. Nothing is given away
by the wider mode: every process in the container is the application.
*/}}
{{- define "vfps.dataProtection.volumes" -}}
{{- range $index, $certificate := .Values.dataProtection.certificates }}
- name: dataprotection-cert-{{ $index }}
  secret:
    secretName: {{ $certificate.existingSecret.name | quote }}
    defaultMode: 0444
{{- end }}
{{- end -}}

{{/*
Where the database TLS material is mounted. Constants rather than settings: nothing outside the
chart refers to these paths, and making them configurable would only create a way for the mount
and the connection string to disagree.
*/}}
{{- define "vfps.database.tls.caDir" -}}/etc/vfps/db-tls/ca{{- end -}}
{{- define "vfps.database.tls.clientDir" -}}/etc/vfps/db-tls/client{{- end -}}

{{/*
The TLS portion of the Npgsql connection string, appended to database.additionalConnectionStringParameters.

Emits nothing at all when database.tls.mode is unset, which is the default - existing deployments
keep whatever they already had in additionalConnectionStringParameters.
*/}}
{{- define "vfps.database.tls.connection-string-parameters" -}}
{{- $tls := .Values.database.tls -}}
{{- $mode := $tls.mode | default "" -}}
{{- $caName := $tls.certificateAuthority.existingSecret.name | default "" -}}
{{- $clientName := $tls.clientCertificate.existingSecret.name | default "" -}}
{{- if and (not $mode) (or $caName $clientName) -}}
{{- fail "database.tls.mode must be set when database.tls.certificateAuthority or database.tls.clientCertificate is configured - a certificate has no effect while the connection is unencrypted" -}}
{{- end -}}
{{- if $mode -}}
{{- $valid := list "Disable" "Allow" "Prefer" "Require" "VerifyCA" "VerifyFull" -}}
{{- if not (has $mode $valid) -}}
{{- fail (printf "database.tls.mode must be one of: %s (got %q)" (join ", " $valid) $mode) -}}
{{- end -}}
{{- if contains "ssl mode" (lower .Values.database.additionalConnectionStringParameters) -}}
{{- fail "database.additionalConnectionStringParameters already sets 'SSL Mode' - remove it and use database.tls instead, so the connection string and the mounted certificates cannot disagree" -}}
{{- end -}}
{{- $params := printf "SSL Mode=%s;" $mode -}}
{{- if $caName -}}
{{- $params = printf "%sRoot Certificate=%s/%s;" $params (include "vfps.database.tls.caDir" .) ($tls.certificateAuthority.existingSecret.key | default "ca.crt") -}}
{{- end -}}
{{- if $clientName -}}
{{- $params = printf "%sSSL Certificate=%s/%s;SSL Key=%s/%s;" $params (include "vfps.database.tls.clientDir" .) ($tls.clientCertificate.existingSecret.certificateKey | default "tls.crt") (include "vfps.database.tls.clientDir" .) ($tls.clientCertificate.existingSecret.privateKeyKey | default "tls.key") -}}
{{- end -}}
{{- $params -}}
{{- end -}}
{{- end -}}

{{/*
volumeMounts for the database TLS material - every container that opens a database connection
needs these: the API Deployment, the worker Deployment, and both containers of the migrations Job.
*/}}
{{- define "vfps.database.tls.volumeMounts" -}}
{{- if .Values.database.tls.certificateAuthority.existingSecret.name }}
- name: db-tls-ca
  mountPath: {{ include "vfps.database.tls.caDir" . | quote }}
  readOnly: true
{{- end }}
{{- if .Values.database.tls.clientCertificate.existingSecret.name }}
- name: db-tls-client
  mountPath: {{ include "vfps.database.tls.clientDir" . | quote }}
  readOnly: true
{{- end }}
{{- end -}}

{{/*
volumes for the database TLS material.

Each Secret is projected key by key rather than whole. For a CloudNativePG cluster the CA Secret
also holds `ca.key`, the key that signs every certificate in that cluster - mounting the Secret
wholesale would hand this pod the ability to mint credentials for its own database.

defaultMode 0444 for the same reason as elsewhere in this chart: Secret volumes are owned by root
unless a pod-level fsGroup is set, while the containers run as a non-root user. Npgsql does not
police key file permissions the way libpq does (see the migrations Job, which copies the key to a
0600 temporary file before invoking psql).
*/}}
{{- define "vfps.database.tls.volumes" -}}
{{- $tls := .Values.database.tls }}
{{- if $tls.certificateAuthority.existingSecret.name }}
- name: db-tls-ca
  secret:
    secretName: {{ $tls.certificateAuthority.existingSecret.name | quote }}
    defaultMode: 0444
    items:
      - key: {{ $tls.certificateAuthority.existingSecret.key | default "ca.crt" | quote }}
        path: {{ $tls.certificateAuthority.existingSecret.key | default "ca.crt" | quote }}
{{- end }}
{{- if $tls.clientCertificate.existingSecret.name }}
- name: db-tls-client
  secret:
    secretName: {{ $tls.clientCertificate.existingSecret.name | quote }}
    defaultMode: 0444
    items:
      - key: {{ $tls.clientCertificate.existingSecret.certificateKey | default "tls.crt" | quote }}
        path: {{ $tls.clientCertificate.existingSecret.certificateKey | default "tls.crt" | quote }}
      - key: {{ $tls.clientCertificate.existingSecret.privateKeyKey | default "tls.key" | quote }}
        path: {{ $tls.clientCertificate.existingSecret.privateKeyKey | default "tls.key" | quote }}
{{- end }}
{{- end -}}

{{/*
libpq equivalents of the settings above, for the migrations Job's psql container.

libpq spells the modes differently to Npgsql, and PGSSLKEY is deliberately absent: libpq refuses a
private key readable by group or world, which every Secret mount is, so the container copies it to
a 0600 file and exports PGSSLKEY itself.
*/}}
{{- define "vfps.database.tls.libpq-env" -}}
{{- $tls := .Values.database.tls -}}
{{- if $tls.mode }}
{{- $libpqModes := dict "Disable" "disable" "Allow" "allow" "Prefer" "prefer" "Require" "require" "VerifyCA" "verify-ca" "VerifyFull" "verify-full" }}
- name: PGSSLMODE
  value: {{ get $libpqModes $tls.mode | quote }}
{{- if $tls.certificateAuthority.existingSecret.name }}
- name: PGSSLROOTCERT
  value: "{{ include "vfps.database.tls.caDir" . }}/{{ $tls.certificateAuthority.existingSecret.key | default "ca.crt" }}"
{{- end }}
{{- if $tls.clientCertificate.existingSecret.name }}
- name: PGSSLCERT
  value: "{{ include "vfps.database.tls.clientDir" . }}/{{ $tls.clientCertificate.existingSecret.certificateKey | default "tls.crt" }}"
- name: VFPS_PGSSLKEY_SOURCE
  value: "{{ include "vfps.database.tls.clientDir" . }}/{{ $tls.clientCertificate.existingSecret.privateKeyKey | default "tls.key" }}"
{{- end }}
{{- end }}
{{- end -}}
