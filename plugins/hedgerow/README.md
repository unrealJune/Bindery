# Bindery plugin: Hedgerow

Reconciles a single web serial — *Maidens of the Fall*, on
[Royal Road](https://www.royalroad.com/) and mirrored at hungryshedgerow.net — from **two
sources** into one correctly ordered EPUB:

1. **Royal Road**, scraped through [FanFicFare](https://github.com/JimmXinu/FanFicFare) (a
   supported site — no scraper is written here).
2. **Discord**, where the author posts advance-copy EPUBs, often as *forwarded* messages. A
   bot ingests those attachments.

Chapters 1–5 might come from Royal Road, chapter 6 from a forwarded EPUB, chapter 7 from
Royal Road again; Hedgerow merges them into one book. It implements
`docs/PLUGIN-PROTOCOL.md` and knows nothing else about Bindery. Like every plugin it is a
container that speaks HTTP and shares no volume with the host — even the merged EPUB travels
to Bindery over HTTP.

## Running it

```bash
docker build -t bindery-plugin-hedgerow:dev plugins/hedgerow
docker run --rm -p 8080:8080 -e BINDERY_PLUGIN_TOKEN=dev \
  -v hedgerow-work:/work bindery-plugin-hedgerow:dev

curl -H 'Authorization: Bearer dev' localhost:8080/bindery/v1/manifest
```

Without Docker:

```bash
python -m venv .venv && . .venv/Scripts/activate    # PowerShell: .venv\Scripts\Activate.ps1
pip install -r plugins/hedgerow/requirements.txt
cd plugins/hedgerow
BINDERY_PLUGIN_TOKEN=dev uvicorn app.main:app --port 8080
```

The plugin **starts and serves `/healthz` with no configuration at all** — no token, no
Discord bot. In that state it does Royal Road only, which is the intended graceful
degradation.

## Environment

| Variable | Default | Meaning |
|---|---|---|
| `BINDERY_PLUGIN_TOKEN` | unset | Shared bearer token. Unset means no auth — dev only, and it warns. |
| `BINDERY_PLUGIN_PORT` | `8080` | Listen port. |
| `BINDERY_PLUGIN_WORKDIR` | `/work` | Writable volume. Durable state lives in `data/`, scratch in `jobs/`. |
| `BINDERY_FFF_BIN` | `fanficfare` | Path to the FanFicFare executable. |
| `HEDGEROW_DISCORD_TOKEN` | unset | Boot-time bot token fallback (see below). |
| `HEDGEROW_DISCORD_CHANNELS` | unset | Boot-time channel-id fallback, comma-separated. |

Everything a *user* configures — the bot token, channel IDs, the adult gate — is declared in
the manifest's `config` and rendered by Bindery. The `HEDGEROW_DISCORD_*` variables exist
only so an operator can bring the bot up at first boot before any download has carried the
host-stored config in.

## Persistence

Unlike the FanFicFare plugin, whose `/work` is disposable, Hedgerow keeps **durable state**
in SQLite on its own private volume at `/work/data/hedgerow.db`: ingested chapter bodies,
which source supplied each chapter first, manual ordering overrides, and channel→work
bindings. This is the plugin's own volume, not a mount shared with the host, so the
no-shared-volume rule holds. Per-job scratch lives separately under `/work/jobs`, and the
job reaper only ever walks `jobs/` — it can never delete durable state.

## Configuring the Discord bot

1. Create an application and bot at <https://discord.com/developers/applications>.
2. Under **Bot → Privileged Gateway Intents**, enable **MESSAGE CONTENT INTENT**. Hedgerow
   needs it to read the Royal Road URL a user posts to bind a channel to a work. Without it
   the bot connects but sees empty message content.
3. Invite the bot to your server with permission to read the relevant channels.
4. In Bindery, set the plugin's **Discord bot token** and **Discord channel IDs** config.
   The token is stored encrypted by Bindery and sent to the plugin, which persists it to its
   own private volume so the always-on bot survives restarts. The token is never logged.

With no token, the bot stays dormant and the plugin still works for Royal Road.

## Reconciliation rules

- **Binding.** A Royal Road fiction URL posted in a watched channel sets that channel's
  *current work*. EPUB attachments posted afterward attach to it. The URL is normalized the
  same way the download path normalizes it, so a bare `http://royalroad.com/fiction/123`
  and the canonical form are one work, not two.
- **Binding without a URL.** If an EPUB arrives in a channel with nothing bound, and the
  file *names its own source* — `dc:source`, or a `dc:identifier` that is a URL — the work
  is created and bound from that, no posted URL needed. A file that claims nothing is still
  ignored: a title is not an identity, and two serials sharing one would merge silently.
- **Forwarded messages.** Both `message.attachments` and every
  `message_snapshots[*].attachments` are checked — forwarded advance copies live only in the
  snapshots.
- **Expiring CDN URLs.** Discord attachment URLs are signed and expire (~24h). Bytes are
  read on receipt and stored locally; a CDN URL is never persisted.
- **Ordering.** Each chapter's `arc.part` is read from its title and sorted numerically,
  falling back to Royal Road publish date, then the Discord message timestamp. Labelled
  numbers win over unlabelled ones and a major label always supplies the arc, so
  `Maidens of the Fall, Arc 6, Zodiacal Light, Chapter 3` is **6.3** — not 3, which taking
  the last number in the title would give. `Zodiacal Light – 6.1`, `Chapter 7`, `Arc VI,
  Chapter 3`, and a bare `7` all still read correctly; `plugins/hedgerow/tests` pins the
  cases down.
- **Changing the parser is a migration.** `chapter_key` is derived from the parsed number, so
  a smarter parser moves keys that are already stored. `merge.KEY_VERSION` and
  `Store.reindex_chapters` rewrite them once at boot — without that, the next scrape would
  match nothing and re-ingest the whole back catalogue as duplicates. Bump the version with
  any parsing change.
- **Precedence: first-seen wins.** Whichever source supplies a chapter first owns it; the
  same chapter later supplied by the other source is dropped, not merged over. This is
  enforced by a `UNIQUE(work_id, chapter_key)` clause in the store.
- **Manual reconciliation.** The plugin's own UI lets you drag chapters into order and pin
  explicit numbers, both of which override the automatic ordering.

## How a download works

1. **Resolve the work.** A Royal Road URL creates/looks up a work; a hungryshedgerow.net URL
   resolves to the work bound via Discord.
2. **Scrape Royal Road** (if the work has a Royal Road URL) via FanFicFare as a **subprocess**
   — never imported into the serving process, exactly as the reference plugin does — and
   ingest its chapters with first-seen precedence.
3. **Reconcile** the Royal Road and Discord chapters into one order.
4. **Emit** a freshly built EPUB as the primary artifact, plus the Royal Road cover when one
   is available.

`unchanged` is deliberately never claimed on an update: a forwarded EPUB can add chapters
even when the Royal Road side has not moved, so every run rebuilds the merged book.

## Telling Bindery something arrived

Discord delivers chapters whenever the author posts, which no polling interval can predict.
So after ingesting, the plugin POSTs a contentless hint to Bindery (protocol §3.7) naming
the source that moved; Bindery decides whether to queue an update. The endpoint and its
token are read from the `X-Bindery-Notify*` headers on requests Bindery makes and persisted,
because the bot is not serving a request when a file arrives.

It is debounced — a forwarded batch is one call — and entirely best-effort. Bindery's own
update schedule is the backstop, so a hint that never lands costs latency, never
correctness. That is why nothing here retries.

## The actions

- **`pending`** — works that have ingested chapters but have not been downloaded into the
  library yet, each with a Download button.
- **`rescan`** — re-fetch a work from Royal Road and reconcile immediately, without waiting
  for the host's update schedule.
- **`forget`** — stop tracking a work: its ingested chapters and any channel bound to it are
  deleted. A book already filed in Bindery's library is **not** touched — the library is the
  host's, and a plugin reaching into it would be exactly the coupling the protocol prevents.
  Deleting the filed book is done from Bindery's own book page.

## The UI

`ui.mode: "sandboxed"` (see `docs/PLUGIN-UI.md`). Hedgerow ships a **complete HTML document**
with its own vanilla JavaScript and CSS, loaded into an opaque-origin iframe. Because the
frame's CSP sets `connect-src 'none'`, `fetch`/`XMLHttpRequest` are blocked; **all** host
communication goes through the `postMessage` bridge (`request`/`response`, `resize`, `ready`,
`notify`, `theme`). It links `/css/bindery.css` to inherit the host's `--bnd-*` design tokens
and follows the `theme` message.

Two screens:

- **Chapters** — every chapter with its source (Royal Road vs Discord EPUB), drag-and-drop
  reordering via the native HTML5 drag-and-drop API, and per-chapter number pinning. A *Clear
  overrides* button drops back to automatic ordering.
- **Sources** — the Discord bot's connection state, the channels it watches, the known
  works, and the channel→work bindings. Each work can be forgotten and each binding
  unbound from here; both are armed by a first click and performed by a second, because a
  blocking `confirm()` inside the frame would take the postMessage bridge down with it.

Even inside the sandbox, everything user-supplied is escaped before it reaches markup —
defense in depth, and the conformance suite checks reflection.

## Failure codes

FanFicFare reports failures by printing English, so `royalroad.py` maps its output onto the
protocol's error codes: missing stories to `not_found`, login trouble to `auth_required`,
throttling to `rate_limited`, timeouts and upstream 5xx to `network`, changed markup to
`parse`, and anything unrecognized to a retryable `internal`.
