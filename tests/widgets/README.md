# Widget tests

Browser tests for `StructuredRAG.Mcp/Widgets/*.html`, run by
`.github/workflows/widget-tests.yml` on every pull request that touches the
widgets or these tests.

The widgets are plain HTML files that talk to a host (ChatGPT's Apps SDK or the
MCP Apps postMessage bridge) through a small JSON-RPC layer. Most of what can go
wrong lives in that conversation rather than in the markup: the host applies
sizes *asynchronously*, re-renders the widget whenever its own context changes,
and may cap the frame and scroll the widget internally instead of growing it.
So the tests do not assert on markup — they drive a scripted host and check what
the student ends up seeing.

```bash
npm ci
npx playwright install --with-deps chromium
npm test                 # npm run test:headed to watch it
npm run report           # last HTML report
```

Running in a dev container that already ships a Chromium of a different build
(`PLAYWRIGHT_BROWSERS_PATH=/opt/pw-browsers`), point at it instead of
downloading:

```bash
CHROMIUM_EXECUTABLE=/opt/pw-browsers/chromium-*/chrome-linux/chrome npm test
```

## Layout

| Path | What it is |
| --- | --- |
| `harness/server.js` | Static server plus the scriptable host page. Query string controls which widget is loaded, how long the host takes to apply a reported size (`delay`), and whether it caps the frame (`maxh`). |
| `harness/widget.js` | Helpers the specs share: open a widget, wait for the frame to settle, read geometry, click accordions, push host updates. |
| `specs/timetable-navigation.spec.js` | Clicking a Modulanlass in the timetable lands on that module's row (issue #35). |
| `specs/frame-sizing.spec.js` | The height a widget reports tracks its content in both directions, including absolutely positioned overflow. |
| `fixtures/*.json` | Committed tool payloads. |

## Fixtures

`fixtures/plan-semester.json` and `fixtures/start.json` are generated from the
compiled catalog and committed, so the tests stay deterministic while
`compiled/` keeps moving. Regenerate after a change to the tool payload shape:

```bash
npm run build-fixture          # reads ../../compiled/modules.json
```

The scenarios need several module types in the payload (the planner only draws
the category accordion when there is more than one), enough rows that opening a
category outgrows the frame, and modules with timed lessons so the timetable has
blocks to click. `tools/build-fixture.js` asserts the first two.

## The host is the point

Both bugs these specs were written for came from assuming the host reacts
synchronously. When adding a test, prefer driving the real gesture (click the
accordion summary, click a timetable block) over calling widget internals, and
vary `delay`/`maxh` rather than trusting one host shape — a fast auto-sizing
host hides most of what these tests catch.
