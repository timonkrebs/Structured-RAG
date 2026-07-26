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
});
