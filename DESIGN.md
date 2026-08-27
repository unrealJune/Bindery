---
name: Bindery
description: A harbour signal station for a self-hosted ebook library — flip-dot status, hairline panels, two themes from one token set.
colors:
  page: "light-dark(#eef2f5, #0a161e)"
  void: "light-dark(#dde4e9, #060c14)"
  panel: "light-dark(#ffffff, #060f16)"
  panel-sunk: "light-dark(#f4f8fa, #0b1720)"
  panel-raise: "light-dark(#ffffff, #0e1c26)"
  ink-strong: "light-dark(#0d1c27, #f2f5ec)"
  ink: "light-dark(#152633, #cfd9ca)"
  ink-body: "light-dark(#28414f, #b3c1b7)"
  ink-muted: "light-dark(#4d6675, #8fa39c)"
  ink-faint: "light-dark(#526a78, #74867f)"
  meta: "light-dark(#3f6a70, #86b3ab)"
  accent: "light-dark(#116b73, #a8ddd2)"
  accent-hover: "light-dark(#0c5860, #cbeee5)"
  accent-quiet: "light-dark(#2e8f93, #80b8ae)"
  accent-line: "light-dark(rgba(17, 107, 115, 0.45), rgba(168, 221, 210, 0.5))"
  accent-wash: "light-dark(rgba(17, 107, 115, 0.07), rgba(128, 184, 174, 0.09))"
  amber: "light-dark(#8a5310, #eda23c)"
  amber-wash: "light-dark(rgba(198, 121, 26, 0.1), rgba(237, 162, 60, 0.12))"
  hairline: "light-dark(#d6dee4, rgba(128, 184, 174, 0.14))"
  hairline-soft: "light-dark(#e4eaef, rgba(128, 184, 174, 0.08))"
  hairline-strong: "light-dark(#b9c6cf, rgba(128, 184, 174, 0.26))"
  ok: "light-dark(#1c7a51, #30d158)"
  warn: "light-dark(#8a5310, #ff9f0a)"
  err: "light-dark(#a3231b, #ff453a)"
  board-face: "light-dark(#e3eaee, #0d0d0d)"
  board-edge: "light-dark(#cdd8de, #1c1c1c)"
  dot-off: "light-dark(#d2dce2, #171717)"
  island: "light-dark(rgba(255, 255, 255, 0.9), rgba(10, 20, 31, 0.9))"
  island-line: "light-dark(rgba(30, 55, 72, 0.12), rgba(130, 170, 190, 0.16))"
  seg: "light-dark(#dde5ea, rgba(128, 184, 174, 0.14))"
  focus: "light-dark(#116b73, #a8ddd2)"
typography:
  display:
    fontFamily: "Rajdhani, Fira Sans, ui-sans-serif, system-ui, sans-serif"
    fontSize: "clamp(1.75rem, 1.2rem + 2vw, 2.75rem)"
    fontWeight: 700
    lineHeight: 1.1
    letterSpacing: "-0.005em"
  headline:
    fontFamily: "Rajdhani, Fira Sans, ui-sans-serif, system-ui, sans-serif"
    fontSize: "1.35rem"
    fontWeight: 600
    lineHeight: 1.1
    letterSpacing: "-0.005em"
  title:
    fontFamily: "Rajdhani, Fira Sans, ui-sans-serif, system-ui, sans-serif"
    fontSize: "1.05rem"
    fontWeight: 600
    lineHeight: 1.1
    letterSpacing: "-0.005em"
  body:
    fontFamily: "Fira Sans, -apple-system, BlinkMacSystemFont, Segoe UI, sans-serif"
    fontSize: "1rem"
    fontWeight: 400
    lineHeight: 1.6
    letterSpacing: "normal"
  label:
    fontFamily: "JetBrains Mono, ui-monospace, SFMono-Regular, Menlo, monospace"
    fontSize: "0.74rem"
    fontWeight: 500
    lineHeight: 1.4
    letterSpacing: "0.15em"
  data:
    fontFamily: "JetBrains Mono, ui-monospace, SFMono-Regular, Menlo, monospace"
    fontSize: "0.7rem"
    fontWeight: 400
    lineHeight: 1.5
    letterSpacing: "0.04em"
    fontFeature: "tabular-nums"
  tally:
    fontFamily: "Rajdhani, Fira Sans, ui-sans-serif, system-ui, sans-serif"
    fontSize: "0.95rem"
    fontWeight: 600
    lineHeight: 1.2
    letterSpacing: "0.06em"
    fontFeature: "tabular-nums"
rounded:
  xs: "3px"
  md: "4px"
  board: "6px"
  island: "26px"
  pill: "999px"
spacing:
  s1: "0.38rem"
  s2: "0.5rem"
  s3: "0.6rem"
  s4: "0.75rem"
  s5: "1rem"
  s6: "1.1rem"
  s7: "1.25rem"
  s8: "1.5rem"
  s9: "2rem"
  s10: "3rem"
  s11: "4rem"
  gutter: "1rem"
components:
  button:
    backgroundColor: "{colors.panel-sunk}"
    textColor: "{colors.ink-strong}"
    typography: "{typography.body}"
    rounded: "{rounded.md}"
    padding: "0.6rem 1.1rem"
    height: "44px"
  button-hover:
    backgroundColor: "{colors.accent-wash}"
    textColor: "{colors.accent}"
  button-primary:
    backgroundColor: "{colors.accent}"
    textColor: "light-dark(#ffffff, #04141b)"
    rounded: "{rounded.md}"
    padding: "0.6rem 1.1rem"
    height: "44px"
  button-primary-hover:
    backgroundColor: "{colors.accent-hover}"
    textColor: "light-dark(#ffffff, #04141b)"
  text-action:
    backgroundColor: "transparent"
    textColor: "{colors.accent}"
    typography: "{typography.data}"
    padding: "0.38rem 0"
  control:
    backgroundColor: "{colors.panel-sunk}"
    textColor: "{colors.ink-strong}"
    typography: "{typography.data}"
    rounded: "{rounded.md}"
    padding: "0.6rem 0.75rem"
    width: "100%"
  control-focus:
    backgroundColor: "{colors.panel}"
    textColor: "{colors.ink-strong}"
  panel:
    backgroundColor: "{colors.panel}"
    textColor: "{colors.ink}"
    rounded: "{rounded.md}"
    padding: "1.25rem"
  empty-state:
    backgroundColor: "{colors.panel-sunk}"
    textColor: "{colors.ink-muted}"
    rounded: "{rounded.md}"
    padding: "2rem 1.25rem"
  notice:
    backgroundColor: "{colors.accent-wash}"
    textColor: "{colors.ink}"
    rounded: "{rounded.md}"
    padding: "0.75rem 1.1rem"
  nav-rail-item:
    backgroundColor: "transparent"
    textColor: "{colors.ink-body}"
    typography: "{typography.data}"
    padding: "0.6rem 0.75rem"
  nav-rail-item-current:
    backgroundColor: "{colors.accent-wash}"
    textColor: "{colors.accent}"
  nav-island:
    backgroundColor: "{colors.island}"
    textColor: "{colors.ink-body}"
    rounded: "{rounded.island}"
    padding: "0.5rem"
    width: "min(30rem, calc(100vw - 1.5rem))"
  flipdot-board:
    backgroundColor: "{colors.board-face}"
    rounded: "{rounded.board}"
    padding: "7px"
  tag:
    backgroundColor: "{colors.accent-wash}"
    textColor: "{colors.meta}"
    rounded: "{rounded.xs}"
    padding: "2px 0.6rem"
---

# Design System: Bindery

## Overview

**Creative North Star: "The Harbour Signal Station"**

Sources arrive off the open network and are filed onto a shelf, and the whole interface is the departure board that reports on it. Everything follows from that: an electromechanical flip-dot board states the floor in one word, monospaced data runs in registers beneath it, condensed display type carries the headings, and structure is drawn with hairline rules rather than with stacked cards. This is an operator tool for one self-hoster — intake, register, job ledger, plugin settings, reader tokens — so it is dense, quiet, and legible at a glance, with exactly one loud object per surface.

The visual world is a mesh of two existing systems, not an invention. Dark is Shoreline's ocean: a `#0a161e` page, foam-teal accent, a black dot board, hairline panels sitting *under* the page colour rather than raised above it. Light is streetCryptid's daybreak: cool off-white paper at `#eef2f5`, `#152633` ink, `#d6dee4` hairlines, and a dot board rendered in dark ink because a pale teal dot cannot hold 4.5:1 against paper. Both themes come from **one token set**: every themed value in `src/Bindery.Host/wwwroot/css/bindery.css` is a `light-dark()` pair, and `color-scheme` on `:root` is the only switch. The OS preference therefore works with zero JavaScript; `theme.js` only overrides that one property via `data-theme`.

Two hard constraints shaped every decision and will re-bind anyone who touches this UI. First, the Content-Security-Policy in `src/Bindery.Host/Security/SecurityHeaders.cs` has no `unsafe-inline`: `script-src 'self'`, `style-src 'self'`, `font-src 'self'`. No inline `style` attribute, no inline `<script>`, no CDN font exists anywhere in this codebase. Second, there is no npm or Node build step — htmx is vendored, fonts are self-hosted woff2 latin subsets in `wwwroot/fonts/`, and every page is server-rendered Razor.

**Key Characteristics:**
- Two themes, one token set, switched by `color-scheme` — no JS required for correctness.
- Every edge is a hairline; structure comes from rules, never from a nested card.
- One accent hue family (the ocean ramp) plus amber as an attention rim, never an identity.
- The flip-dot board is the only loud element, and there is one per surface.
- No inline styles anywhere: per-dot colour and delay are quantised into class names.
- Status never rests on colour alone — every mark sits beside the state's word.
- The same nav markup is a hairline rail on desktop and a floating island under 62rem.

## Colors

One hue family — the Shoreline ocean ramp — pitched separately for each ground, with amber as the single non-ocean rim and a small semantic set for state.

### Primary
- **Ocean Accent** `--accent` (`light-dark(#116b73, #a8ddd2)`): links, current-nav marks, primary button ground, caret colour, focus ring. Deep teal on paper, foam teal on abyss — the same role, re-pitched so it holds contrast on either ground.
- **Ocean Accent Deep** `--accent-hover` (`light-dark(#0c5860, #cbeee5)`): the only hover destination for accent-coloured text and for `.button-primary`'s ground.
- **Ocean Quiet** `--accent-quiet` (`light-dark(#2e8f93, #80b8ae)`): the live pip and the `status-running` mark — accent presence at low volume.
- **Accent Line** `--accent-line` (45–50% alpha accent): hairline edges that want to read as accent without becoming a fill. Link underlines, hover borders, the `.action-form` left rule.
- **Accent Wash** `--accent-wash` (7–9% alpha accent): hover ground for rows, rail items, tags, and notices. It is a tint, never a surface in its own right.

### Secondary
- **Frontier Amber** `--amber` (`light-dark(#8a5310, #eda23c)`): required-field marks, the `cancelled` status mark, warning notices. Attention, never identity — amber appears in single marks, never as a surface or a nav state.

### Neutral
- **Daybreak Paper / Ocean Abyss** `--page` (`light-dark(#eef2f5, #0a161e)`): the page ground, mirrored into `<meta name="theme-color">` in `_Layout.cshtml` so mobile browser chrome joins the theme.
- **Void** `--void` (`light-dark(#dde4e9, #060c14)`): the top of the signal field's vertical tint only.
- **Panel** `--panel` (`light-dark(#ffffff, #060f16)`): every docket, station, plugin card, gate.
- **Panel Sunk** `--panel-sunk` (`light-dark(#f4f8fa, #0b1720)`): inputs, secondary buttons, empty states, inset cards — surfaces that sit *below* the page.
- **Ink** ramp: `--ink-strong` `#0d1c27` / `#f2f5ec` for headings and row titles, `--ink` for body default, `--ink-body` for prose, `--ink-muted` for labels and secondary data, `--ink-faint` for inert pager text. One family stepped down; never gray-on-colour.
- **Meta Teal** `--meta` (`light-dark(#3f6a70, #86b3ab)`): counts, folios, formats, tags — data that is neither a link nor a heading.
- **Hairlines** `--hairline` / `--hairline-soft` / `--hairline-strong`: the load-bearing structure of the entire system. Strong for control borders and dashed empty states, plain for panel edges and section rules, soft for the divider between rows inside a list.

### Semantic
- **OK** `--ok` (`light-dark(#1c7a51, #30d158)`), **Warn** `--warn` (`light-dark(#8a5310, #ff9f0a)`), **Error** `--err` (`light-dark(#a3231b, #ff453a)`), each with an 8–12% `-wash` companion for notice grounds and the `failed` ledger row.

### Board palette
- `--board-face` / `--board-edge` / `--dot-off` and the seven-step `--dot-0`…`--dot-6` ramp. Under dark, the board is a black physical object and the dots run blue → foam → near-white. Under light, the board is inked paper and the ramp is **deliberately compressed into dark ink** (`#1a7086` → `#1a2b3d`), because a flip dot has to hold 4.5:1 against its own board face and a pale teal on paper does not.

### Named Rules
**The One Hue Rule.** The palette is one ocean hue family plus amber. A new colour is not added; a new *role* is expressed by re-pitching the existing accent (`--accent-quiet`, `--accent-line`, `--accent-wash`) or by the semantic trio. Amber never becomes an identity colour.

**The Word-Beside-the-Mark Rule.** Status never rests on colour alone. Every `.status-mark` in the interface sits next to the state's word (`.job-state strong`, `.job-strip strong`), and the flip-dot board renders the state as literal letters. A colour-only indicator is not a state.

**The Retargeting Rule.** No component hardcodes a hex. Every themed value is a `light-dark()` pair declared once on `:root`. The only literal colours below the token layer are the two button-primary text values (`light-dark(#ffffff, #04141b)`), which are the accent's own contrast partners.

## Typography

**Display Font:** Rajdhani 500/600/700 (falls back to Fira Sans, then system sans)
**Body Font:** Fira Sans 400/500/600 (falls back to `-apple-system`, Segoe UI)
**Data/Label Font:** JetBrains Mono 400/500

All three are self-hosted latin-subset woff2 files in `src/Bindery.Host/wwwroot/fonts/`, declared with `font-display: swap`. `font-src 'self'` means a CDN font is not an option; adding a weight means adding a file.

**Character:** Condensed Rajdhani gives headings and counts the compressed authority of a departure board. Fira Sans keeps prose humane at operator density. JetBrains Mono does all the work that has to read as machine output — labels, timestamps, URLs, digests, ledger columns — and `font-variant-numeric: tabular-nums` is applied to `time`, `data`, `.folio`, `.tally`, `.digest`, and `.pager-position` so a changing number never shifts its neighbours.

### Hierarchy
- **Display / h1** (Rajdhani 700, `clamp(1.75rem, 1.2rem + 2vw, 2.75rem)`, 1.1, `text-wrap: balance`): one per page, in `.page-heading`.
- **Headline / h2** (Rajdhani 600, 1.35rem, 1.1): section and docket headings.
- **Title / h3** (Rajdhani 600, 1.05rem, 1.1): sub-blocks; note that `.grouping-column h3` and `.plugin-form h3` deliberately drop to the mono label treatment.
- **Body** (Fira Sans 400, 1rem, 1.6): default. Prose is capped at `68ch` (`.page-heading p`, `.book-summary`) and empty-state copy at `46ch`.
- **Label / h4** (JetBrains Mono 500, 0.6–0.74rem, `letter-spacing: var(--track-label)` = 0.15em, uppercase, `--ink-muted`): field labels, `dt` terms, ledger column heads, rail divider.
- **Data** (JetBrains Mono 400, 0.66–0.86rem, 0.03–0.08em tracking): timestamps, URLs, sequence numbers, formats, secrets.
- **Tally** (Rajdhani 600, 0.82–0.95rem, tabular): percentages and per-row counts (`.job-progress data`, `.job-strip data`, `.folio`).

### Named Rules
**The Three-Voice Rule.** Display speaks headings and numbers, mono speaks machine facts, sans speaks to the operator. A label is never set in the sans face and a paragraph is never set in mono. `.field-check` and `.action-form h4` are the explicit exceptions: they revert to sans because they are addressed to a person.

**The Heading-Carries-Itself Rule.** A section heading does not get an explanatory paragraph restating it. `.section-heading p` exists for a mono sub-fact (a count, a filter state), not for an explainer. The redundant explainer under each `h2` was removed on purpose.

## Layout

The frame is `.app-frame`: a two-column grid of `--rail-w` (13.5rem) plus `minmax(0, 1fr)`, capped at `--page-max` (86rem) and centred, with a sticky `--masthead-h` (3.5rem) masthead above it. The rail is sticky and full-height with a right hairline; `.work-surface` is a flex column with `--s10` (3rem) between sections and `--s9 --s7 --s11` padding.

Spacing is Shoreline's own unrounded rem ladder — `--s1` 0.38rem through `--s11` 4rem — plus a single `--gutter` of 1rem used for every multi-panel grid gap. Registers and ledgers are CSS grids with fixed leading columns and `minmax(0, 1fr)` bodies so long titles ellipsize instead of overflowing.

**Breakpoints.** `62rem` is the structural one; `30rem` is a density trim; `(pointer: coarse)` is orthogonal to both.

At `max-width: 62rem` the same `<nav class="section-rail">` markup — one landmark, never duplicated — becomes streetCryptid's floating island: fixed, centred, `width: min(30rem, calc(100vw - 1.5rem))`, `bottom: calc(env(safe-area-inset-bottom, 0px) + 0.75rem)`, `--radius-island` 26px, `backdrop-filter: blur(18px) saturate(1.3)`, items reflowed to icon-over-label columns. `.work-surface` and `.site-footer` take `padding-bottom: calc(env(safe-area-inset-bottom, 0px) + 6.5rem / 6rem)` to clear it. The plugin-desk links (`.rail-extra`) and their divider hide on the island so it stays the five core tabs, and the desks remain reachable from the Plugins page. The intake grid and book sheet collapse to one column, the job ledger drops its head row and becomes a stack of records, and `.book-row` re-lays into named grid areas.

At `max-width: 30rem` panel padding drops to `--s6`, the wordmark's sub-line hides, rail labels drop to 0.52rem, and `.flipdot-lg` shrinks its dots to 4.5px.

Under `(pointer: coarse)`, regardless of viewport: `.book-row`, `.job-ledger-row`, and `.token-row` take `min-height: 52px`; `.text-action` takes `min-height: 32px`; and small standalone links in the footer, grouping columns, desk tabs, and pager become `inline-flex` with `min-height: 32px`, because a 17px-tall footer link is not tappable.

Verified: no horizontal overflow at 1440×960 or 390×844 in either theme.

### Named Rules
**The One Navigation Rule.** There is one nav element in the document and it changes shape, not identity. Do not add a second mobile nav, a hamburger, or a duplicated landmark — a screen reader must not walk the same links twice.

## Elevation & Depth

This system is **not flat, and not lifted**. Depth comes from three sources in strict order: the hairline (which is the edge), tonal layering between `--void` / `--page` / `--panel` / `--panel-sunk`, and a single diffuse ambient shadow that never changes with state. Panels sit *under* the page colour rather than above it; a shadow is atmosphere, not a rank.

The background is **never flat**. `.signal-field` is a fixed, `aria-hidden`, pointer-transparent dot lattice — `radial-gradient` dots on a `--signal-step` 22px grid over a vertical `--void → --page` tint — breathing between 0.85 and 1 opacity on a 14s cycle. A `::after` scrim of `color-mix` page colour ensures content never sits directly on the moving field.

### Shadow Vocabulary
- **Panel ambient** `--shadow-panel` (`light-dark(0 12px 28px rgba(40, 60, 80, 0.1), 0 12px 28px rgba(0, 7, 12, 0.4))`): dockets, stations, plates, the gate. Constant at rest and on hover.
- **Island** `--shadow-island` (`light-dark(0 10px 30px rgba(40, 60, 80, 0.16), 0 12px 30px rgba(0, 0, 0, 0.5))`): the mobile navigation island only, paired with a backdrop blur.

### Named Rules
**The No-Lift Rule.** Hover moves **colour, border, and ground together**: no `translateY`, no scale-up, no shadow change. `.button:hover`, `.book-row:hover`, `.section-rail a:hover` all follow this. Two small transforms exist and neither is a lift — `.button:active { transform: scale(0.98) }` (a press-down) and `.book-row:hover .row-arrow { transform: translateX(2px) }` (a chevron leaning toward its destination) — and both are removed under reduced motion.

**The Hairline-Not-Nesting Rule.** A panel gets structure from rules, not from another card. Nested cards are banned. Where a station board sits inside a docket it sheds its own chrome entirely and takes a hairline: `.queue-docket > .station` drops border, radius, background, and shadow and keeps a `border-bottom` — and drops even that when it is `:last-child`, because a clear floor has no list to divide.

## Shapes

Radii are small and deliberate: `--radius-xs` 3px (checkbox, tag, badge), `--radius` 4px (the default for panels, buttons, controls, notices), `--radius-board` 6px (the flip-dot board), `--radius-island` 26px (the mobile island, and only it), `--radius-pill` 999px (the theme segmented control, scrollbar thumb, progress meter). The jump from 4px to 26px is the signal that the island is a different kind of object — floating chrome, not a document surface.

Every border in the system is 1px. The single 2px rule in the interface is `.action-form`'s `border-left: 2px solid var(--accent-line)` — the inset Card. `:focus-visible` is a hard `2px solid var(--focus)` outline at `4px` offset (2px on controls), never a glow.

Iconography is a two-part policy, because the mesh has two halves. **Navigation and controls are drawn geometry**: 24-viewBox inline SVG at `stroke-width: 1.6`, `fill: none`, `currentColor`, sized 15–19px — the five rail/island icons and the three theme-control icons, all inlined in `_Layout.cshtml`. That is streetCryptid's half, and it supplies every drawn glyph in the system. **Row affordances and inline direction are Shoreline's sanctioned characters**: Shoreline's web half ships no icon set at all and names `›` as its entry chevron, so `.row-arrow` is the character `›` (mono, 1.15rem, `line-height: 1`), the pager reads `‹ Previous` / `Next ›`, the plugin desk link ends `desk ›`, and `·` separates metadata (`@job.PluginName · @job.Url`).

Other small geometry is drawn from CSS primitives rather than from either source: the select chevron is two rotated `linear-gradient` hairlines in `currentColor`; the checkbox tick is two borders rotated 45°; the status mark is a `radial-gradient` dot cluster on a 4px grid — the board's own grain.

### Named Rules
**The Sanctioned Character Rule.** Navigation and controls use drawn SVG (24 viewBox, `stroke-width: 1.6`, `fill: none`, `currentColor`). Row affordances and inline direction use Shoreline's permitted characters and only those: `›` and `‹` for chevrons, `·` for the metadata separator, `—`/`–` for dashes. No other Unicode character stands in for an icon, and no emoji appears anywhere. A character used this way is always `aria-hidden` and never the only carrier of meaning.

**The Reserved Rule.** The 2px accent left-rule belongs to the inset Card (`.action-form`) and to nothing else. Notices, callouts, and alerts are hairline boxes with a tinted ground (`.notice` + `--accent-line` / `--accent-wash`, and the `-success` / `-error` / `-warn` variants). A thick coloured tab on an alert is a different pattern wearing the same clothes.

## Components

### Buttons
- **Shape:** 4px radius (`--radius`), 1px border, `min-height: 44px`, `padding: var(--s3) var(--s6)`.
- **Structure:** a two-line column — a sans label over an optional mono `<small>` at 0.62rem/0.08em uppercase at 0.72 opacity ("File source" / "Queue download").
- **Default:** `--panel-sunk` ground, `--hairline-strong` border, `--ink-strong` text.
- **Primary:** `--accent` ground and border, text `light-dark(#ffffff, #04141b)`.
- **Hover:** default → accent text, `--accent-line` border, `--accent-wash` ground; primary → `--accent-hover` ground and border. 200ms `--ease`. No lift.
- **Active:** `scale(0.98)`. **Disabled:** `opacity: 0.5`, `cursor: not-allowed`, no transform.
- **Text action** (`.text-action`): borderless mono 0.72rem uppercase accent, underlines on hover; `.danger` recolours to `--err`.

### Cards / Containers
- **Corner Style:** 4px (`--radius`).
- **Background:** `--panel`. **Border:** 1px `--hairline`. **Shadow:** `--shadow-panel`, constant.
- **Internal Padding:** `--s7` (1.25rem), dropping to `--s6` under 30rem.
- **Heading:** `.docket-heading` — an h2 with a mono meta span or link on the baseline, separated by a bottom hairline.
- **Empty state:** dashed `--hairline-strong` border on `--panel-sunk`, centred, a Rajdhani `strong` over a 46ch sans line. It is the only dashed border in the system.

### Inputs / Fields
- **Style:** `.control` — full width, `--panel-sunk` ground, 1px `--hairline-strong`, 4px radius, **mono** 0.86rem, `caret-color: var(--accent)`.
- **Hover:** border → `--accent-line`. **Focus:** `2px solid var(--focus)` outline at 2px offset, border → `--accent`, ground → `--panel`. A hard ring, never a glow.
- **Sizes:** `.control-large` for the primary intake field; `.control-text` for vertical-resize textareas.
- **Label:** mono 0.68rem uppercase at 0.15em tracking, `--ink-muted`; required marked by an amber `abbr`.
- **Error:** `.field-error` / `.validation-summary` in mono 0.76rem `--err`; empty summaries collapse.
- **Checkbox:** 18px appearance-none box, accent-filled when checked, tick drawn from two rotated borders.

### Navigation
- **Desktop rail:** mono 0.74rem uppercase at 0.05em, `--ink-body`, 16px SVG at 0.7 opacity, 2px transparent left border. Hover and current both fill `--accent-wash` and colour the left border (`--accent-line` on hover, solid `--accent` when current); the icon goes to full opacity.
- **Mobile island:** same markup, `--island` ground at 0.9 alpha with an 18px blur, `--island-line` hairline, `--shadow-island`, 26px radius; items become 0.58rem icon-over-label columns and the selected state fills `--seg`.
- **Masthead:** sticky, 88% page colour with a 12px backdrop blur, bottom hairline, wordmark = a `flipdot-sm` "B" board beside Rajdhani 600 at 1.18rem.
- **Theme segmented control** (`.theme-seg`): a pill of three 30×26px SVG radio buttons; selection is carried by the `--seg` ground and `--ink-strong`, not by colour. Hidden entirely when `html:not(.js)`, because without JavaScript it cannot do anything.
- **Desk tabs** (`.desk-tabs`): pill-radius mono links over a bottom hairline; current takes `--seg`.

### Registers, Ledgers and Lists
Rows are grids with a top hairline on the container and a `--hairline-soft` bottom on each row, last-child cleared. `.book-row` hovers to `--accent-wash` with its title going accent and its `.row-arrow` chevron (`›`, mono 1.15rem, `aria-hidden`) moving `translateX(2px)` — the one place a transform survives, and it is removed under reduced motion. `.job-ledger-row[data-state="failed"]` takes `--err-wash` across the full row. Job progress is a real `<progress class="job-meter">` — 4px, pill radius, accent value bar, `--seg` track — so the value reaches assistive technology and no inline width style is needed. Job progress is a real `<progress class="job-meter">` — 4px, pill radius, accent value bar, `--seg` track — so the value reaches assistive technology and no inline width style is needed.

### Signature Component: the flip-dot board
The one loud element, and the reason for several structural decisions. `src/Bindery.Host/Ui/FlipDot.cs` renders a word as a 5×7 dot matrix server-side: each dot is an `<i class="fd">`, lit dots gain `on` plus a gradient class `g0`–`g6`, and every dot — lit or unlit — gains a sweep class `s0`–`s23`.

Those are classes and not custom properties **because the CSP has no `unsafe-inline`**: a dot cannot carry its colour or delay in a `style` attribute. The ramp is quantised to 7 buckets sampled left-to-right with a slight vertical skew (banding, not the reference renderer's `Math.random` jitter, which would reshuffle colours on every htmx poll), and the sweep to 24 steps of 29ms — a ~670ms diagonal crossing from the top-left. The flip itself is a 220ms `scaleY(1 → 0.08 → 1)` squash.

The board is `role="img"` with an `aria-label`; individual dots are never announced. Sizes: `.flipdot-sm` (2.5px dots, the wordmark), default (4px), `.flipdot-lg` (7px). Input is uppercased, unknown characters become spaces so the word keeps its rhythm, and boards are truncated at 12 glyphs — past that it stops being a status word and starts being a paragraph.

### Plugin fragment API (a protocol, not a style choice)
The last section of `bindery.css` is API. `docs/PLUGIN-UI.md` rule 3 promises plugins nine custom properties (`--bnd-bg`, `--bnd-surface`, `--bnd-text`, `--bnd-muted`, `--bnd-accent`, `--bnd-danger`, `--bnd-radius`, `--bnd-gap`, `--bnd-font`) and seven classes (`.bnd-card`, `.bnd-btn`, `.bnd-btn-primary`, `.bnd-input`, `.bnd-list`, `.bnd-row`, `.bnd-badge`) inside `#bnd-plugin-ui`. Renaming any of them is a protocol change that breaks every conforming plugin. The values retarget with the theme, so a plugin that uses them inherits light and dark for free — that is the whole promise.

## Do's and Don'ts

### Do:
- **Do** declare every new themed value as a `light-dark()` pair on `:root` in `bindery.css`, so `color-scheme` stays the single switch and the no-JavaScript path stays correct.
- **Do** carry structure with hairlines: `--hairline` for panel edges and section rules, `--hairline-soft` between rows, `--hairline-strong` for control borders and the dashed empty state.
- **Do** put a word beside every state mark, and prefer the flip-dot board when a surface needs to state one status.
- **Do** keep hover to colour + border + ground, transitioned over `--dur-hover` (200ms) with `--ease` (`cubic-bezier(0.16, 1, 0.3, 1)`) — the system's only entrance curve.
- **Do** draw navigation and control icons as inline 24-viewBox SVG at `stroke-width: 1.6`, `fill: none`, in `currentColor` — and use `›` / `‹` / `·` for row affordances, pager direction, and metadata separation, `aria-hidden` in every case.
- **Do** give real elements their semantics: `<progress>` for progress, `<time>`/`<data>` for values, `role="img"` + `aria-label` for a board.
- **Do** freeze motion under `prefers-reduced-motion: reduce` — the block sets `animation: none` on both `.signal-field` and every `.fd`, not merely a shorter duration.
- **Do** hold WCAG AA on both themes, composited against the actual ancestor ground (alpha washes included). This is verified on every route today.
- **Do** treat `--bnd-*` names as a versioned contract with plugins.

### Don't:
- **Don't** write an inline `style` attribute, an inline `<script>`, or a CDN font link. `style-src 'self'`, `script-src 'self'`, `font-src 'self'` — the policy has no `unsafe-inline`, and `<meta name="htmx-config" content='{"includeIndicatorStyles":false}'>` exists precisely because htmx would otherwise inject a blocked `<style>`.
- **Don't** nest a card inside a card. Where a panel-shaped thing must sit inside a panel, strip its chrome down to a hairline the way `.queue-docket > .station` does.
- **Don't** put the 2px accent left-rule on a notice, callout, or alert. It is reserved for the inset Card (`.action-form`).
- **Don't** lift, scale up, or change a shadow on hover.
- **Don't** use a glow, a soft box-shadow, or a colour shift as the focus indicator; focus is a hard 2px ring at 4px offset.
- **Don't** press any other Unicode character into service as an icon — no arrows, no bullets beyond `·`, no emoji, no icon font. Outside the sanctioned set, an icon is drawn SVG or it does not exist.
- **Don't** repeat a fact the board already stated. On a clear floor the board says CLEAR and both the sub-line and the job list disappear; do not add a second badge, count, or status string beside a live one.
- **Don't** add an explanatory paragraph under a section heading. The heading carries its own weight.
- **Don't** introduce a second accent hue, or promote amber from an attention rim to an identity colour.
- **Don't** put more than one loud element on a surface.
- **Don't** add an npm/Node build step, a client-side framework, or a runtime style injector. Server-rendered Razor + vendored htmx is the delivery model.
- **Don't** ship a flat background: the signal field is the ground everywhere.
