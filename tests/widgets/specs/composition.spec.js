// The widgets are assembled from shared parts at serve time (see
// WidgetResources.LoadWidgetHtml, mirrored in harness/server.js). These checks
// cover the assembly itself, which the browser specs only exercise indirectly:
// a widget whose include never resolved still renders until the first call into
// the missing code, and a stray marker would ship to the host as literal text.
const { test, expect } = require("@playwright/test");
const fs = require("fs");
const path = require("path");
const vm = require("vm");

const WIDGETS = path.join(__dirname, "..", "..", "..", "StructuredRAG.Mcp", "Widgets");
const PAGES = ["start.html", "semester-planner.html", "path-planner.html", "module-comparer.html"];
const SHARED = ["_tokens.css", "_host.js"];
const MARKER = /^[ \t]*(?:\/\/|\/\*)[ \t]*\{\{include:([\w.\-]+)\}\}[ \t]*(?:\*\/)?[ \t]*$/gm;

const compose = (name) =>
  fs.readFileSync(path.join(WIDGETS, name), "utf8").replace(
    new RegExp(MARKER.source, "gm"),
    (_, part) => fs.readFileSync(path.join(WIDGETS, part), "utf8").replace(/[\r\n]+$/, ""));

test.describe("widget composition", () => {
  for (const name of PAGES) {
    test(`${name} pulls in every shared part exactly once`, () => {
      const raw = fs.readFileSync(path.join(WIDGETS, name), "utf8");
      for (const part of SHARED) {
        expect(raw.match(new RegExp(`\\{\\{include:${part.replace(".", "\\.")}\\}\\}`, "g")))
          .toHaveLength(1);
      }
    });

    test(`${name} composes with no marker left behind`, () => {
      expect(compose(name)).not.toMatch(new RegExp(MARKER.source, "m"));
    });

    test(`${name}'s composed script parses`, () => {
      const body = compose(name);
      const scripts = [...body.matchAll(/<script>([\s\S]*?)<\/script>/g)].map((m) => m[1]);
      expect(scripts.length).toBeGreaterThan(0);
      // Compile only — never run: the widget expects a host and a DOM.
      for (const src of scripts) {
        expect(() => new vm.Script(src, { filename: name })).not.toThrow();
      }
    });

    test(`${name} defines the shared helpers it calls`, () => {
      const src = compose(name).match(/<script>([\s\S]*?)<\/script>/)[1];
      // Nothing may fall back to a per-widget copy: these come from _host.js now.
      for (const symbol of ["var Host =", "function esc(", "function isDe(", "function boot("]) {
        expect(src.split(symbol).length - 1, `${symbol} in ${name}`).toBe(1);
      }
      expect(src).toContain("boot();");
    });
  }

  test("the shared parts carry no include markers of their own", () => {
    // Substitution is a single pass, so a nested marker would ship verbatim.
    for (const part of SHARED) {
      expect(fs.readFileSync(path.join(WIDGETS, part), "utf8"))
        .not.toMatch(new RegExp(MARKER.source, "m"));
    }
  });

  // The marker is anchored to end-of-line, and .NET's multiline $ sits before the
  // \n of a CRLF pair — so a CRLF working tree used to make LoadWidgetHtml match
  // nothing and, because the guard shares the pattern, fail silently. The C# regex
  // now tolerates \r, but this suite cannot prove that: JS treats \r as a line
  // terminator, so the harness composes CRLF happily and would stay green while the
  // server shipped raw markers. .gitattributes pins these files to LF; this asserts
  // it actually held, which is the part CI can check.
  test("widget sources are LF, as .gitattributes requires", () => {
    for (const name of [...PAGES, ...SHARED]) {
      const raw = fs.readFileSync(path.join(WIDGETS, name), "latin1");
      expect(raw.indexOf("\r"), `CR byte in ${name}`).toBe(-1);
    }
  });
});
