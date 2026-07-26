// Clicking a Modulanlass in the timetable must land on that module's row in the
// list below (issue #35). What makes this fragile is that opening the category
// holding the row changes the widget's height, and the host applies heights
// asynchronously: scroll too early and the host's resize resets the widget's own
// scroller, dumping the student at the top of the widget.
//
// These scenarios drive the catalog view ({ view: "all" }) because that is where
// the accordion lives; plan-view.spec.js covers the same jump in the view the
// widget opens in.
const { test, expect } = require("@playwright/test");
const {
  VIEWPORT, openWidget, settle, timetableBlocks, deepestTimetableBlock, rowInView,
  toggleCategory, toggleFirstCategory, pushHostUpdate,
} = require("../harness/widget");

test.use({ viewport: VIEWPORT });

/** Scrolls the timetable into view and clicks one of its blocks. */
async function clickBlock(page, frame, code) {
  const block = await frame.$(`.tt-block[data-nav="${code}"]`);
  await block.scrollIntoViewIfNeeded();
  await page.waitForTimeout(250);
  await block.click();
}

test.describe("timetable → module row", () => {
  test("the accordion starts collapsed, so the row is hidden before the click", async ({ page }) => {
    const frame = await openWidget(page, { view: "all" });
    expect(await frame.$$eval("details.cat", (e) => e.length)).toBeGreaterThan(1);
    expect(await frame.$$eval("details.cat[open]", (e) => e.length)).toBe(0);
    expect((await timetableBlocks(frame)).length).toBeGreaterThan(1);
  });

  for (const resizeDelay of [60, 400]) {
    test(`lands on the row with a collapsed accordion (host resize latency ${resizeDelay}ms)`, async ({ page }) => {
      const frame = await openWidget(page, { resizeDelay, view: "all" });
      const code = await deepestTimetableBlock(frame);

      await clickBlock(page, frame, code);
      await page.waitForTimeout(1500 + resizeDelay);

      expect(await rowInView(frame, code)).toMatchObject({ visible: true });
      expect(await frame.$$eval("details.cat[open]", (e) => e.length)).toBeGreaterThan(0);
    });
  }

  test("lands on the row when the accordion is already open", async ({ page }) => {
    const frame = await openWidget(page, { view: "all" });
    const codes = await timetableBlocks(frame);

    await clickBlock(page, frame, await deepestTimetableBlock(frame));
    await page.waitForTimeout(1500);
    await clickBlock(page, frame, codes[0]); // categories are open by now
    await page.waitForTimeout(1500);

    expect(await rowInView(frame, codes[0])).toMatchObject({ visible: true });
  });

  test("survives a host re-render arriving mid-navigation", async ({ page }) => {
    const frame = await openWidget(page, { resizeDelay: 200, view: "all" });
    const code = await deepestTimetableBlock(frame);

    await clickBlock(page, frame, code);
    await page.waitForTimeout(80); // still inside the widget's wait window
    await pushHostUpdate(page);    // rebuilds the list, replacing the target row
    await page.waitForTimeout(1800);

    expect(await rowInView(frame, code)).toMatchObject({ visible: true });
    // The cue must be re-applied to the rebuilt row, not lost with the old one.
    expect(await frame.$$eval("li.nav-flash", (e) => e.length)).toBe(1);
  });

  test("lands on the row after the student expanded a category by hand", async ({ page }) => {
    const frame = await openWidget(page, { view: "all" });
    const code = await deepestTimetableBlock(frame);

    await toggleFirstCategory(frame); // native <details> toggle, not a render
    await page.waitForTimeout(500);
    await clickBlock(page, frame, code);
    await page.waitForTimeout(1800);

    expect(await rowInView(frame, code)).toMatchObject({ visible: true });
  });

  test("lands on the row when clicked while a resize is still in flight", async ({ page }) => {
    // The widget genuinely overflows its frame at click time here, which is what
    // a fixed-height host looks like from the inside — it must not be mistaken
    // for one, or it scrolls its own scroller and the host resets it.
    const frame = await openWidget(page, { resizeDelay: 400, view: "all" });
    const code = await deepestTimetableBlock(frame);

    await toggleFirstCategory(frame);
    await page.waitForTimeout(120); // host has not applied the new size yet
    expect(await frame.evaluate(() =>
      document.documentElement.scrollHeight > window.innerHeight + 2)).toBe(true);

    await clickBlock(page, frame, code);
    await page.waitForTimeout(2500);

    expect(await rowInView(frame, code)).toMatchObject({ visible: true });
  });

  test("waits for the newest size report, not the click that started it", async ({ page }) => {
    // A slow host plus a re-render late in the wait: the newer report is still
    // in flight when the original fallback would have expired.
    const frame = await openWidget(page, { resizeDelay: 2000, view: "all" });
    const code = await deepestTimetableBlock(frame);

    await clickBlock(page, frame, code);
    await page.waitForTimeout(1100);
    await pushHostUpdate(page); // new render → new size report → applied at ~3.1s
    await page.waitForTimeout(4000);

    expect(await rowInView(frame, code)).toMatchObject({ visible: true });
  });

  test("still waits for the host after the frame has shrunk", async ({ page }) => {
    // Expanding and re-collapsing leaves the frame smaller than it once was. If
    // the widget remembers the OLD height as what it asked the host for, it reads
    // the shrunken frame as a host that caps it, scrolls immediately, and the
    // resize that follows the next expansion resets that scroll.
    const frame = await openWidget(page, { view: "all" });
    await toggleFirstCategory(frame); // grow
    await page.waitForTimeout(400);
    await toggleFirstCategory(frame); // ...and back down
    await page.waitForTimeout(700);   // past the classifier's grace period

    const code = await deepestTimetableBlock(frame);
    await clickBlock(page, frame, code);
    await page.waitForTimeout(1800);

    expect(await rowInView(frame, code)).toMatchObject({ visible: true });
  });

  test("lands on the row when a frame shrink is still in flight", async ({ page }) => {
    // Collapsing sections asks the host for a SMALLER frame, so the click below
    // happens while a resize is pending and the document fits the old, larger
    // frame. That is safe — the scroll goes to the host page, which a frame
    // resize does not disturb — and this pins that down: the widget must not
    // start scrolling itself here, or the pending shrink would clamp it.
    const frame = await openWidget(page, { resizeDelay: 800, view: "all" });
    await toggleCategory(frame, 0);
    await toggleCategory(frame, 1);
    await settle(page, frame, 800);

    const code = await deepestTimetableBlock(frame);
    const block = await frame.$(`.tt-block[data-nav="${code}"]`);
    await block.scrollIntoViewIfNeeded();
    await page.waitForTimeout(200);

    // Collapse both, then click before the host has applied either shrink: the
    // click reopens one category, so the document is smaller than the frame it
    // is sitting in.
    await toggleCategory(frame, 0);
    await toggleCategory(frame, 1);
    expect(await frame.evaluate(() =>
      document.documentElement.scrollHeight <= window.innerHeight + 2)).toBe(true);
    await block.click();
    // The widget's own scroller must stay put; the host page does the scrolling.
    expect(await frame.evaluate(() => document.scrollingElement.scrollTop)).toBe(0);
    await page.waitForTimeout(3000);

    expect(await rowInView(frame, code)).toMatchObject({ visible: true });
  });

  test("a host slower than the capped-frame grace is not mistaken for capped", async ({ page }) => {
    // An auto-sizing host that takes longer than the grace period to honour a
    // report still overflows the frame when the student clicks. Reading that as
    // a fixed-height host scrolls immediately, and the late resize resets it.
    const frame = await openWidget(page, { resizeDelay: 900, view: "all" });
    const code = await deepestTimetableBlock(frame);
    // Positioned before the window opens: scrolling the timetable into view
    // afterwards would eat most of it and the click would land after the resize.
    const block = await frame.$(`.tt-block[data-nav="${code}"]`);
    await block.scrollIntoViewIfNeeded();

    await toggleCategory(frame, 0); // size report the host will honour at +900ms
    await page.waitForTimeout(600);  // past a 500ms grace, before the resize lands
    expect(await frame.evaluate(() =>
      document.documentElement.scrollHeight > window.innerHeight + 2)).toBe(true);

    await block.click();
    await page.waitForTimeout(3000);

    expect(await rowInView(frame, code)).toMatchObject({ visible: true });
  });

  test("scrolls itself, promptly, when the host caps the frame", async ({ page }) => {
    const frame = await openWidget(page, { maxFrameHeight: 500, view: "all" });
    const code = await deepestTimetableBlock(frame);

    // Timed from the click itself — scrolling the timetable into view first would
    // otherwise count against the budget on a loaded runner.
    const block = await frame.$(`.tt-block[data-nav="${code}"]`);
    await block.scrollIntoViewIfNeeded();
    await page.waitForTimeout(250);
    const startedAt = Date.now();
    await block.click();
    // A capped host never grows the frame, so there is nothing to wait for: the
    // scroll must not sit out the per-report wait (1.2s) before starting.
    await expect.poll(async () => frame.evaluate(() => document.scrollingElement.scrollTop),
      { timeout: 4000, intervals: [25] }).toBeGreaterThan(0);
    expect(Date.now() - startedAt).toBeLessThan(1000);

    await page.waitForTimeout(1200);
    expect(await rowInView(frame, code)).toMatchObject({ visible: true });
  });

  test("the flash cue expires instead of firing on every later render", async ({ page }) => {
    const frame = await openWidget(page, { view: "all" });
    const codes = await timetableBlocks(frame);

    const before = await page.evaluate(() => window.scrollY);
    await clickBlock(page, frame, await deepestTimetableBlock(frame));
    expect(await frame.$$eval("li.nav-flash", (e) => e.length)).toBe(1);

    // The cue's lifetime starts when the scroll lands, so wait for that rather
    // than counting from the click — the class itself lingers on the row until
    // the next render, which is precisely what this test is about.
    await expect.poll(async () => page.evaluate(() => window.scrollY),
      { timeout: 8000, intervals: [50] }).not.toBe(before);
    await page.waitForTimeout(2400); // past the cue's 2s window

    await pushHostUpdate(page, "de-CH"); // rebuilds the list
    await page.waitForTimeout(400);
    expect(await frame.$$eval("li.nav-flash", (e) => e.length)).toBe(0);
  });
});
