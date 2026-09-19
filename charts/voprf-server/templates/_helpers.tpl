{{/*
Expand the name of the chart.
*/}}
{{- define "voprf-server.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" }}
{{- end }}

{{/*
Create a default fully qualified app name.
*/}}
{{- define "voprf-server.fullname" -}}
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

{{- define "voprf-server.chart" -}}
{{- printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" | trunc 63 | trimSuffix "-" }}
{{- end }}

{{- define "voprf-server.labels" -}}
helm.sh/chart: {{ include "voprf-server.chart" . }}
{{ include "voprf-server.selectorLabels" . }}
{{- if .Chart.AppVersion }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
{{- end }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
app.kubernetes.io/part-of: vfps
{{- end }}

{{- define "voprf-server.selectorLabels" -}}
app.kubernetes.io/name: {{ include "voprf-server.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end }}

{{- define "voprf-server.serviceAccountName" -}}
{{- if .Values.serviceAccount.create }}
{{- default (include "voprf-server.fullname" .) .Values.serviceAccount.name }}
{{- else }}
{{- default "default" .Values.serviceAccount.name }}
{{- end }}
{{- end }}

{{/*
The Secret holding the key material, whether this chart created it or it already existed.
*/}}
{{- define "voprf-server.keySecretName" -}}
{{- if .Values.key.existingSecret }}
{{- .Values.key.existingSecret }}
{{- else }}
{{- printf "%s-key" (include "voprf-server.fullname" .) }}
{{- end }}
{{- end }}

{{/*
Refuses configurations that would start but be wrong, or not start at all - with a message
saying what to set, rather than leaving it to a CrashLoopBackOff and a container log.
*/}}
{{- define "voprf-server.validateValues" -}}
{{- if and .Values.hardening.enabled (eq .Values.key.source "Ephemeral") }}
{{- fail "key.source=Ephemeral regenerates the key on every restart, which silently invalidates every pseudonym already issued. It is refused while hardening.enabled is true." }}
{{- end }}
{{- if and (ne .Values.key.source "Ephemeral") (not .Values.key.existingSecret) (not .Values.key.value) }}
{{- fail "Set either key.existingSecret (preferred) or key.value, or use key.source=Ephemeral for a throwaway deployment." }}
{{- end }}
{{- if and .Values.hardening.enabled .Values.hardening.requireTls (not .Values.tls.existingSecret) }}
{{- fail "hardening.requireTls is true but tls.existingSecret is unset: Kestrel would have no certificate to serve. Point it at a TLS secret (cert-manager's tls.crt/tls.key layout), or set hardening.requireTls=false when a mesh or ingress terminates TLS in front of this pod." }}
{{- end }}
{{- if and .Values.hardening.enabled .Values.hardening.requireAuthentication (eq .Values.auth.mode "Jwt") (not .Values.auth.jwt.authority) }}
{{- fail "auth.mode=Jwt requires auth.jwt.authority." }}
{{- end }}
{{- if and .Values.hardening.enabled .Values.hardening.requireAuthentication (eq .Values.auth.mode "ClientCertificate") (not .Values.hardening.requireTls) }}
{{- fail "auth.mode=ClientCertificate needs hardening.requireTls=true: a client certificate is presented during the TLS handshake, so over plaintext no caller could ever authenticate and every request would be refused. Turn requireTls back on, or use auth.mode=Jwt, which a mesh terminating TLS in front of the pod can carry." }}
{{- end }}
{{- if not .Values.hardening.enabled }}
{{- if ne .Values.environment "Development" }}
{{- fail "hardening.enabled=false serves the key over plaintext to unauthenticated callers. The server refuses to start that way unless environment is Development - set both deliberately, or leave hardening on." }}
{{- end }}
{{- end }}
{{- end }}
