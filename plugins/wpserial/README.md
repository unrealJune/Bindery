# Bindery plugin: WordPress serials

Binds web serials published on **WordPress** — self-hosted or WordPress.com — into EPUBs,
straight from the author's own site. Built for, and tested against:

| Serial | Site | What it exercises |
|---|---|---|
| *Worm* | parahumans.wordpress.com | WordPress.com; a TOC of per-chapter category archives; "Last/Next Chapter" links |
| *Maidens of the Fall* | hungryshedgerow.net | landing page → chapter list; password-protected advance chapters |
| *Sunspot* | sunspot.gay | TOC in a script-driven modal; `?p=` links; Patreon/Discord boilerplate; artwork |

There is no per-site adapter. The plugin reads a site the way a reader does, using what
WordPress itself emits (`article#post-N`, `.entry-content`, `post-password-required`,
`rel="next"`) and the words authors put on links ("Next Chapter", "Table of Contents"). It
implements `docs/PLUGIN-PROTOCOL.md` and knows nothing else about Bindery.

## Running it

```bash
docker build -t bindery-plugin-wpserial:dev plugins/wpserial
docker run --rm -p 8080:8080 -e BINDERY_PLUGIN_TOKEN=dev \
  -v wpserial-work:/work bindery-plugin-wpserial:dev

curl -H 'Authorization: Bearer dev' localhost:8080/bindery/v1/manifest
```

Unit tests are stdlib-only and need no network:

```bash
cd plugins/wpserial && python -m unittest discover -s tests -t .
```

## How a download works

1. **Find the table of contents.** The pasted URL may *be* the TOC, may link to it ("Click
   here for the chapters!", or "Table of Contents" in the menu), or may be a chapter whose
   page links to it. `inspect` (below) shows what was found without downloading anything.
2. **Read every chapter it lists, in order.** Links that turn out not to be story once loaded
   — a "side stories" index, a 404 — are skipped and logged, never fatal.
3. **Follow "Next Chapter" past the end of the TOC.** Authors update the TOC late. A link the
   author placed in the post is followed freely; the theme's chronological "next post" link
   only while the next post shares a category, so a hiatus announcement or another story on
   the same blog is not swallowed. A locked chapter ends the walk.
4. **No TOC at all?** Start from a "Start Reading" link, or walk "Previous" back from a pasted
   chapter, then forward.
5. **Clean and bind.** Navigation, share buttons, related posts, and Patreon/Discord *buttons*
   are removed; a sentence that links to the author's Patreon keeps its words and loses the
   link. Everything else goes through an allowlist serializer that emits well-formed XHTML
   and cannot emit script. Images are embedded, preferring WordPress's ≤1280px `srcset` size
   over multi-megabyte originals.

Chapters are identified by WordPress post id, not URL — the same post is routinely reachable
as `?p=45`, a pretty permalink, an old slug that redirects, and (on WordPress.com) a category
archive. That is what keeps duplicates out.

## Updates

Every chapter page read is cached on the plugin's own volume (`/work/data`), so an update of
a long serial re-reads only the newest few chapters (authors fix typos in those) plus
whatever is new: a 40-chapter update is ~6 requests. If nothing changed, the result is
`unchanged`. Locked chapters are never cached, so setting a password later just works. The
`clear_cache` action forgets a site's cached pages if one ever needs re-reading.

## Routing

WordPress serials live on every domain, so `matches` can only name a few. The rest is
`probe`, which answers without network I/O and claims:

- WordPress.com blogs, and the sites in `matches`;
- URLs shaped like WordPress permalinks (`?p=123`, `/2011/06/14/slug/`, `/chapter/…`);
- hosts in `WPSERIAL_HOSTS` (comma-separated), and hosts it has already bound a book from.

For any other site, pick **WordPress serials** in Bindery's *Routing* menu once; after a
successful download the host is remembered.

Priority is 50 — below FanFicFare (100) and Hedgerow (200). In particular **Hedgerow still
claims hungryshedgerow.net** by default, because it merges Discord advance copies; choose
this plugin explicitly to bind *Maidens of the Fall* from the site alone.

## Configuration

| Key | Type | Meaning |
|---|---|---|
| `post_passwords` | secret | For WordPress "Protected:" posts. One password for every site, or `host=password; host2=password2`. Submitted once per job through WordPress's own post-password form. |
| `request_delay_ms` | int | Politeness gap between page loads. Default 750. |
| `embed_images` | bool | Include chapter images and the cover. Default true. |
| `max_image_kb` | int | Leave out images larger than this. Default 0 (no limit; 10 MB hard cap). |
| `user_agent` | string | Override the identity presented to sites. |

| Environment | Default | Meaning |
|---|---|---|
| `BINDERY_PLUGIN_TOKEN` | unset | Shared bearer token. Unset means no auth — dev only, and it warns. |
| `BINDERY_PLUGIN_PORT` | `8080` | Listen port. |
| `BINDERY_PLUGIN_WORKDIR` | `/work` | Scratch in `jobs/`, cache in `data/`. |
| `WPSERIAL_HOSTS` | unset | Extra hosts `probe` should claim. |

## Actions

- **`inspect`** — given a URL, report the title, the start page, where the TOC is, and the
  links it lists. Reads one or two pages; no chapters.
- **`clear_cache`** — forget cached chapter pages for one site (any URL on it), or all.

## Limits and failure codes

- **Cloudflare and other bot challenges** are detected and reported as a non-retryable
  `network` error naming the challenge. This plugin does not drive a browser.
- **Patreon-gated posts** are detected and skipped; there is nothing to unlock them with.
- A site that does not look like WordPress at all is `unsupported_url`; a start page that
  404s is `not_found`; 429/503 are `rate_limited` (with `Retry-After` honoured, up to 60 s).
