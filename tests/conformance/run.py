#!/usr/bin/env python3
"""Bindery plugin conformance suite.

Black box. Point it at a running plugin container; Bindery is not involved.

    python tests/conformance/run.py --base-url http://localhost:8080 --token dev

Add a URL the plugin actually claims to exercise a real download end to end:

    python tests/conformance/run.py --base-url http://localhost:8080 --token dev \
        --download-url https://archiveofourown.org/works/12345

Self-test the suite itself against the bundled reference stub:

    python tests/conformance/run.py --self-test
"""

from __future__ import annotations

import argparse
import os
import subprocess
import sys
import time
import urllib.error
import urllib.request

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import checks_protocol  # noqa: F401,E402  (import registers checks)
import checks_ui  # noqa: F401,E402
from harness import REGISTRY, Client, Ctx, run_all  # noqa: E402

STUB = os.path.join(os.path.dirname(os.path.abspath(__file__)), "stub", "stub_plugin.py")


def parse_kv(values, what):
    result = {}
    for item in values or []:
        if "=" not in item:
            raise SystemExit(f"--{what} expects key=value, got {item!r}")
        key, _, value = item.partition("=")
        result[key.strip()] = value
    return result


def parse_action_input(values):
    """--action-input search:q=hobbit"""
    result = {}
    for item in values or []:
        if ":" not in item or "=" not in item:
            raise SystemExit(f"--action-input expects action:key=value, got {item!r}")
        action, _, rest = item.partition(":")
        key, _, value = rest.partition("=")
        result.setdefault(action.strip(), {})[key.strip()] = value
    return result


def wait_for_health(base_url: str, timeout: float = 20.0) -> bool:
    deadline = time.time() + timeout
    while time.time() < deadline:
        try:
            with urllib.request.urlopen(base_url.rstrip("/") + "/healthz", timeout=2) as resp:
                if resp.status == 200:
                    return True
        except (urllib.error.URLError, TimeoutError, ConnectionError):
            time.sleep(0.2)
    return False


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description="Bindery plugin conformance suite")
    parser.add_argument("--base-url", help="plugin base URL, e.g. http://localhost:8080")
    parser.add_argument("--token", default=os.environ.get("BINDERY_PLUGIN_TOKEN"),
                        help="shared bearer token the plugin was started with")
    parser.add_argument("--download-url", help="a URL the plugin claims, to exercise a real download")
    parser.add_argument("--download-timeout", type=float, default=300.0,
                        help="seconds to allow a real download (default 300)")
    parser.add_argument("--config", action="append", metavar="KEY=VALUE",
                        help="plugin config value, repeatable")
    parser.add_argument("--action-input", action="append", metavar="ACTION:KEY=VALUE",
                        help="input for an action to invoke, repeatable")
    parser.add_argument("--only", help="run only checks whose name contains this, or a whole group")
    parser.add_argument("--list", action="store_true", help="list checks and exit")
    parser.add_argument("--no-color", action="store_true")
    parser.add_argument("--self-test", action="store_true",
                        help="start the bundled stub plugin and run the suite against it")
    parser.add_argument("--stub-ui-mode", default="fragment", choices=["fragment", "sandboxed"],
                        help="UI tier the self-test stub presents (default fragment)")
    args = parser.parse_args(argv)

    if args.list:
        for chk in REGISTRY:
            print(f"{chk.group:10} {chk.name:34} {chk.description}")
        return 0

    args.config = parse_kv(args.config, "config")
    args.action_input = parse_action_input(args.action_input)

    stub = None
    if args.self_test:
        port = 8791
        args.base_url = f"http://127.0.0.1:{port}"
        args.token = args.token or "conformance-self-test"
        args.download_url = args.download_url or "https://stub.invalid/works/1"
        args.action_input = args.action_input or {"echo": {"text": "hello"}}
        env = dict(os.environ, BINDERY_PLUGIN_TOKEN=args.token, BINDERY_PLUGIN_PORT=str(port),
                   BINDERY_STUB_UI_MODE=args.stub_ui_mode)
        stub = subprocess.Popen([sys.executable, STUB], env=env,
                                stdout=subprocess.DEVNULL, stderr=subprocess.STDOUT)
        if not wait_for_health(args.base_url):
            stub.terminate()
            print("stub plugin did not become healthy", file=sys.stderr)
            return 2

    if not args.base_url:
        parser.error("--base-url is required (or use --self-test)")

    try:
        client = Client(args.base_url, args.token)
        ctx = Ctx(client=client, args=args)

        # Bootstrap the manifest so capability-gated checks can decide whether they apply,
        # even when --only skips the manifest check that normally loads it.
        try:
            resp = client.request("GET", "/bindery/v1/manifest", timeout=10)
            if resp.status == 200:
                ctx.manifest = resp.json()
        except Exception:
            pass

        print(f"Bindery plugin conformance — {args.base_url}")
        if ctx.manifest:
            print(f"  {ctx.manifest.get('displayName') or ctx.manifest.get('name')} "
                  f"{ctx.manifest.get('version')}  (ui: {ctx.ui_mode})")
        else:
            print("  manifest unavailable — most checks will fail")

        return run_all(ctx, only=args.only, color=not args.no_color and sys.stdout.isatty())
    finally:
        if stub is not None:
            stub.terminate()
            try:
                stub.wait(timeout=5)
            except subprocess.TimeoutExpired:
                stub.kill()


if __name__ == "__main__":
    sys.exit(main())
