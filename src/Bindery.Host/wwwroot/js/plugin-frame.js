/*
    The parent half of the sandboxed plugin bridge — docs/PLUGIN-UI.md §4.

    A sandboxed plugin's document runs in an iframe with no `allow-same-origin`, so it holds
    an opaque origin: no cookie, no parent DOM, no storage, and — via `connect-src 'none'` in
    the frame's CSP — no network of its own. This file is therefore the *entire* channel
    between a plugin's interface and Bindery.

    That makes it the security-critical surface of the tier, which is why it is small,
    non-parsing, and allowlist-driven. Read it in one sitting; keep it that way.

    Two invariants worth stating before touching anything here:

      · Every inbound message is checked to have come from the frame's own contentWindow.
        `event.origin` is the string "null" for an opaque origin and is therefore worthless
        as identity — several sandboxed frames would all claim it. The window reference is
        the thing that cannot be forged.

      · Outbound messages use targetOrigin "*", because there is no origin string that could
        name an opaque origin instead. Nothing secret is ever sent through this channel; in
        particular the CSRF token stays here and is attached by us, so a plugin cannot leak
        what it is never given.
*/
(() => {
    const frame = document.querySelector("[data-plugin-frame]");

    if (!frame) {
        return;
    }

    const plugin = frame.dataset.plugin;
    const base = frame.dataset.base;
    const csrf = frame.dataset.csrf;
    const status = document.querySelector("[data-plugin-status]");

    /* Anything not on this list is refused rather than sanitized into shape. */
    const METHODS = new Set(["GET", "POST", "PUT", "PATCH", "DELETE"]);
    const MAX_BODY = 1024 * 1024;
    const MAX_HEIGHT = 15000;
    const MAX_INFLIGHT = 8;

    let inflight = 0;

    const send = (message) => frame.contentWindow?.postMessage({ bindery: 1, ...message }, "*");

    /*
        Reduces a plugin-supplied path to one inside that plugin's own mount point, or to
        null. `new URL` does the normalizing so that `..` segments are resolved *before* the
        prefix is checked rather than after — checking first and normalizing later is how
        traversal bugs happen.
    */
    const resolvePath = (raw) => {
        if (typeof raw !== "string" || raw === "" || raw.includes("\\") || !raw.startsWith("/") || raw.startsWith("//")) {
            return null;
        }

        let url;

        try {
            url = new URL(raw, window.location.origin);
        } catch (error) {
            return null;
        }

        if (url.origin !== window.location.origin) {
            return null;
        }

        if (url.pathname !== base && !url.pathname.startsWith(base + "/")) {
            return null;
        }

        return url.pathname + url.search;
    };

    const announce = (text) => {
        if (status) {
            status.textContent = text;
        }
    };

    const perform = async (message) => {
        const id = message.id;
        const method = String(message.method ?? "GET").toUpperCase();
        const path = resolvePath(message.path);

        if (!path) {
            send({ type: "error", id, message: `path is outside ${base}` });
            return;
        }

        if (!METHODS.has(method)) {
            send({ type: "error", id, message: `method ${method} is not allowed` });
            return;
        }

        const body = message.body;

        if (typeof body === "string" && body.length > MAX_BODY) {
            send({ type: "error", id, message: "request body is too large" });
            return;
        }

        if (inflight >= MAX_INFLIGHT) {
            send({ type: "error", id, message: "too many requests in flight" });
            return;
        }

        inflight += 1;

        try {
            const headers = { "X-CSRF-TOKEN": csrf };

            if (method !== "GET" && typeof message.contentType === "string") {
                headers["Content-Type"] = message.contentType;
            }

            const response = await fetch(path, {
                method,
                headers,
                body: method === "GET" ? undefined : body,
                credentials: "same-origin",
                redirect: "error"
            });

            send({
                type: "response",
                id,
                status: response.status,
                contentType: response.headers.get("content-type") ?? "",
                body: await response.text()
            });
        } catch (error) {
            send({ type: "error", id, message: "request failed" });
        } finally {
            inflight -= 1;
        }
    };

    window.addEventListener("message", (event) => {
        /* Identity is the window, never the origin string. */
        if (event.source !== frame.contentWindow) {
            return;
        }

        const message = event.data;

        if (!message || message.bindery !== 1 || typeof message.type !== "string") {
            return;
        }

        switch (message.type) {
            case "ready":
                send({
                    type: "init",
                    plugin,
                    base,
                    theme: document.documentElement.getAttribute("data-theme") ?? "auto",
                    csrf: false
                });
                frame.classList.add("is-ready");
                announce("");
                break;

            case "resize": {
                const height = Number(message.height);

                if (Number.isFinite(height) && height > 0) {
                    frame.style.height = `${Math.min(Math.round(height), MAX_HEIGHT)}px`;
                }

                break;
            }

            case "request":
                if (typeof message.id === "string") {
                    perform(message);
                }

                break;

            case "notify":
                /* Text, assigned as text. It never becomes markup on this side. */
                if (typeof message.message === "string") {
                    announce(message.message.slice(0, 300));
                }

                break;

            case "navigate": {
                const path = resolvePath(message.path);

                if (path) {
                    window.history.replaceState(null, "", path);
                }

                break;
            }

            default:
                /* Unknown message types are ignored, per the protocol's forward-compatibility rule. */
                break;
        }
    });

    /* Keep the frame's own chrome following the host's light/dark choice. */
    new MutationObserver(() =>
        send({ type: "theme", theme: document.documentElement.getAttribute("data-theme") ?? "auto" })
    ).observe(document.documentElement, { attributes: true, attributeFilter: ["data-theme"] });
})();
