# BiblioCommons plugin

Search your public library's catalog, place holds on physical books, and keep track of holds
and loans — for any library whose catalog runs on [BiblioCommons](https://www.bibliocommons.com/).
Built for **The Seattle Public Library** and the **King County Library System**; works with
others by subdomain.

It is a *circulation* plugin (protocol §3.8). It downloads nothing: a library loan is not a
file, and an ebook from the library is borrowed in the lending platform's own app, not copied
into an OPDS shelf.

## What it does

| | Physical (a copy on a shelf) | Digital (a licence on a lending platform) |
|---|---|---|
| Search | yes, with availability, copies, and holds queue | yes, labelled with the platform (OverDrive/Libby, hoopla…) |
| Place a hold | **yes**, at a chosen pickup branch or the card's default | no — linked out to borrow it in the platform's app |
| Track holds & loans | yes | yes, if they appear on the BiblioCommons account |
| Cancel a hold | yes | yes, if they appear on the BiblioCommons account |

Everything stateful — the lifecycle of each hold, history, email when something is ready — is
Bindery's, not this plugin's. See [`docs/CIRCULATION.md`](../../docs/CIRCULATION.md).

## Accounts

Each person connects their own card in Bindery under **Holds → Library cards**. Bindery
stores it per person with the PIN encrypted, and sends it to this plugin on each call. The
plugin keeps the signed-in library session **in memory only** — there is no volume, the root
filesystem is read-only, and a restart costs each card one fresh sign-in.

## Libraries

```
seattle   The Seattle Public Library     (preset)
kcls      King County Library System     (preset)
other     any BiblioCommons subdomain    e.g. "sfpl" for sfpl.bibliocommons.com
```

An operator can add more presets to the account form without code:

```bash
BIBLIOCOMMONS_LIBRARIES="sfpl=San Francisco Public Library,chipublib=Chicago Public Library"
```

The subdomain becomes part of a hostname, so it is validated against
`^[a-z0-9][a-z0-9-]{1,40}$` before use; a pasted catalog address is accepted only if it
really ends in `.bibliocommons.com`. Not every library that once used BiblioCommons still
answers on its public API — the gateway says so (`403 not_available`), and the plugin
reports it as a wrong library rather than a wrong PIN.

## What is verified, and what is not

This matters, so it is stated plainly.

**Verified against the live SPL and KCLS gateways (November 2026):**

- Search — `GET https://gateway.bibliocommons.com/v2/libraries/{slug}/bibs/search`. The
  real responses are in `tests/fixtures/` and the mapping is unit tested against them.
- Physical vs. digital — `policy.materialType`, the `ELECTRONIC_FORMATS` super-format, and an
  `eresourceUrl` are combined, because KCLS marks hoopla music `PHYSICAL`.
- The `f_STATUS=_physical_` / `_online_` filter (and that it is not exact, so the plugin
  enforces the medium itself).
- Pickup branches, from the `STATUS` facet of a broad physical search.
- The sign-in form's fields (`name`, `user_pin`, `authenticity_token`).
- That the holds and checkouts endpoints exist and require a session.

**Not verified — no library card was used while building this:**

- That a successful sign-in yields the `bc_access_token` / `session_id` cookies and an
  the patron's account id in the `/v2/` page state (chosen by matching the card's barcode).
- The exact shapes of the holds and checkouts responses, and of the place/cancel requests.

All of that lives in [`app/account.py`](app/account.py), reads several plausible keys,
and fails *closed*: anything it cannot make sense of becomes an error, never a partial
answer — because Bindery reads a hold missing from a snapshot as "cancelled" and would
mail you about it. The first sign-in with a real card is the test that settles it:

```bash
python tests/conformance/run.py --base-url http://localhost:8080 --token dev \
  --account library=seattle --account card=YOUR_CARD --account pin=YOUR_PIN
```

That runs verify, search, and snapshot read-only. Add `--hold-item S246C…` to also place
and cancel a real hold — it is a real hold on your real card, so it is opt-in.

## Running

```bash
pip install -r requirements.txt
BINDERY_PLUGIN_TOKEN=dev uvicorn app.main:app --port 8080
python -m unittest discover -s tests -t .     # stdlib only; no fastapi needed
```

| Variable | Meaning |
|---|---|
| `BINDERY_PLUGIN_TOKEN` | Shared bearer token (protocol §2.1). Unset means no auth — dev only. |
| `BINDERY_PLUGIN_PORT` | Port. Default `8080`. |
| `BIBLIOCOMMONS_LIBRARIES` | Extra presets for the account form, `slug=Name,…`. |
| `BIBLIOCOMMONS_CALL_TIMEOUT` | Seconds per library call. Default `27`, under the host's 30. |
