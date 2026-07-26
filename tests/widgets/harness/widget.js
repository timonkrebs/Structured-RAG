// Shared helpers for driving a widget inside the scripted host.
const { expect } = require("@playwright/test");

const PORT = Number(process.env.WIDGET_TEST_PORT || 5601);
const VIEWPORT = { width: 1000, height: 800 };

/**
 * Opens the host page and returns the widget's frame once it has rendered.
 *
 * The semester planner opens on the student's plan when the payload carries a
 * proposal (issue #34), so specs about the full module list — the accordion,
 * everything the catalog view holds — pass `{ view: "all" }` and get there the
 * way a student does: by clicking the switch.
 */
async function openWidget(page, opts = {}) {
  const q = new URLSearchParams({
    w: opts.widget || "semester-planner.html",
    delay: String(opts.resizeDelay === undefined ? 60 : opts.resizeDelay),
    maxh: String(opts.maxFrameHeight || 0),
    proposal: opts.proposal === false ? "0" : "1",
  });
  await page.goto(`http://localhost:${PORT}/?${q}`);
  const frame = await (await page.waitForSelector("#w")).contentFrame();
  await frame.waitForSelector(opts.waitFor || "#root > *", { timeout: 20000 });
  // Let the opening size report land, so tests start from a settled frame.
  await settle(page, frame, opts.resizeDelay);
  if (opts.view) {
    await setView(frame, opts.view);
    await settle(page, frame, opts.resizeDelay);
  }
  return frame;
}

/** Clicks the plan ⇄ catalog switch of the semester planner ("plan" | "all"). */
async function setView(frame, view) {
  // Re-queried each call: a render replaces #root wholesale, detaching handles.
  const button = await frame.$(`.view-switch button[data-view="${view}"]`);
  if (!button) throw new Error(`no view switch button for "${view}"`);
  await button.evaluate((el) => el.click());
}

/** Which view the switch reports as active, or null when there is no switch. */
async function activeView(frame) {
  return frame.$eval(".view-switch button[aria-pressed='true']",
    (el) => el.getAttribute("data-view")).catch(() => null);
}

/** The module codes of the rows the list currently renders, in order. */
async function listedRows(frame) {
  return frame.$$eval("li[data-mod]", (els) => els.map((e) => e.getAttribute("data-mod")));
}

/**
 * Waits until the host has applied the widget's size reports and the frame has
 * stopped moving. Frame height is not compared against the reported height —
 * a capped host deliberately applies less than the widget asked for.
 */
async function settle(page, frame, resizeDelay = 60) {
  let last = null, stable = 0;
  await expect.poll(async () => {
    const s = await page.evaluate(() => ({
      reported: window.__lastReported,
      resizes: window.__resizes,
      frameH: Math.round(document.getElementById("w").getBoundingClientRect().height),
    }));
    const key = `${s.resizes}:${s.frameH}`;
    if (key === last) stable++; else { stable = 0; last = key; }
    return s.reported > 0 && s.resizes > 0 && stable >= 2;
  }, { timeout: Math.max(6000, resizeDelay * 4), intervals: [50] }).toBe(true);
}

/** Geometry of the frame, the reported size and the widget's content root. */
async function geometry(page, frame) {
  return {
    frameHeight: Math.round(await page.evaluate(() =>
      document.getElementById("w").getBoundingClientRect().height)),
    reported: await page.evaluate(() => window.__lastReported),
    resizes: await page.evaluate(() => window.__resizes),
    contentHeight: await frame.evaluate(() =>
      Math.ceil(document.getElementById("root").getBoundingClientRect().height)),
    documentScrollHeight: await frame.evaluate(() => document.documentElement.scrollHeight),
    innerHeight: await frame.evaluate(() => window.innerHeight),
    widgetScrollTop: await frame.evaluate(() => document.scrollingElement.scrollTop),
  };
}

/** The module codes of the timetable blocks, in render order. */
async function timetableBlocks(frame) {
  return frame.$$eval(".tt-block", (els) => els.map((e) => e.getAttribute("data-nav")));
}

/**
 * The timetable block whose module row sits deepest in the list — the longest
 * jump, and the one a mistimed scroll fails most visibly. Rows inside collapsed
 * categories are still in the DOM, so their order is known before any click.
 */
async function deepestTimetableBlock(frame) {
  return frame.evaluate(() => {
    const order = [...document.querySelectorAll("li[data-mod]")]
      .map((li) => li.getAttribute("data-mod"));
    const blocks = [...document.querySelectorAll(".tt-block")]
      .map((b) => b.getAttribute("data-nav"));
    return blocks.sort((a, b) => order.indexOf(a) - order.indexOf(b)).pop();
  });
}

/** True when the module's row sits fully inside the host viewport. */
async function rowInView(frame, code) {
  const row = await frame.$(`li[data-mod="${code}"]`);
  if (!row) return { visible: false, y: null };
  const box = await row.boundingBox();
  if (!box) return { visible: false, y: null };
  // Rows are tall; requiring the whole row on screen would fail for legitimate
  // landings, so check the top plus a readable slice of it.
  const slice = Math.min(box.height, 200);
  return { visible: box.y >= 0 && box.y + slice <= VIEWPORT.height, y: Math.round(box.y) };
}

/**
 * Clicks a category accordion's summary — the native <details> toggle path,
 * which changes the widget's height without going through render().
 */
async function toggleCategory(frame, index = 0) {
  // Re-queried each call: a render replaces #root wholesale, detaching handles.
  const summaries = await frame.$$("details.cat > summary");
  await summaries[index].evaluate((el) => el.click());
}
const toggleFirstCategory = (frame) => toggleCategory(frame, 0);

/**
 * Pushes a tool result into the widget the way a host delivers one, so a spec
 * can drive a payload shape the committed fixture does not contain (rather than
 * teaching the harness server about every edge case).
 */
async function pushToolResult(page, payload) {
  await page.evaluate((data) => {
    document.getElementById("w").contentWindow.postMessage({
      jsonrpc: "2.0", method: "ui/notifications/tool-result",
      params: { structuredContent: data },
    }, "*");
  }, payload);
}

/** Pushes a host-context change, which makes the widget re-render mid-flight. */
async function pushHostUpdate(page, locale = "en-US") {
  await page.evaluate((loc) => {
    document.getElementById("w").contentWindow.postMessage({
      jsonrpc: "2.0", method: "ui/notifications/host-context-changed",
      params: { locale: loc }
    }, "*");
  }, locale);
}

module.exports = {
  PORT, VIEWPORT, openWidget, settle, geometry,
  timetableBlocks, deepestTimetableBlock, rowInView,
  toggleCategory, toggleFirstCategory, pushHostUpdate, pushToolResult,
  setView, activeView, listedRows,
};
