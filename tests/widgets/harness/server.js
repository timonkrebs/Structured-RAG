// Static server + a scriptable widget host, so the widgets can be driven exactly
// the way ChatGPT and MCP Apps hosts drive them.
//
// The host page embeds a widget in an iframe, answers ui/initialize, pushes the
// tool result, and — this is the part the tests care about — applies the sizes
// the widget reports the way a real host does: asynchronously, and optionally
// capped. Both are configurable per test via the query string:
//
//   w         widget file name       (default semester-planner.html)
//   delay     ms before a reported size is applied to the frame   (default 60)
//   maxh      cap the frame at this height; 0 = grow to fit       (default 0)
//   proposal  0 = strip the assistant's preselection from the payload (default: keep)
//
// window.__resizes / window.__lastReported record what the host received.
const http = require("http");
const fs = require("fs");
const path = require("path");

const WIDGETS = path.join(__dirname, "..", "..", "..", "StructuredRAG.Mcp", "Widgets");
// Widgets that take a tool result get one; the rest render their empty state.
const FIXTURES = {
  "semester-planner.html": "plan-semester.json",
  "start.html": "start.json",
};
const PORT = Number(process.env.WIDGET_TEST_PORT || 5601);

// The frame starts far shorter than any widget's content: every test therefore
// begins in the state a real host starts in, before the first size report lands.
const INITIAL_FRAME_HEIGHT = 320;

const HOST_PAGE = `<!doctype html><html><head><meta charset="utf-8"><title>widget host</title></head>
<body style="margin:0;font:14px sans-serif">
<div style="height:900px;background:#f2f2f2;padding:8px">transcript above</div>
<div id="card" style="max-width:760px;margin:0 auto;border:1px solid #ccc;overflow:hidden">
  <div id="slot"></div>
</div>
<div id="below" style="background:#f2f2f2;padding:8px">transcript below</div>
<script>
var q = new URLSearchParams(location.search);
var WIDGET = q.get("w") || "semester-planner.html";
var RESIZE_DELAY = q.get("delay") === null ? 60 : +q.get("delay");
var MAXH = +(q.get("maxh") || 0);
// How much transcript sits below the widget. A widget at the very end of the
// conversation is the normal case, and it changes what a frame resize does to
// the surrounding scroll position.
document.getElementById("below").style.height = (q.get("below") === null ? 2000 : +q.get("below")) + "px";
window.__resizes = 0;
window.__lastReported = 0;
window.__ready = false;

fetch("/fixture.json?w=" + WIDGET).then(function (r) { return r.json(); }).then(function (plan) {
  // plan_semester answers without a proposal too (the student asked for the
  // offering, not for a plan) — the planner opens differently then.
  if (plan && q.get("proposal") === "0") { plan.proposed = []; plan.proposedClasses = {}; }
  var f = document.createElement("iframe");
  f.id = "w";
  f.style.cssText = "width:100%;height:${INITIAL_FRAME_HEIGHT}px;border:0;display:block";
  f.src = "/widgets/" + WIDGET;
  document.getElementById("slot").appendChild(f);

  window.addEventListener("message", function (ev) {
    var m = ev.data;
    if (!m || m.jsonrpc !== "2.0") return;
    var reply = function (result) {
      f.contentWindow.postMessage({ jsonrpc: "2.0", id: m.id, result: result || {} }, "*");
    };
    if (m.method === "ui/initialize") {
      reply({ hostContext: { theme: "light", locale: "de-CH" } });
      if (plan) {
        setTimeout(function () {
          f.contentWindow.postMessage({
            jsonrpc: "2.0", method: "ui/notifications/tool-result",
            params: { structuredContent: plan }
          }, "*");
          window.__ready = true;
        }, 30);
      } else {
        window.__ready = true;
      }
      return;
    }
    if (m.method === "ui/notifications/size-changed") {
      var h = m.params.height;
      window.__lastReported = h;
      // Real hosts apply a reported size a turn of the message loop later, not
      // synchronously — the whole point of the scenarios below.
      setTimeout(function () {
        f.style.height = (MAXH ? Math.min(h, MAXH) : h) + "px";
        window.__resizes++;
      }, RESIZE_DELAY);
      return;
    }
    if (m.id !== undefined) reply({});
  });
});
</script>
</body></html>`;

const TYPES = { ".html": "text/html; charset=utf-8", ".json": "application/json" };

// The widgets are composed from shared parts: a line holding only
// {{include:name}} inside a comment is replaced by that file. The server does
// this in WidgetResources.LoadWidgetHtml — mirrored here so the tests drive the
// same HTML a host receives, not the un-composed source. Keep the two in sync;
// a marker that survives substitution is a broken widget, so it throws.
//
// The \r? is redundant here (JS treats \r as a line terminator, so $ matches
// before it) but not in .NET, where its absence silently breaks every CRLF
// checkout. Kept identical to the C# pattern precisely so this harness cannot
// compose a file the real server would choke on and leave the suite green.
const INCLUDE_MARKER = /^[ \t]*(?:\/\/|\/\*)[ \t]*\{\{include:([\w.\-]+)\}\}[ \t]*(?:\*\/)?[ \t]*\r?$/gm;

function composeWidget(file) {
  const html = fs.readFileSync(file, "utf8").replace(INCLUDE_MARKER, (_, name) => {
    const part = path.join(WIDGETS, name);
    if (!fs.existsSync(part)) throw new Error(`widget include not found: ${name}`);
    return fs.readFileSync(part, "utf8").replace(/[\r\n]+$/, "");
  });
  // Anchored, so the shared files may still talk about include markers in prose.
  INCLUDE_MARKER.lastIndex = 0;
  if (INCLUDE_MARKER.test(html)) throw new Error(`unresolved include in ${path.basename(file)}`);
  INCLUDE_MARKER.lastIndex = 0;
  return html;
}

const server = http.createServer(function (req, res) {
  const url = req.url.split("?")[0];
  if (url === "/" || url === "/host.html") {
    res.writeHead(200, { "content-type": TYPES[".html"] });
    return res.end(HOST_PAGE);
  }
  let file = null;
  if (url.startsWith("/widgets/")) file = path.join(WIDGETS, path.basename(url));
  else if (url === "/fixture.json") {
    const name = FIXTURES[new URLSearchParams(req.url.split("?")[1] || "").get("w")];
    if (!name) { res.writeHead(200, { "content-type": TYPES[".json"] }); return res.end("null"); }
    file = path.join(__dirname, "..", "fixtures", name);
  }
  if (!file || !fs.existsSync(file)) { res.writeHead(404); return res.end("not found"); }
  res.writeHead(200, { "content-type": TYPES[path.extname(file)] || "text/plain" });
  res.end(url.startsWith("/widgets/") ? composeWidget(file) : fs.readFileSync(file));
});

server.listen(PORT, function () {
  console.log("widget host on http://localhost:" + PORT);
});

module.exports = { PORT, INITIAL_FRAME_HEIGHT };
