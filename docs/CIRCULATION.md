# Circulation: holds, loans, and notifications

Status: **describes the host.** The wire contract is [`PLUGIN-PROTOCOL.md` §3.8](PLUGIN-PROTOCOL.md#38-circulation--library-search-holds-and-loans);
this document is what Bindery does with it.

Downloads end in a file. Library circulation does not end at all for weeks: a hold on a
physical book sits in a queue, moves to a branch, waits on a shelf with a deadline, becomes a
loan, and comes due. This is the part of Bindery that keeps track of that, per person, and
tells them when something needs them.

---

## 1. Who owns what

| Concern | Owner | Where |
|---|---|---|
| Talking to the library's catalog | the plugin | e.g. `plugins/bibliocommons/` |
| A person's library cards and PINs | the host, per person, secrets encrypted | `CirculationAccounts` table |
| The lifecycle of each hold and loan | the host | `Bindery.Core.Circulation` (rules), `CirculationItems` (state) |
| The history of every transition | the host | `CirculationEvents` |
| Deciding to tell someone, and telling them | the host | `Notifications/` |

A plugin reports what an account looks like *now*. The host compares that with what it saw
last time and decides what *happened*. Keeping the rules in one place — a pure F# function
with exhaustive matching — is what lets every circulation plugin share them, and lets them
be tested without a library, a network, or a database.

## 2. Accounts are per person

Every row in `CirculationAccounts` has an `Owner`: the OIDC subject of the person who
connected it. Every query in `CirculationService` is filtered by it. One person cannot see,
search with, place holds on, or receive mail about another person's card — this is the one
place in Bindery that is multi-user on purpose, because a library card is a person's, not a
household's. (The book library itself stays shared; PLAN.md §10 is unchanged.)

Card numbers are stored as plain values because the UI shows the last four digits; PINs are
`secret` fields, encrypted with Data Protection exactly like plugin settings, never logged,
and never rendered back.

## 3. The lifecycle

```
                          ┌────────────── suspend ──────────────┐
                          ▼                                     │
   placed ──► Pending ◄── resume ── Suspended                   │
                │  (queue position moves; recorded, not mailed) │
                ▼                                               │
            InTransit ──────────────────────────────────────────┘
                │
                ▼
              Ready ─────── deadline passes, hold gone, no loan ──► Expired   ✉
                │
                │ hold gone, loan for the same item appeared
                ▼
            Collected ──► (the loan is tracked separately)

   any active hold ── cancelled through Bindery ──► Cancelled
   any active hold ── gone, not ready, no loan ───► Closed                    ✉

   loan seen ──► CheckedOut ── due date passes / library says overdue ──► Overdue  ✉
                    │  ▲ due date moves later: Renewed (recorded)
                    ▼
                 Returned  (gone from the account)
```

`✉` marks a transition that produces a notification. So do `InTransit` and `Ready`, plus two
reminders that are not transitions at all (§4).

### Rules worth knowing

- **Disappearance is evidence, but only from a complete snapshot.** The protocol requires a
  snapshot to be complete or to be an error, and the host never reconciles a failed one. A
  plugin outage therefore produces no events at all, rather than a flood of "Closed".
- **Collected is matched by catalog id.** When a hold vanishes and the same snapshot holds a
  loan whose `itemId` matches, the hold is `Collected`. This is how a ready hold turns into
  a checkout without anyone telling Bindery.
- **Ready-then-gone without a loan is Expired**, deadline or not: the library pulled it off
  the shelf. Gone while still queued is `Closed` — cancelled on the library's own site, or
  by the library — and is mailed because it is a surprise.
- **Cancelling through Bindery is recorded immediately** as `Cancelled`, so the next
  snapshot's absence is not misread as `Closed`, and nobody is mailed about their own click.
- **Unknown states are not invented.** A hold the plugin reports in a state this build does
  not know keeps its previous state; the raw value is kept in the event history.
- Terminal rows stay as history. A hold id that reappears after being closed starts a new
  row rather than resurrecting the old one.

## 4. Notifications

### Where they come from

Notifications are not a plugin feature, and that is deliberate. A plugin never sends a
message: it reports state, and the host decides. The reason is the same as for §3.7's
"hint, never command" — a plugin that could address mail to people would be a spam and
phishing channel with a container image in front of it.

What makes the subsystem shared across plugins is that its inputs are generic:

| Source | Categories | Fires for |
|---|---|---|
| Circulation (every circulation plugin) | `hold-in-transit`, `hold-ready`, `pickup-reminder`, `hold-ended`, `due-reminder`, `overdue` | transitions in §3 and the two reminders below |
| Downloads (every downloader plugin) | `download-failed`, `download-filed` | a job that failed for good; a book filed by a job you started |

Anything new — a future plugin kind, a library scan report — enqueues through the same
`NotificationService.EnqueueAsync(owner, category, subject, body)` and inherits preferences,
the outbox, retries, and every delivery channel.

### Reminders

Two notifications are about time passing, not about state changing, so the reconcile
function computes them on every sync:

- `pickup-reminder` — a ready hold whose `readyUntil` is within `Circulation:PickupReminder`
  (default 48 hours).
- `due-reminder` — a loan whose `dueAt` is within `Circulation:DueReminder` (default 3 days).

Each item remembers the deadline it was last reminded about, so a reminder goes out once
per deadline. A renewal moves the deadline and earns a fresh reminder.

### Preferences

Per person, on the **Library cards** page: an address (defaulting to the `email` claim), a
master switch, and a mute per category. `download-filed` is muted by default — it is noise
for anyone who just pressed the button — and everything else is on.

### Delivery

Outbox pattern. `EnqueueAsync` writes a `Notifications` row and returns; nothing on a
request path or inside a sync ever waits on SMTP. `NotificationWorker` drains the outbox,
retries with exponential backoff up to `Notifications:MaxAttempts`, and marks rows
`Sent`, `Failed`, or `Suppressed` (no address, or muted after it was queued).

Channels implement `INotificationChannel`. Shipped: **email** over SMTP with STARTTLS, using
the framework's `SmtpClient` so the image gains no dependency. Port 465 implicit TLS is not
supported by that client; use 587. For development and tests, `Email:PickupDirectory` writes
`.eml` files instead of connecting anywhere.

```yaml
Bindery:
  Notifications:
    Email:
      Enabled: true
      Host: smtp.example.org
      Port: 587
      UseStartTls: true
      Username: bindery@example.org
      Password: "…"          # from a Secret, never values.yaml
      From: "Bindery <bindery@example.org>"
```

## 5. Sync cadence

`CirculationSyncWorker` sweeps every `Circulation:SweepInterval` (5 minutes) and syncs each
account whose last sync is older than `Circulation:SyncInterval` (30 minutes), at most
`Circulation:MaxPerSweep` at a time. A person can always press **Sync now**. An account whose
credentials are refused is marked as needing attention and skipped until they are re-entered,
so a wrong PIN does not get a card locked by repeated attempts.

## 6. Rebuildability

The library-files-are-truth rule (CLAUDE.md) is untouched: none of these tables describe
books on disk. Circulation state is a cache of the library's own records — delete it and the
next sync rebuilds every active hold and loan, losing only history. Accounts are in the same
position as plugin settings: configuration a person typed in, kept only in the database.
