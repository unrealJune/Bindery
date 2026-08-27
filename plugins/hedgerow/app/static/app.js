/* Hedgerow's frame-side app. Sandboxed, opaque origin, no network egress: every call to the
   backend goes through the postMessage bridge (docs/PLUGIN-UI.md §4), never fetch/XHR, which
   the frame's `connect-src 'none'` CSP would block anyway.

   The whole point of the plugin is here — dragging chapters into the right order and pinning
   explicit numbers — implemented with the native HTML5 drag-and-drop API and no framework. */

(function () {
  "use strict";

  // ---- the bridge ---------------------------------------------------------

  var pending = new Map();
  var seq = 0;
  var BASE = ""; // '/plugins/hedgerow/ui' — every request path must start under here.

  function request(method, subpath, body) {
    var id = String(++seq);
    var msg = { bindery: 1, type: "request", id: id, method: method, path: BASE + subpath };
    if (body !== undefined) {
      msg.body = typeof body === "string" ? body : JSON.stringify(body);
      msg.contentType = "application/json";
    }
    parent.postMessage(msg, "*");
    return new Promise(function (resolve, reject) {
      pending.set(id, { resolve: resolve, reject: reject });
    });
  }

  function notify(level, message) {
    parent.postMessage({ bindery: 1, type: "notify", level: level, message: message }, "*");
  }

  function json(response) {
    try { return JSON.parse(response.body || "null"); } catch (e) { return null; }
  }

  window.addEventListener("message", function (e) {
    var msg = e.data;
    if (!msg || msg.bindery !== 1) return;
    if (msg.type === "init") { BASE = msg.base || BASE; applyTheme(msg.theme); route(); return; }
    if (msg.type === "theme") { applyTheme(msg.theme); return; }
    var entry = msg.id && pending.get(msg.id);
    if (!entry) return;
    pending.delete(msg.id);
    if (msg.type === "response") entry.resolve(msg); else entry.reject(new Error(msg.message || "bridge error"));
  });

  // Report our height so the host can size the iframe to the content.
  new ResizeObserver(function () {
    parent.postMessage(
      { bindery: 1, type: "resize", height: document.documentElement.scrollHeight }, "*");
  }).observe(document.documentElement);

  parent.postMessage({ bindery: 1, type: "ready" }, "*");

  function applyTheme(theme) {
    document.body.classList.toggle("hg-dark", theme === "dark");
  }

  // ---- tiny DOM helpers (textContent everywhere: no HTML injection) --------

  function el(tag, cls, text) {
    var node = document.createElement(tag);
    if (cls) node.className = cls;
    if (text != null) node.textContent = text;
    return node;
  }

  function clear(node) { while (node.firstChild) node.removeChild(node.firstChild); }

  var app = document.getElementById("app");
  var selectedWork = null;

  // ---- routing ------------------------------------------------------------

  function route() {
    if (location.pathname.replace(/\/+$/, "").endsWith("/sources")) renderSources();
    else renderChapters();
  }

  // ---- chapters view ------------------------------------------------------

  function renderChapters() {
    request("GET", "/api/state").then(function (r) {
      var state = json(r) || {};
      var works = state.works || [];
      clear(app);
      if (!works.length) {
        app.appendChild(emptyCard("Nothing ingested yet. Paste a Royal Road URL into Bindery, "
          + "or post one in a watched Discord channel, to begin."));
        return;
      }
      if (selectedWork == null || !works.some(function (w) { return w.id === selectedWork; })) {
        selectedWork = works[0].id;
      }
      app.appendChild(chapterHeader(works));
      var listHost = el("div");
      listHost.id = "hg-chapters";
      app.appendChild(listHost);
      loadChapters();
    });
  }

  function chapterHeader(works) {
    var card = el("div", "hg-card");
    var row = el("div", "hg-row hg-spread");
    row.appendChild(el("h1", null, "Chapters"));
    var right = el("div", "hg-row");
    if (works.length > 1) {
      var select = el("select", "hg-select");
      works.forEach(function (w) {
        var opt = el("option", null, w.title || ("Work " + w.id));
        opt.value = String(w.id);
        if (w.id === selectedWork) opt.selected = true;
        select.appendChild(opt);
      });
      select.addEventListener("change", function () {
        selectedWork = Number(select.value);
        loadChapters();
      });
      right.appendChild(select);
    }
    var reset = el("button", "hg-btn", "Clear overrides");
    reset.addEventListener("click", function () {
      request("POST", "/api/reset", { work: selectedWork }).then(function () {
        notify("info", "Manual ordering cleared.");
        loadChapters();
      });
    });
    right.appendChild(reset);
    row.appendChild(right);
    card.appendChild(row);
    card.appendChild(el("p", "hg-muted",
      "Drag to reorder. Set a number to pin a chapter explicitly; both override the "
      + "automatic arc.part ordering."));
    return card;
  }

  function loadChapters() {
    request("GET", "/api/chapters?work=" + encodeURIComponent(selectedWork)).then(function (r) {
      var data = json(r) || {};
      var host = document.getElementById("hg-chapters");
      if (!host) return;
      clear(host);
      var chapters = data.chapters || [];
      if (!chapters.length) {
        host.appendChild(emptyCard("This work has no chapters yet."));
        return;
      }
      var list = el("ul", "hg-list");
      chapters.forEach(function (ch) { list.appendChild(chapterRow(ch)); });
      wireDragAndDrop(list);
      host.appendChild(list);
    });
  }

  function chapterRow(ch) {
    var li = el("li", "hg-chapter");
    li.setAttribute("draggable", "true");
    li.dataset.id = String(ch.id);

    li.appendChild(el("span", "hg-handle", "\u2630")); // ☰ drag handle

    var mid = el("div");
    mid.appendChild(el("div", "hg-title", ch.title || "Untitled"));
    var badge = el("span", "hg-badge " + (ch.source === "discord" ? "discord" : "rr"),
      ch.source === "discord" ? "Discord EPUB" : "Royal Road");
    mid.appendChild(badge);
    li.appendChild(mid);

    li.appendChild(el("span", "hg-num", ch.number != null ? ch.number : "\u2014"));

    var pin = el("input", "hg-pin");
    pin.type = "number";
    pin.step = "0.1";
    pin.placeholder = "pin #";
    if (ch.pinned != null) pin.value = String(ch.pinned);
    pin.addEventListener("change", function () {
      var value = pin.value.trim();
      request("POST", "/api/pin",
        { work: selectedWork, chapter: ch.id, number: value === "" ? null : Number(value) })
        .then(function () { notify("info", "Pin updated."); loadChapters(); });
    });
    // The handle drags, not the number field.
    pin.addEventListener("mousedown", function (e) { e.stopPropagation(); });
    li.appendChild(pin);
    return li;
  }

  // ---- drag and drop ------------------------------------------------------

  function wireDragAndDrop(list) {
    var dragging = null;

    list.addEventListener("dragstart", function (e) {
      dragging = e.target.closest(".hg-chapter");
      if (dragging) dragging.classList.add("dragging");
    });

    list.addEventListener("dragend", function () {
      if (dragging) dragging.classList.remove("dragging");
      Array.prototype.forEach.call(list.children, function (c) { c.classList.remove("drop-target"); });
      dragging = null;
      commitOrder(list);
    });

    list.addEventListener("dragover", function (e) {
      e.preventDefault();
      var over = e.target.closest(".hg-chapter");
      if (!over || over === dragging) return;
      var rect = over.getBoundingClientRect();
      var after = (e.clientY - rect.top) > rect.height / 2;
      list.insertBefore(dragging, after ? over.nextSibling : over);
    });
  }

  function commitOrder(list) {
    var order = Array.prototype.map.call(list.children, function (c) { return Number(c.dataset.id); });
    request("POST", "/api/order", { work: selectedWork, order: order }).then(function () {
      notify("info", "Order saved.");
    });
  }

  // ---- sources view -------------------------------------------------------

  function renderSources() {
    request("GET", "/api/state").then(function (r) {
      var state = json(r) || {};
      clear(app);
      app.appendChild(el("h1", null, "Sources"));

      var bot = state.bot || {};
      var botCard = el("div", "hg-card");
      var row = el("div", "hg-row hg-spread");
      row.appendChild(el("h2", null, "Discord bot"));
      var badge = el("span", "hg-badge state-" + (bot.status || "disabled"), bot.status || "disabled");
      row.appendChild(badge);
      botCard.appendChild(row);
      botCard.appendChild(el("p", "hg-muted", bot.detail || ""));
      if (bot.channels && bot.channels.length) {
        botCard.appendChild(el("p", "hg-muted", "Watching channels: " + bot.channels.join(", ")));
      }
      app.appendChild(botCard);

      var works = state.works || [];
      var bindings = state.bindings || [];
      var wcard = el("div", "hg-card");
      wcard.appendChild(el("h2", null, "Works"));
      if (!works.length) {
        wcard.appendChild(el("p", "hg-muted", "No works yet."));
      } else {
        works.forEach(function (w) {
          var dl = el("dl", "hg-defn");
          addDef(dl, "Title", w.title || "Untitled");
          addDef(dl, "Chapters", String(w.chapters));
          if (w.source_url) addDef(dl, "Royal Road", w.source_url);
          wcard.appendChild(dl);
        });
      }
      app.appendChild(wcard);

      if (bindings.length) {
        var bcard = el("div", "hg-card");
        bcard.appendChild(el("h2", null, "Channel bindings"));
        bindings.forEach(function (b) {
          bcard.appendChild(el("p", "hg-muted", "Channel " + b.channel_id + " \u2192 " + (b.title || ("work " + b.work_id))));
        });
        app.appendChild(bcard);
      }
    });
  }

  function addDef(dl, term, value) {
    dl.appendChild(el("dt", null, term));
    dl.appendChild(el("dd", null, value));
  }

  function emptyCard(text) {
    var card = el("div", "hg-card hg-empty hg-muted");
    card.textContent = text;
    return card;
  }
})();
