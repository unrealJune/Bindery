# Product

<!-- impeccable:product-schema 1 -->

## Platform

web

## Users

The primary user is a single self-hoster managing a private personal ebook and fanfiction library.

## Product Purpose

Bindery acquires books through isolated HTTP plugins, files them into a human-browsable library,
and serves that library to reading applications through OPDS 1.2 and 2.0. Success means one
operator can add, monitor, organize, and retrieve books without maintaining downloader
dependencies inside the Bindery host.

## Positioning

Downloader plugins are independently deployable HTTP containers. Bindery never loads plugin
code in-process, starts plugin subprocesses, or shares a writable library volume with plugins.

## Operating Context

Bindery runs as a self-hosted ASP.NET Core service, commonly behind an ingress or reverse proxy.
The operator manages downloads and plugin settings in a server-rendered web UI; ereaders consume
the same library through token-authenticated OPDS feeds.

## Capabilities and Constraints

- ASP.NET Core 8 host, F# serialization core, Razor Pages with htmx, and SQLite via EF Core.
- Plugins are discovered from configuration and communicate only through the versioned HTTP
  protocol.
- Library files are the source of truth; SQLite is a rebuildable index.
- Plugin secrets are encrypted at rest and never rendered back in cleartext.
- Fragment plugin UIs contain no JavaScript and pass through a maintained sanitizer, CSP, and
  Bindery-owned antiforgery protection.
- The v1 scope excludes scheduling, watch-folder import, progress sync, multi-user libraries,
  full-text search, and a plugin marketplace.

## Brand Commitments

The product name is Bindery. Its voice should be calm, direct, technically trustworthy, and
appropriate for a private library tool rather than a consumer storefront.

## Evidence on Hand

`PLAN.md`, `docs/PLUGIN-PROTOCOL.md`, and `docs/PLUGIN-UI.md` define the product and protocol.
The repository contains the FanFicFare reference plugin, protocol conformance suite, OPDS golden
feeds, and the partially implemented host.

## Product Principles

- Keep downloader dependencies outside the host.
- Make files portable and recoverable without the database.
- Prefer explicit, inspectable behavior over discovery magic.
- Treat content types, authentication boundaries, and sanitization as compatibility and security
  requirements rather than polish.
- Optimize the operator interface for quick scanning and confident recovery from failures.

## Accessibility & Inclusion

The web interface should meet WCAG 2.2 AA expectations, remain fully keyboard operable, preserve
clear focus states, and respect reduced-motion and color-scheme preferences.
