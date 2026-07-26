  // ---------- host bridge ----------
  // Inlined into every widget's IIFE by the include marker near the top of each
  // file (see Resources/WidgetResources.cs) — one implementation, four widgets.
  //
  // Abstracts the two widget host conventions over one interface:
  //  - OpenAI Apps SDK (ChatGPT): window.openai globals + methods
  //  - MCP Apps extension (Claude, ...): JSON-RPC 2.0 over postMessage (spec 2026-01-26)
  //
  // Widgets that include this file must define a render() function and call
  // boot() once their own state is initialized.
  var Host = (function () {
    // Height the host should give the widget. Measured on the content root, not
    // documentElement: documentElement.scrollHeight is floored at the viewport,
    // so a widget measuring that way could never ask for a SMALLER frame and
    // collapsing a section would leave a blank card in the chat — while the
    // root's bounding box would miss absolutely positioned overflow such as the
    // autocomplete list. scrollHeight covers both.
    function contentHeight() {
      var root = document.getElementById("root");
      return root ? root.scrollHeight : document.documentElement.scrollHeight;
    }

    var h = {
      mode: "none",       // "openai" | "mcp" | "none"
      ready: false,       // becomes true once host data has arrived (mcp is async)
      theme: null,
      locale: null,
      toolOutput: null,
      widgetState: null,  // openai only; MCP Apps has no widget-state store
      canCall: false,
      onchange: null,
      setState: function () {},
      callTool: function () { return Promise.reject(new Error("no host")); },
      sendMessage: function () {},
      openLink: function (url) { window.open(url, "_blank", "noopener"); },
      // Always returns the height the widget wants, in every host mode, so
      // callers can track what was asked for. Hosts that size the frame
      // themselves (ChatGPT) get no message — only the measurement.
      reportSize: contentHeight
    };
    function notify() { if (h.onchange) h.onchange(); }

    if (window.openai) {
      var api = window.openai;
      h.mode = "openai";
      h.ready = true;
      h.theme = api.theme || null;
      h.locale = api.locale || null;
      h.toolOutput = api.toolOutput || null;
      h.widgetState = api.widgetState || null;
      h.canCall = typeof api.callTool === "function";
      h.setState = function (state, summary) {
        // Widget state persists between renders but is NOT reliably model-visible in
        // ChatGPT. Mirror the model-facing summary into the state snapshot and, where
        // the host supports ui/update-model-context, push it there too — otherwise a
        // profile recorded here never reaches the model unless a flow button is used.
        var snapshot = summary ? Object.assign({}, state, { modelSummary: summary }) : state;
        if (typeof api.setWidgetState === "function") api.setWidgetState(snapshot);
        // Always push — an empty summary must CLEAR previously pushed context
        // (a cleared draft would otherwise stay model-visible), like the MCP branch.
        if (typeof api.updateModelContext === "function") {
          try { api.updateModelContext({ content: summary ? [{ type: "text", text: summary }] : [] }); } catch (e) {}
        }
      };
      h.callTool = function (name, args) { return api.callTool(name, args); };
      h.sendMessage = function (text) { if (typeof api.sendFollowUpMessage === "function") api.sendFollowUpMessage({ prompt: text }); };
      h.openLink = function (url) {
        if (typeof api.openExternal === "function") api.openExternal({ href: url });
        else window.open(url, "_blank", "noopener");
      };
      // ChatGPT fires set_globals for many reasons (scroll, keyboard, layout
      // changes — very frequently on mobile). Re-rendering on every event snaps
      // open dropdowns shut mid-pick, so only notify when something the widget
      // actually renders from has changed.
      var sigOf = function () {
        var ws; try { ws = JSON.stringify(h.widgetState || null); } catch (e) { ws = ""; }
        return [h.theme, h.locale, ws].join("|");
      };
      var lastSig = sigOf();
      window.addEventListener("openai:set_globals", function () {
        var toolChanged = !!api.toolOutput && api.toolOutput !== h.toolOutput;
        h.theme = api.theme || h.theme;
        h.locale = api.locale || h.locale;
        h.toolOutput = api.toolOutput || h.toolOutput;
        h.widgetState = api.widgetState || h.widgetState;
        var sig = sigOf();
        if (!toolChanged && sig === lastSig) return;
        lastSig = sig;
        notify();
      });
    } else if (window.parent && window.parent !== window) {
      h.mode = "mcp";
      h.canCall = true;
      var nextId = 1, pending = {};
      var post = function (msg) { window.parent.postMessage(msg, "*"); };
      var request = function (method, params) {
        return new Promise(function (resolve, reject) {
          var id = nextId++;
          pending[id] = { resolve: resolve, reject: reject };
          post({ jsonrpc: "2.0", id: id, method: method, params: params });
        });
      };
      window.addEventListener("message", function (ev) {
        var m = ev.data;
        if (!m || m.jsonrpc !== "2.0") return;
        if (m.id !== undefined && m.method === undefined) {
          var p = pending[m.id];
          if (!p) return;
          delete pending[m.id];
          if (m.error) p.reject(new Error(m.error.message || "host error")); else p.resolve(m.result);
          return;
        }
        if (m.method === "ui/notifications/tool-result") {
          var pr = m.params || {};
          if (pr.structuredContent) { h.toolOutput = pr.structuredContent; h.ready = true; notify(); }
        } else if (m.method === "ui/notifications/host-context-changed") {
          var c = m.params || {};
          if (c.theme) h.theme = c.theme;
          if (c.locale) h.locale = c.locale;
          notify();
        }
      });
      h.setState = function (state, summary) {
        // Nearest MCP Apps equivalent of widget state: persist the draft into the
        // model context (each request overwrites the previous one), so the model
        // sees the student's current selection in future turns.
        request("ui/update-model-context", {
          structuredContent: state,
          content: summary ? [{ type: "text", text: summary }] : []
        }).catch(function () {});
      };
      h.callTool = function (name, args) { return request("tools/call", { name: name, arguments: args }); };
      h.sendMessage = function (text) {
        // MCP Apps expects content as a ContentBlock ARRAY — validating hosts
        // reject a bare object, and the send-to-chat buttons silently do nothing.
        request("ui/message", { role: "user", content: [{ type: "text", text: text }] }).catch(function () {});
      };
      h.openLink = function (url) { request("ui/open-link", { url: url }).catch(function () {}); };
      h.reportSize = function () {
        var height = contentHeight();
        post({ jsonrpc: "2.0", method: "ui/notifications/size-changed",
               params: { width: document.documentElement.scrollWidth, height: height } });
        return height;
      };
      request("ui/initialize", {
        protocolVersion: "2026-01-26",
        clientInfo: { name: "module-catalog-widget", version: "1.0.0" },
        capabilities: {},
        appCapabilities: {}
      }).then(function (res) {
        var ctx = (res && res.hostContext) || {};
        if (ctx.theme) h.theme = ctx.theme;
        if (ctx.locale) h.locale = ctx.locale;
        notify();
      }).catch(function () { notify(); });
    }
    return h;
  })();

  // ---------- locale ----------
  function isDe() {
    var loc = Host.locale || "de";
    return String(loc).toLowerCase().indexOf("de") === 0;
  }
  var DAYS = {
    de: { Monday: "Mo", Tuesday: "Di", Wednesday: "Mi", Thursday: "Do", Friday: "Fr", Saturday: "Sa", Sunday: "So" },
    en: { Monday: "Mon", Tuesday: "Tue", Wednesday: "Wed", Thursday: "Thu", Friday: "Fri", Saturday: "Sat", Sunday: "Sun" }
  };
  function day(d) { return (isDe() ? DAYS.de : DAYS.en)[d] || d; }
  function locTitle(m) { return (!isDe() && m.titleEn) ? m.titleEn : m.title; }
  function locSummary(m) { return (!isDe() && m.summaryEn) ? m.summaryEn : m.summary; }

  // ---------- shared helpers ----------
  function esc(s) {
    return String(s == null ? "" : s).replace(/[&<>"']/g, function (c) {
      return { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c];
    });
  }
  // Minimal markdown for the fetch text (headings, bold, bullets). The input is
  // escaped up front, so the transforms below only ever emit our own tags.
  function mdHtml(md) {
    var out = [], list = null, m;
    esc(md).split(/\r?\n/).forEach(function (line) {
      line = line.replace(/\*\*([^*]+)\*\*/g, "<strong>$1</strong>");
      if ((m = /^\s*[-*•]\s+(.*)$/.exec(line))) {
        (list = list || []).push("<li>" + m[1] + "</li>");
        return;
      }
      if (list) { out.push("<ul>" + list.join("") + "</ul>"); list = null; }
      if ((m = /^\s*#{1,4}\s+(.*)$/.exec(line))) out.push('<div class="md-h">' + m[1] + "</div>");
      else if (/\S/.test(line)) out.push("<p>" + line + "</p>");
    });
    if (list) out.push("<ul>" + list.join("") + "</ul>");
    return out.join("");
  }
  // callTool result → structured payload: prefer structuredContent, fall back to
  // parsing the JSON text block hosts emit when structured output is unavailable.
  // Unparseable text is handed back as {text} rather than dropped — callers that
  // want a specific shape check for it (Array.isArray(sc.steps) etc.), and the
  // detail panels can still show what the tool returned.
  function extractStructured(res) {
    if (!res) return null;
    var sc = res.structuredContent || (res.result && res.result.structuredContent);
    if (sc) return sc;
    var content = res.content || (res.result && res.result.content);
    if (Array.isArray(content)) {
      var text = content.map(function (c) { return c && c.text ? c.text : ""; }).join("\n");
      if (text) {
        try { return JSON.parse(text); } catch (e) { return { text: text }; }
      }
    }
    return null;
  }

  // ---------- theme + render scheduling ----------
  function applyTheme() {
    var theme = Host.theme ||
      (window.matchMedia && window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light");
    document.documentElement.dataset.theme = theme;
  }

  // Host-triggered re-renders must never destroy a form control the student is
  // using: replacing an open <select> closes it before a choice can be made.
  // Defer the render until the control is committed or left; our own event
  // handlers keep calling render() directly (the dropdown is closed by then).
  var hostRenderDeferred = false;
  function hostRender() {
    var root = document.getElementById("root");
    var active = document.activeElement;
    if (active && root.contains(active) && (active.tagName === "SELECT" || active.tagName === "INPUT")) {
      if (hostRenderDeferred) return;
      hostRenderDeferred = true;
      var done = function (ev) {
        active.removeEventListener("blur", done);
        active.removeEventListener("change", done);
        hostRenderDeferred = false;
        // Focus moving to another element inside the widget (e.g. clicking an
        // autocomplete suggestion or a button): rendering right now would replace
        // the click target mid-gesture — let the click land first, then refresh.
        if (ev && ev.type === "blur" && ev.relatedTarget && root.contains(ev.relatedTarget)) {
          setTimeout(hostRender, 150);
          return;
        }
        render();
      };
      active.addEventListener("blur", done);
      active.addEventListener("change", done);
      return;
    }
    render();
  }

  // Called by each widget once its own state is initialized — render() reads
  // that state, so it must not run while the widget's vars are still undefined.
  function boot() {
    Host.onchange = function () { applyTheme(); hostRender(); };
    applyTheme();
    render();
  }
