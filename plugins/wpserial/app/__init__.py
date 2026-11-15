"""WP Serial: a Bindery plugin that binds web serials published on WordPress into EPUBs.

It reads the site the way a reader does — a table of contents, then each chapter, then the
"Next Chapter" links past the end of the TOC — so it works on self-hosted WordPress and
WordPress.com alike, with no per-site adapter. Like every Bindery plugin it is an HTTP server
in a container that shares nothing with the host but `docs/PLUGIN-PROTOCOL.md`.
"""
