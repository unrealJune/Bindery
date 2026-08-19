# Plugin conformance suite

Black-box verification that a container is a Bindery plugin. It talks to a plugin over
HTTP and nothing else — no Bindery, no .NET, no third-party Python packages.

```bash
# against a plugin you are running
python tests/conformance/run.py --base-url http://localhost:8080 --token dev

# self-test: starts the bundled stub plugin and runs everything against it
python tests/conformance/run.py --self-test
```

Exit code is `0` when every check passed and `1` otherwise, so it drops straight into CI.

## Useful flags

| Flag | Purpose |
|---|---|
| `--download-url URL` | A URL the plugin claims. Unlocks the real-download, artifact, and cleanup checks — without it, only the stream's *shape* is verified. |
| `--config KEY=VALUE` | Plugin config, repeatable. Site credentials go here. |
| `--action-input ACTION:KEY=VALUE` | Input for a declared action, repeatable. Unlocks `actions.invoke`. |
| `--download-timeout SECONDS` | Default 300. Real fetches of long works need more. |
| `--only NAME_OR_GROUP` | Run one check or one group (`health`, `manifest`, `probe`, `download`, `artifacts`, `jobs`, `actions`, `ui`). |
| `--list` | Print every check and what it asserts. |

Checks that do not apply are skipped, not failed: probe checks need
`capabilities.probe`, UI checks need `ui.mode` of `fragment` or `iframe`, artifact checks
need a successful real download.

## Layout

```
run.py               entry point, argument parsing, self-test bootstrap
harness.py           HTTP client, NDJSON reader, check registry, runner
checks_protocol.py   sections 2-5, 7-9 of docs/PLUGIN-PROTOCOL.md
checks_ui.py         docs/PLUGIN-UI.md — fragments, no-JS, X-Bindery-Base, reflection
vectors/xss.json     shared XSS corpus (also read by the host's sanitizer tests)
stub/stub_plugin.py  a complete, correct plugin in ~450 stdlib lines
```

## The stub

`stub/stub_plugin.py` fabricates a small but structurally valid EPUB and serves every
endpoint the protocol defines, including a `fragment` UI and two actions. It is the fixture
for this suite's own self-test and for the host's integration tests, and it doubles as the
worked example for anyone writing a plugin in another language.

```bash
BINDERY_PLUGIN_TOKEN=dev BINDERY_PLUGIN_PORT=8080 python tests/conformance/stub/stub_plugin.py
curl -H 'Authorization: Bearer dev' localhost:8080/bindery/v1/manifest
```

It claims `https://stub.invalid/works/{id}` and nothing else.

## Adding a check

Write a function in the relevant module, decorate it, raise `Fail` for a violation and
`Skip` when it does not apply:

```python
@check("download.emits-progress", group="download")
def download_emits_progress(ctx: Ctx) -> None:
    """A download of a multi-chapter work reports progress."""
    expect(condition, "message explaining what the plugin should have done")
```

Two rules keep the suite honest:

- **Every check cites the spec.** If `docs/PLUGIN-PROTOCOL.md` does not require it, it is a
  `ctx.note(...)`, not a failure.
- **Every sanitizer allowlist entry gets a vector.** `vectors/xss.json` is the merge gate
  for `fragment` plugins on both sides of the proxy; widening what the host permits without
  adding a vector is how that gate quietly stops working.
