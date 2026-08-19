"""Protocol conformance checks — docs/PLUGIN-PROTOCOL.md sections 2-5, 7-9."""

from __future__ import annotations

import hashlib
import re
import uuid

from harness import (
    Ctx,
    Fail,
    Skip,
    check,
    expect,
    expect_error_body,
    expect_status,
    expect_type,
    read_ndjson,
)

NAME_RE = re.compile(r"^[a-z0-9][a-z0-9-]{0,31}$")
CONFIG_KEY_RE = re.compile(r"^[a-z0-9_]{1,64}$")
ACTION_NAME_RE = re.compile(r"^[a-z0-9_]{1,32}$")
ARTIFACT_ID_RE = re.compile(r"^[A-Za-z0-9._-]{1,64}$")
FIELD_TYPES = {"string", "secret", "text", "bool", "int", "select", "url"}
ERROR_CODES = {
    "unsupported_url",
    "not_found",
    "auth_required",
    "rate_limited",
    "network",
    "parse",
    "cancelled",
    "internal",
}
LOG_LEVELS = {"debug", "info", "warn", "error"}

# A URL no plugin can plausibly claim, used to exercise the stream's shape without
# touching a real site.
NONSENSE_URL = "https://conformance.invalid/definitely-not-a-work/0"


# ---------------------------------------------------------------- health & auth


@check("health.responds", group="health")
def health_responds(ctx: Ctx) -> None:
    """GET /healthz returns 200 while the process can serve requests."""
    resp = ctx.client.request("GET", "/healthz", auth=False, timeout=10)
    expect_status(resp, 200)


@check("health.unauthenticated", group="health")
def health_unauthenticated(ctx: Ctx) -> None:
    """/healthz is reachable without a token so orchestrator probes work."""
    resp = ctx.client.request("GET", "/healthz", auth=False, timeout=10)
    expect(
        resp.status == 200,
        "/healthz must not require the bearer token — kubelet probes cannot send one",
    )


@check("auth.rejects-missing-token", group="health", requires=lambda ctx: bool(ctx.args.token))
def auth_rejects_missing_token(ctx: Ctx) -> None:
    """Protocol endpoints reject a request with no Authorization header."""
    resp = ctx.client.request("GET", "/bindery/v1/manifest", auth=False)
    expect_status(resp, 401)
    expect_error_body(resp, "unauthenticated manifest")


@check("auth.rejects-wrong-token", group="health", requires=lambda ctx: bool(ctx.args.token))
def auth_rejects_wrong_token(ctx: Ctx) -> None:
    """Protocol endpoints reject a request with the wrong bearer token."""
    resp = ctx.client.request(
        "GET", "/bindery/v1/manifest", auth=False, headers={"Authorization": "Bearer not-the-token"}
    )
    expect_status(resp, 401)


# ---------------------------------------------------------------- manifest


@check("manifest.served", group="manifest")
def manifest_served(ctx: Ctx) -> None:
    """GET /bindery/v1/manifest returns 200 application/json."""
    resp = ctx.client.request("GET", "/bindery/v1/manifest", timeout=10)
    expect_status(resp, 200)
    expect(
        resp.content_type == "application/json",
        f"manifest Content-Type must be application/json, got {resp.content_type!r}",
    )
    expect(len(resp.body) <= 256 * 1024, "manifest exceeds the 256 KiB limit")
    manifest = resp.json()
    expect_type(manifest, dict, "manifest")
    ctx.manifest = manifest


@check("manifest.protocol-version", group="manifest")
def manifest_protocol_version(ctx: Ctx) -> None:
    """protocolVersion is present and is an integer this suite understands."""
    version = ctx.manifest.get("protocolVersion")
    expect(version is not None, "manifest is missing 'protocolVersion'")
    expect_type(version, int, "protocolVersion")
    expect(version == 1, f"this suite implements protocolVersion 1, plugin declares {version}")


@check("manifest.identity", group="manifest")
def manifest_identity(ctx: Ctx) -> None:
    """name matches the required pattern; version is a non-empty string."""
    name = ctx.manifest.get("name")
    expect(isinstance(name, str) and NAME_RE.match(name) is not None,
           f"'name' must match {NAME_RE.pattern}, got {name!r}")
    version = ctx.manifest.get("version")
    expect(isinstance(version, str) and version.strip() != "", "'version' must be a non-empty string")
    for key in ("displayName", "description", "homepage"):
        if key in ctx.manifest:
            expect_type(ctx.manifest[key], str, key)
    homepage = ctx.manifest.get("homepage")
    if homepage:
        expect(homepage.startswith("https://") or homepage.startswith("http://"),
               "'homepage' must be an absolute URL")
    priority = ctx.manifest.get("priority", 0)
    expect_type(priority, int, "priority")


@check("manifest.matches-compile", group="manifest")
def manifest_matches_compile(ctx: Ctx) -> None:
    """Every entry in 'matches' is a compilable regex of sane length."""
    matches = ctx.manifest.get("matches", [])
    expect_type(matches, list, "matches")
    for pattern in matches:
        expect_type(pattern, str, "matches entry")
        expect(len(pattern) <= 512, f"regex is longer than 512 characters: {pattern[:60]}...")
        try:
            re.compile(pattern)
        except re.error as exc:
            raise Fail(f"regex does not compile: {pattern!r} ({exc})")
    if not matches and not ctx.caps.get("probe"):
        ctx.note("plugin declares no 'matches' and no probe capability — it can only be selected explicitly")


@check("manifest.capabilities", group="manifest")
def manifest_capabilities(ctx: Ctx) -> None:
    """capabilities members are booleans."""
    caps = ctx.manifest.get("capabilities", {})
    expect_type(caps, dict, "capabilities")
    for key, value in caps.items():
        expect_type(value, bool, f"capabilities.{key}")


@check("manifest.formats", group="manifest")
def manifest_formats(ctx: Ctx) -> None:
    """formats is a list of lowercase, dotless extensions."""
    formats = ctx.manifest.get("formats", ["epub"])
    expect_type(formats, list, "formats")
    expect(len(formats) > 0, "'formats' must not be empty when present")
    for fmt in formats:
        expect_type(fmt, str, "formats entry")
        expect(fmt == fmt.lower() and "." not in fmt, f"format must be a lowercase extension without a dot: {fmt!r}")


def _check_fields(fields, where: str) -> None:
    expect_type(fields, list, where)
    seen = set()
    for field_def in fields:
        expect_type(field_def, dict, f"{where} entry")
        key = field_def.get("key")
        expect(isinstance(key, str) and CONFIG_KEY_RE.match(key) is not None,
               f"{where}: 'key' must match {CONFIG_KEY_RE.pattern}, got {key!r}")
        expect(key not in seen, f"{where}: duplicate key {key!r}")
        seen.add(key)
        expect(isinstance(field_def.get("label"), str) and field_def["label"].strip() != "",
               f"{where}.{key}: 'label' is required")
        ftype = field_def.get("type")
        expect(ftype in FIELD_TYPES, f"{where}.{key}: 'type' must be one of {sorted(FIELD_TYPES)}, got {ftype!r}")
        if ftype == "select":
            options = field_def.get("options")
            expect(isinstance(options, list) and options, f"{where}.{key}: select fields need non-empty 'options'")
            for opt in options:
                expect(isinstance(opt, dict) and isinstance(opt.get("value"), str),
                       f"{where}.{key}: each option needs a string 'value'")
        if ftype == "secret":
            expect("default" not in field_def, f"{where}.{key}: a secret field must not declare a default")
        if "required" in field_def:
            expect_type(field_def["required"], bool, f"{where}.{key}.required")
        if "pattern" in field_def:
            try:
                re.compile(field_def["pattern"])
            except re.error as exc:
                raise Fail(f"{where}.{key}: 'pattern' does not compile ({exc})")


@check("manifest.config-schema", group="manifest")
def manifest_config_schema(ctx: Ctx) -> None:
    """Every config field descriptor is well-formed."""
    _check_fields(ctx.manifest.get("config", []), "config")


@check("manifest.actions-schema", group="manifest")
def manifest_actions_schema(ctx: Ctx) -> None:
    """Every declared action has a valid name, input schema, and output kind."""
    actions = ctx.manifest.get("actions", [])
    expect_type(actions, list, "actions")
    seen = set()
    for action in actions:
        expect_type(action, dict, "actions entry")
        name = action.get("name")
        expect(isinstance(name, str) and ACTION_NAME_RE.match(name) is not None,
               f"action 'name' must match {ACTION_NAME_RE.pattern}, got {name!r}")
        expect(name not in seen, f"duplicate action {name!r}")
        seen.add(name)
        expect(isinstance(action.get("label"), str) and action["label"].strip() != "",
               f"action {name}: 'label' is required")
        _check_fields(action.get("input", []), f"actions.{name}.input")
        output = action.get("output", {})
        expect_type(output, dict, f"actions.{name}.output")
        kind = output.get("kind")
        expect(kind in {"list", "text", "message"},
               f"actions.{name}: output.kind must be list|text|message, got {kind!r}")
        if kind == "list" and "itemAction" in output:
            expect(output["itemAction"] in {"download", "none"},
                   f"actions.{name}: output.itemAction must be download|none")


@check("manifest.ui", group="manifest")
def manifest_ui(ctx: Ctx) -> None:
    """ui.mode is a known tier and nav entries are well-formed."""
    ui = ctx.manifest.get("ui", {})
    expect_type(ui, dict, "ui")
    mode = ui.get("mode", "declarative")
    expect(mode in {"declarative", "fragment", "iframe"},
           f"ui.mode must be declarative|fragment|iframe, got {mode!r}")
    for entry in ui.get("nav", []):
        expect_type(entry, dict, "ui.nav entry")
        expect(isinstance(entry.get("label"), str) and entry["label"].strip() != "",
               "ui.nav entry needs a 'label'")
        path = entry.get("path")
        expect(isinstance(path, str) and path.startswith("/"),
               f"ui.nav 'path' must start with '/', got {path!r}")
        expect(".." not in path, f"ui.nav 'path' must not traverse: {path!r}")
    if mode == "declarative" and ui.get("nav"):
        ctx.note("ui.mode is 'declarative' but ui.nav is declared — nav is only meaningful for fragment/iframe")


@check("manifest.stable", group="manifest")
def manifest_stable(ctx: Ctx) -> None:
    """The manifest is stable across requests (it is cached by the host)."""
    first = ctx.client.request("GET", "/bindery/v1/manifest", timeout=10).json()
    second = ctx.client.request("GET", "/bindery/v1/manifest", timeout=10).json()
    if first != second:
        ctx.note("manifest changed between two consecutive requests; the host caches it, so it should not")


# ---------------------------------------------------------------- probe


@check("probe.answers", group="probe", requires=lambda ctx: bool(ctx.caps.get("probe")))
def probe_answers(ctx: Ctx) -> None:
    """POST /probe returns {supported, confidence} for a URL the plugin claims."""
    url = ctx.args.download_url or _sample_url(ctx)
    if not url:
        raise Skip("no URL available to probe (pass --download-url)")
    resp = ctx.client.request("POST", "/bindery/v1/probe", json_body={"url": url}, timeout=5)
    expect_status(resp, 200)
    body = resp.json()
    expect_type(body, dict, "probe response")
    expect_type(body.get("supported"), bool, "probe.supported")
    if "confidence" in body:
        conf = body["confidence"]
        expect_type(conf, (int, float), "probe.confidence")
        expect(0.0 <= float(conf) <= 1.0, f"probe.confidence must be within [0,1], got {conf}")


@check("probe.declines-nonsense", group="probe", requires=lambda ctx: bool(ctx.caps.get("probe")))
def probe_declines_nonsense(ctx: Ctx) -> None:
    """POST /probe declines a URL no plugin could handle."""
    resp = ctx.client.request("POST", "/bindery/v1/probe", json_body={"url": NONSENSE_URL}, timeout=5)
    expect_status(resp, 200)
    expect(resp.json().get("supported") is False, f"probe claimed to support {NONSENSE_URL}")


@check("probe.rejects-malformed", group="probe", requires=lambda ctx: bool(ctx.caps.get("probe")))
def probe_rejects_malformed(ctx: Ctx) -> None:
    """POST /probe with no 'url' is a 400 or 422 with an error body."""
    resp = ctx.client.request("POST", "/bindery/v1/probe", json_body={}, timeout=5)
    expect_status(resp, 400, 422)
    expect_error_body(resp, "malformed probe")


def _sample_url(ctx: Ctx):
    """Best-effort concrete URL derived from the manifest's own regexes."""
    for pattern in ctx.manifest.get("matches", []):
        literal = re.sub(r"\\d\+?", "12345", pattern)
        literal = re.sub(r"\(([^()|]*)\|[^()]*\)", r"\1", literal)
        literal = literal.replace("(www\\.)?", "").replace("^", "").replace("$", "")
        literal = literal.replace("\\.", ".").replace("\\/", "/").replace("?", "")
        if literal.startswith("https") or literal.startswith("http"):
            return literal
    return None


# ---------------------------------------------------------------- download


def _download(ctx: Ctx, url: str, *, options=None, timeout: float = 120.0):
    job_id = str(uuid.uuid4())
    payload = {"jobId": job_id, "url": url, "config": ctx.args.config, "options": options or {}}
    status, headers, fp = ctx.client.stream(
        "POST",
        "/bindery/v1/download",
        json_body=payload,
        headers={"X-Bindery-Job-Id": job_id},
        timeout=timeout,
    )
    try:
        expect(status == 200, f"download must answer 200 and report failures as a result event, got {status}")
        ctype = headers.get("content-type", "").split(";")[0].strip().lower()
        expect(ctype == "application/x-ndjson",
               f"download Content-Type must be application/x-ndjson, got {ctype!r}")
        events = list(read_ndjson(fp))
    finally:
        fp.close()

    expect(events, "download produced no events at all")
    results = [e for e in events if e.get("event") == "result"]
    expect(len(results) == 1, f"exactly one 'result' event is required, got {len(results)}")
    expect(events[-1].get("event") == "result", "the 'result' event must be the last line of the stream")

    last_percent = -1.0
    for event in events:
        kind = event.get("event")
        expect(isinstance(kind, str), "every event needs a string 'event' member")
        if kind == "progress":
            if "percent" in event and event["percent"] is not None:
                percent = event["percent"]
                expect_type(percent, (int, float), "progress.percent")
                expect(0 <= percent <= 100, f"progress.percent must be within [0,100], got {percent}")
                if percent < last_percent:
                    ctx.note(f"progress.percent went backwards ({last_percent} then {percent})")
                last_percent = float(percent)
            if "message" in event and event["message"] is not None:
                expect_type(event["message"], str, "progress.message")
        elif kind == "log":
            expect(event.get("level") in LOG_LEVELS,
                   f"log.level must be one of {sorted(LOG_LEVELS)}, got {event.get('level')!r}")
            expect_type(event.get("message"), str, "log.message")

    result = results[0]
    status_value = result.get("status")
    expect(status_value in {"ok", "unchanged", "error"},
           f"result.status must be ok|unchanged|error, got {status_value!r}")

    if status_value == "error":
        err = result.get("error")
        expect_type(err, dict, "result.error")
        code = err.get("code")
        expect(isinstance(code, str) and code != "", "result.error.code is required")
        if code not in ERROR_CODES:
            ctx.note(f"unknown error code {code!r} — the host treats it as 'internal'")
        expect_type(err.get("message"), str, "result.error.message")
        if "retryable" in err:
            expect_type(err["retryable"], bool, "result.error.retryable")
    elif status_value == "unchanged":
        expect(not result.get("artifacts"), "an 'unchanged' result must carry no artifacts")
    else:
        _check_ok_result(result)

    return job_id, events, result


def _check_ok_result(result: dict) -> None:
    artifacts = result.get("artifacts")
    expect(isinstance(artifacts, list) and artifacts, "an 'ok' result must carry at least one artifact")
    primaries = [a for a in artifacts if a.get("primary")]
    expect(len(primaries) == 1, f"exactly one artifact must be primary, got {len(primaries)}")
    ids = set()
    for artifact in artifacts:
        expect_type(artifact, dict, "artifact")
        art_id = artifact.get("id")
        expect(isinstance(art_id, str) and ARTIFACT_ID_RE.match(art_id) is not None,
               f"artifact 'id' must match {ARTIFACT_ID_RE.pattern}, got {art_id!r}")
        expect(art_id not in ids, f"duplicate artifact id {art_id!r}")
        ids.add(art_id)
        filename = artifact.get("filename")
        expect(isinstance(filename, str) and filename != "", "artifact 'filename' is required")
        expect("/" not in filename and "\\" not in filename,
               f"artifact 'filename' must not contain path separators: {filename!r}")
        fmt = artifact.get("format")
        expect(isinstance(fmt, str) and fmt == fmt.lower() and fmt != "",
               f"artifact 'format' must be a lowercase extension, got {fmt!r}")
        if "kind" in artifact:
            expect(artifact["kind"] in {"book", "cover"},
                   f"artifact 'kind' must be book|cover, got {artifact['kind']!r}")
        if "sha256" in artifact and artifact["sha256"] is not None:
            expect(re.fullmatch(r"[0-9a-f]{64}", artifact["sha256"]) is not None,
                   "artifact 'sha256' must be 64 lowercase hex characters")

    metadata = result.get("metadata")
    expect_type(metadata, dict, "result.metadata")
    title = metadata.get("title")
    expect(isinstance(title, str) and title.strip() != "", "metadata.title is required and must be non-empty")
    for key in ("authors", "tags"):
        if key in metadata and metadata[key] is not None:
            expect_type(metadata[key], list, f"metadata.{key}")
            for item in metadata[key]:
                expect_type(item, str, f"metadata.{key} entry")
    if metadata.get("chapters") is not None:
        expect_type(metadata["chapters"], int, "metadata.chapters")
    if metadata.get("seriesIndex") is not None:
        expect_type(metadata["seriesIndex"], (int, float), "metadata.seriesIndex")
    for key in ("published", "updated"):
        value = metadata.get(key)
        if value:
            expect(re.match(r"^\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}", value) is not None,
                   f"metadata.{key} must be an RFC 3339 timestamp, got {value!r}")


@check("download.rejects-malformed", group="download")
def download_rejects_malformed(ctx: Ctx) -> None:
    """POST /download without a 'url' is rejected before the stream starts."""
    resp = ctx.client.request("POST", "/bindery/v1/download", json_body={"jobId": str(uuid.uuid4())}, timeout=15)
    expect_status(resp, 400, 422)
    expect_error_body(resp, "malformed download")


@check("download.stream-shape", group="download")
def download_stream_shape(ctx: Ctx) -> None:
    """A download of an unclaimable URL still streams a well-formed NDJSON result."""
    _, _, result = _download(ctx, NONSENSE_URL, timeout=60)
    if result["status"] == "ok":
        ctx.note(f"plugin claims to have downloaded {NONSENSE_URL} — check its URL handling")


@check("download.real", group="download", requires=lambda ctx: bool(ctx.args.download_url))
def download_real(ctx: Ctx) -> None:
    """A download of --download-url succeeds and carries usable metadata."""
    job_id, events, result = _download(ctx, ctx.args.download_url, timeout=ctx.args.download_timeout)
    if result["status"] == "error":
        raise Fail(f"download failed: {result['error'].get('code')}: {result['error'].get('message')}")
    if result["status"] == "unchanged":
        raise Fail("a first download must not answer 'unchanged'")
    ctx.state["job_id"] = job_id
    ctx.state["artifacts"] = result["artifacts"]
    if not any(e.get("event") == "progress" for e in events):
        ctx.note("no progress events were emitted; long downloads look frozen in the UI")
    if not result["metadata"].get("sourceId"):
        ctx.note("metadata.sourceId is absent — updates and duplicate detection depend on it")


@check("artifacts.retrievable", group="artifacts", requires=lambda ctx: "job_id" in ctx.state)
def artifacts_retrievable(ctx: Ctx) -> None:
    """Every declared artifact is retrievable with a content type, and hashes match."""
    job_id = ctx.state["job_id"]
    for artifact in ctx.state["artifacts"]:
        path = f"/bindery/v1/artifacts/{job_id}/{artifact['id']}"
        resp = ctx.client.request("GET", path, timeout=120, max_bytes=512 * 1024 * 1024)
        expect_status(resp, 200)
        expect(resp.content_type != "", f"{path}: a Content-Type is required")
        expect(len(resp.body) > 0, f"{path}: artifact is empty")
        declared = artifact.get("sha256")
        if declared:
            actual = hashlib.sha256(resp.body).hexdigest()
            expect(actual == declared, f"{path}: sha256 mismatch (declared {declared}, got {actual})")
        length = resp.header("content-length")
        if length:
            expect(int(length) == len(resp.body), f"{path}: Content-Length disagrees with the body")


@check("artifacts.unknown-is-404", group="artifacts")
def artifacts_unknown_is_404(ctx: Ctx) -> None:
    """An unknown job or artifact is a 404, not a 500 and not an empty 200."""
    resp = ctx.client.request("GET", f"/bindery/v1/artifacts/{uuid.uuid4()}/nope", timeout=15)
    expect_status(resp, 404)


@check("artifacts.no-traversal", group="artifacts")
def artifacts_no_traversal(ctx: Ctx) -> None:
    """Artifact ids that try to traverse the filesystem are refused."""
    for evil in ("..%2f..%2fetc%2fpasswd", "..", "%2e%2e%2f%2e%2e%2fetc%2fpasswd"):
        resp = ctx.client.request("GET", f"/bindery/v1/artifacts/{uuid.uuid4()}/{evil}", timeout=15)
        expect(resp.status in (400, 404),
               f"traversal attempt {evil!r} returned {resp.status}; expected 400 or 404")


# ---------------------------------------------------------------- cancel


@check("jobs.delete-is-idempotent", group="jobs")
def jobs_delete_idempotent(ctx: Ctx) -> None:
    """DELETE of an unknown job is 204, and repeating it stays 204."""
    unknown = str(uuid.uuid4())
    for _ in range(2):
        resp = ctx.client.request("DELETE", f"/bindery/v1/jobs/{unknown}", timeout=15)
        expect_status(resp, 204)


@check("jobs.delete-removes-artifacts", group="jobs", requires=lambda ctx: "job_id" in ctx.state)
def jobs_delete_removes_artifacts(ctx: Ctx) -> None:
    """After DELETE, the job's artifacts are gone."""
    job_id = ctx.state["job_id"]
    artifact = ctx.state["artifacts"][0]
    resp = ctx.client.request("DELETE", f"/bindery/v1/jobs/{job_id}", timeout=30)
    expect_status(resp, 204)
    after = ctx.client.request("GET", f"/bindery/v1/artifacts/{job_id}/{artifact['id']}", timeout=15)
    expect_status(after, 404)
    ctx.state.pop("job_id", None)


# ---------------------------------------------------------------- actions


@check("actions.unknown-is-404", group="actions",
       requires=lambda ctx: bool(ctx.manifest.get("actions")))
def actions_unknown_is_404(ctx: Ctx) -> None:
    """Invoking an action the manifest does not declare is a 404."""
    resp = ctx.client.request(
        "POST",
        "/bindery/v1/actions/definitely_not_declared",
        json_body={"action": "definitely_not_declared", "input": {}, "config": ctx.args.config},
        timeout=30,
    )
    expect_status(resp, 404)


@check("actions.invoke", group="actions",
       requires=lambda ctx: bool(ctx.manifest.get("actions")) and bool(ctx.args.action_input))
def actions_invoke(ctx: Ctx) -> None:
    """Invoking a declared action returns output matching its declared kind."""
    for name, input_values in ctx.args.action_input.items():
        declared = next((a for a in ctx.manifest.get("actions", []) if a.get("name") == name), None)
        if declared is None:
            raise Fail(f"--action-input names {name!r}, which the manifest does not declare")
        resp = ctx.client.request(
            "POST",
            f"/bindery/v1/actions/{name}",
            json_body={"action": name, "input": input_values, "config": ctx.args.config},
            timeout=30,
        )
        expect_status(resp, 200)
        body = resp.json()
        if body.get("status") == "error":
            raise Fail(f"action {name} failed: {body.get('error')}")
        expect(body.get("status") == "ok", f"action {name}: status must be ok|error, got {body.get('status')!r}")
        output = body.get("output")
        expect_type(output, dict, f"action {name}: output")
        kind = output.get("kind")
        expect(kind == declared["output"]["kind"],
               f"action {name}: returned kind {kind!r}, manifest declares {declared['output']['kind']!r}")
        if kind == "list":
            items = output.get("items")
            expect_type(items, list, f"action {name}: output.items")
            for item in items:
                expect_type(item, dict, f"action {name}: item")
                expect(isinstance(item.get("title"), str), f"action {name}: each item needs a string 'title'")
                if declared["output"].get("itemAction") == "download":
                    expect(isinstance(item.get("url"), str) and item["url"].startswith("http"),
                           f"action {name}: itemAction 'download' requires an absolute 'url' on every item")
        elif kind == "text":
            expect_type(output.get("text"), str, f"action {name}: output.text")
        elif kind == "message":
            expect_type(output.get("message"), str, f"action {name}: output.message")
