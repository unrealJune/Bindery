# Bindery Helm chart

The chart installs the Bindery host and renders the plugin registry it reads at boot.
Plugins are containers that speak HTTP. Sidecars are reached on localhost; service-mode
plugins get their own Deployment and Service. Neither shape shares `/data` or `/library`
with Bindery.

```yaml
plugins:
  - name: fanficfare
    enabled: true
    mode: sidecar                 # sidecar | service
    image: ghcr.io/OWNER/bindery-plugin-fanficfare:0.1.0
    port: 8080
    resources: { limits: { memory: 512Mi } }
    configSecret: fanficfare-creds
```

## Admins and members

Everyone who signs in has a private shelf; admins also manage plugins, shared plugin
settings, and rescans. Name them by an OIDC claim:

```yaml
bindery:
  auth:
    scopes: ["openid", "profile", "email", "groups"]   # if your provider needs a scope for it
    adminClaim: groups
    adminValues: ["bindery-admins"]
```

Leave `adminValues` empty and the first person who ever signed in is the admin. Groups are
read at sign-in, so removing someone from the admin group applies when they next sign in.

Secrets stay in Kubernetes Secrets, not in values. Set `bindery.plugins.tokenSecret.existingSecret`
for the shared host-to-plugin bearer token and `bindery.oidc.clientSecret.existingSecret`
for the OIDC client secret. If no plugin token Secret is supplied, the chart creates one.
