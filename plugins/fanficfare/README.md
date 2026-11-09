# Bindery plugin: FanFicFare

Wraps [FanFicFare](https://github.com/JimmXinu/FanFicFare) as a Bindery plugin. Paste an
AO3, FFN, SpaceBattles, Royal Road, or one of ~110 other sites' story URLs into Bindery and
this container turns it into an EPUB.

Site support does not guarantee access through a site's bot checks. See
[Royal Road browser checks](#royal-road-browser-checks) for the current limitation.

It implements `docs/PLUGIN-PROTOCOL.md` and knows nothing else about Bindery.

## Running it

```bash
docker build -t bindery-plugin-fanficfare:dev plugins/fanficfare
docker run --rm -p 8080:8080 -e BINDERY_PLUGIN_TOKEN=dev bindery-plugin-fanficfare:dev

curl -H 'Authorization: Bearer dev' localhost:8080/bindery/v1/manifest
python tests/conformance/run.py --base-url http://localhost:8080 --token dev
```

Without Docker:

```bash
pip install -r plugins/fanficfare/requirements.txt
cd plugins/fanficfare
BINDERY_PLUGIN_TOKEN=dev uvicorn app.main:app --port 8080
```

## Environment

| Variable | Default | Meaning |
|---|---|---|
| `BINDERY_PLUGIN_TOKEN` | unset | Shared bearer token. Unset means no auth — dev only, and it warns. |
| `BINDERY_PLUGIN_PORT` | `8080` | Listen port. |
| `BINDERY_PLUGIN_WORKDIR` | `/work` | Writable scratch for in-flight artifacts. |
| `BINDERY_FFF_BIN` | `fanficfare` | Path to the FanFicFare executable. |
| `BINDERY_FFF_META_TIMEOUT` | `120` | Seconds for the metadata pass. |
| `BINDERY_FFF_IDLE_TIMEOUT` | `300` | Seconds of silence before a download is abandoned. |
| `BINDERY_FFF_ALLOW_TEST_SITES` | unset | Claim FanFicFare's four offline test adapters. Used by the test suite. |

Everything a *user* configures — credentials, `personal.ini`, image inclusion — is declared
in the manifest's `config` and rendered by Bindery. Nothing user-facing hides in an env var.

## How a download works

1. **Probe.** `adapters.getNormalStoryURLSite(url)` — FanFicFare's own offline adapter
   lookup. No network, no regex guessing, and it normalizes the URL as a side effect.
2. **Metadata pass.** `fanficfare --meta-only --json-meta` fetches the story index only.
   The host gets a title within seconds, and an update that has nothing new ends here as
   `status: "unchanged"` without downloading a single chapter.
3. **Download pass.** `fanficfare --progressbar --json-meta-file`. FanFicFare writes one
   dot per network fetch; against the chapter count from step 2 that is a real percentage
   rather than a fabricated one.
4. **Cover.** Pulled out of the finished EPUB by reading its OPF, falling back to a
   filename match. Offered as a second artifact with `kind: "cover"`.

FanFicFare runs as a **subprocess**, never imported into the serving process — except for
the offline adapter lookup, which is pure and stateless. It is a large library that keeps
global state and talks to sites that misbehave. A subprocess can be killed on host
disconnect; an import cannot.

### Updates

`capabilities.update` is honoured by comparing the site's `dateUpdated` against the host's
`options.knownUpdated` and answering `unchanged` when there is nothing new. When there *is*
something new, the story is fetched fresh rather than merged into the old EPUB: the host
holds the library, the plugin never sees the old file, and a clean re-fetch is the only
answer that is always correct.

## Configuration fields

| Key | Scope | Type | Notes |
|---|---|---|---|
| `ao3_username` / `ao3_password` | user | string / secret | Written into an `[archiveofourown.org]` section. Needed only for restricted works. |
| `is_adult` | user | bool | Satisfies the adult gate several sites use. |
| `include_images` | install | bool | Embeds illustrations. Bigger files, slower downloads. |
| `user_agent` | install | string | Override the browser identity. Leave blank unless a site is refusing you. |
| `personal_ini` | install | text | Raw FanFicFare config, applied *after* the fields above so it always wins. |

**Scope** is protocol §4.1. A `user` field is each person's own: Bindery shows it under
"Your settings", and a download carries the values of the person who queued it (for an
unattended update, the person who has held the book longest). An `install` field is set by
an admin and shared. That is why logins are not in `personal_ini` — put one there and it is
everyone's.

Two ini files are written per job and passed with `-c` in priority order: one generated
from the fields above, then the installation's `personal_ini`. `HOME` and `XDG_CONFIG_HOME` point
at the job's scratch directory so FanFicFare cannot pick up configuration from anywhere
unexpected.

## The action

`list_urls` takes an author page, series, collection, or bookmark list and returns every
story URL on it, each with a Download button. That is "search and pick" without the plugin
shipping a single tag of HTML — Bindery renders it from the manifest's action schema.

## The UI

`ui.mode: "fragment"`, three pages, no JavaScript and no CSS:

- **`/`** — a URL checker. Answers "will this work?" offline, before you queue anything.
- **`/sites`** — the supported-site list, filtered live over htmx. Read out of the
  installed FanFicFare at startup, so it is right by construction after every image bump.
- **`/ini`** — a `personal.ini` validator: does it parse, and do its sections name sites
  FanFicFare recognizes? `personal_ini` is installation config, so this screen is an
  admin's: its nav entry is `access: "admin"` and `/ini` is in `ui.adminPaths`. Bindery
  refuses it to a member before forwarding, and the plugin checks `X-Bindery-Role` itself
  too.

Fragments are stateless with respect to configuration. The `/ini` page validates; it does
not save. Persistent settings are declared in the manifest and stored by the host, which
is the only place they can be encrypted at rest and audited.

## Tests

The plugin is verified end to end without touching a real fanfic site, using FanFicFare's
own offline test adapters:

```bash
BINDERY_PLUGIN_TOKEN=dev BINDERY_FFF_ALLOW_TEST_SITES=1 uvicorn app.main:app --port 8080 &

python tests/conformance/run.py --base-url http://localhost:8080 --token dev \
  --download-url "http://test1.com?sid=1234&num_chapters=4" \
  --action-input "list_urls:url=http://test1.com?sid=1234"
```

That exercises the whole path — probe, metadata, streaming progress, artifact retrieval,
sha256 verification, cancellation — against a story FanFicFare fabricates locally.

## Failure codes

FanFicFare's CLI reports failures by printing English and does not use its exit code to say
what went wrong, so `fff.py` maps its output onto the protocol's error codes: missing
stories to `not_found`, login trouble and adult gates to `auth_required`, throttling to
`rate_limited`, timeouts to `network`, changed site markup to `parse`. Unrecognized output
becomes a retryable `internal` — cheap when the failure was transient, and visible in the
job log either way.

## Royal Road browser checks

Royal Road can return a Cloudflare challenge to the Python CLI even when the same story
opens in a browser. Bindery reports a confirmed challenge separately from a login error.
This diagnosis does **not** restore server-side downloads; issue #51 remains blocked.

The chosen workaround is local downloading followed by an EPUB upload:

1. Install a current Calibre (9.16 or newer) and its FanFicFare plugin.
2. In **Calibre's FanFicFare personal.ini**, not Bindery's settings, add:

   ```ini
   [www.royalroad.com]
   use_camoufox:true
   do_camoufox_warmup:true
   ```

3. Download the story as an EPUB in Calibre. Its browser integration may still encounter
   site-side failures; successful access is not guaranteed.
4. Save the EPUB to disk, then use Bindery's **Intake → Deposit a file**. It goes onto
   your own shelf. This is a manual import, not an automatically refreshed Royal Road
   download; repeat the export/import when you need an updated copy. A changed EPUB can
   appear as a separate book rather than replacing the earlier upload.

FanFicFare's [proxy documentation](https://github.com/JimmXinu/FanFicFare/wiki/ProxyFeatures#camoufox)
documents this integration and explicitly says it is unavailable in the Python CLI.
Setting `use_camoufox` in Bindery's `personal_ini` cannot enable it in this container.
The [browser-cache documentation](https://github.com/JimmXinu/FanFicFare/wiki/BrowserCacheFeature#websites-known-not-to-work)
also lists Royal Road as incompatible because its responses disable caching; mounting a
desktop browser cache is therefore not a supported solution here.

No browser, Calibre installation, shared browser profile, or new proxy is added to the
Bindery host or plugin deployment by this workaround.
