{{- define "bindery.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "bindery.fullname" -}}
{{- if .Values.fullnameOverride -}}
{{- .Values.fullnameOverride | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- $name := default .Chart.Name .Values.nameOverride -}}
{{- if contains $name .Release.Name -}}
{{- .Release.Name | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- printf "%s-%s" .Release.Name $name | trunc 63 | trimSuffix "-" -}}
{{- end -}}
{{- end -}}
{{- end -}}

{{- define "bindery.chart" -}}
{{- printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "bindery.labels" -}}
helm.sh/chart: {{ include "bindery.chart" . }}
{{ include "bindery.selectorLabels" . }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
{{- end -}}

{{- define "bindery.selectorLabels" -}}
app.kubernetes.io/name: {{ include "bindery.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end -}}

{{- define "bindery.serviceAccountName" -}}
{{- if .Values.serviceAccount.create -}}
{{- default (include "bindery.fullname" .) .Values.serviceAccount.name -}}
{{- else -}}
{{- default "default" .Values.serviceAccount.name -}}
{{- end -}}
{{- end -}}

{{- define "bindery.pluginTokenSecretName" -}}
{{- default (printf "%s-plugin-token" (include "bindery.fullname" .)) .Values.bindery.plugins.tokenSecret.existingSecret -}}
{{- end -}}

{{- define "bindery.dataClaimName" -}}
{{- default (printf "%s-data" (include "bindery.fullname" .)) .Values.persistence.data.existingClaim -}}
{{- end -}}

{{- define "bindery.libraryClaimName" -}}
{{- default (printf "%s-library" (include "bindery.fullname" .)) .Values.persistence.library.existingClaim -}}
{{- end -}}

{{- define "bindery.pluginServiceName" -}}
{{- printf "%s-plugin-%s" (include "bindery.fullname" .root) .plugin.name | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{/*
The claim backing a plugin's /work.

Most plugins want nothing here: /work is scratch for in-flight artifacts and an emptyDir is
correct. A plugin that keeps durable state — hedgerow keeps ingested chapters and their
source attribution in SQLite — must set `persistence.enabled`, or a pod restart silently
throws that state away.
*/}}
{{- define "bindery.pluginWorkClaimName" -}}
{{- default (printf "%s-plugin-%s-work" (include "bindery.fullname" .root) .plugin.name | trunc 63 | trimSuffix "-") (default dict .plugin.persistence).existingClaim -}}
{{- end -}}
