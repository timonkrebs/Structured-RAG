// Widgets tell the host how tall they are; the host sizes the frame from that.
// The reported height must track the content in BOTH directions — a report that
// can only ever grow leaves an oversized, mostly blank card in the chat.
const { test, expect } = require("@playwright/test");
const { VIEWPORT, openWidget, settle, geometry, toggleFirstCategory } = require("../harness/widget");

test.use({ viewport: VIEWPORT });

const WIDGETS = ["start.html", "path-planner.html", "module-comparer.html", "semester-planner.html"];

test.describe("frame sizing", () => {
  for (const widget of WIDGETS) {
    test(`${widget} reports its content height, not the frame it sits in`, async ({ page }) => {
      const errors = [];
      page.on("pageerror", (e) => errors.push(String(e)));

      const frame = await openWidget(page, { widget });
      const g = await geometry(page, frame);

      // documentElement.scrollHeight is floored at the viewport, so a widget
      // that measured itself that way would report the frame back to the host
      // and could never shrink. The frame starts at 320px for exactly this.
      expect(g.reported).toBe(g.contentHeight);
      expect(g.frameHeight).toBe(g.contentHeight);
      expect(errors).toEqual([]);
    });
  }

  test("collapsing a category shrinks the frame back to the content", async ({ page }) => {
    const frame = await openWidget(page);
    const collapsed = await geometry(page, frame);

    await toggleFirstCategory(frame);
    await settle(page, frame);
    const expanded = await geometry(page, frame);
    expect(expanded.frameHeight).toBeGreaterThan(collapsed.frameHeight);

    await toggleFirstCategory(frame);
    await settle(page, frame);
    const reCollapsed = await geometry(page, frame);

    expect(reCollapsed.frameHeight).toBe(reCollapsed.contentHeight);
    expect(reCollapsed.frameHeight).toBeLessThan(expanded.frameHeight);
  });

  test("expanding a category by hand grows the frame", async ({ page }) => {
    // The native <details> toggle bypasses render(); without its own size report
    // the host never hears about the new height and clips the open category.
    const frame = await openWidget(page);
    const before = await geometry(page, frame);

    await toggleFirstCategory(frame);
    await settle(page, frame);
    const after = await geometry(page, frame);

    expect(after.resizes).toBeGreaterThan(before.resizes);
    expect(after.frameHeight).toBe(after.contentHeight);
    expect(after.documentScrollHeight).toBeLessThanOrEqual(after.innerHeight + 2);
  });

  test("the start widget's autocomplete list is inside the reported height", async ({ page }) => {
    // .suggestions is absolutely positioned, so it hangs outside #root's bounding
    // box: a height measured from that box alone would let the host clip the
    // lower suggestions.
    const frame = await openWidget(page, { widget: "start.html", waitFor: "#search" });
    await frame.fill("#search", "a"); // broad query → list longer than the field
    await expect.poll(async () =>
      frame.$$eval(".suggestions button", (e) => e.length), { timeout: 5000 }).toBeGreaterThan(2);
    await settle(page, frame);

    const g = await geometry(page, frame);
    const suggestionsBottom = await frame.evaluate(() =>
      Math.ceil(document.querySelector(".suggestions").getBoundingClientRect().bottom));
    expect(suggestionsBottom).toBeGreaterThan(g.contentHeight); // hangs outside the box
    expect(g.reported).toBeGreaterThanOrEqual(suggestionsBottom);
    expect(g.frameHeight).toBeGreaterThanOrEqual(suggestionsBottom);
  });

  test("a shrink through an ordinary render is reported too", async ({ page }) => {
    // Same floor, reached without the accordion: grow the frame, then let a
    // render (unticking a module drops it from the timetable) shrink the content.
    const frame = await openWidget(page);
    await toggleFirstCategory(frame);
    await settle(page, frame);
    const expanded = await geometry(page, frame);

    await toggleFirstCategory(frame);
    const boxes = await frame.$$('input[type="checkbox"][data-toggle]:checked');
    await boxes[0].evaluate((el) => el.click()); // render → size report
    await settle(page, frame);
    const after = await geometry(page, frame);

    expect(after.frameHeight).toBeLessThan(expanded.frameHeight);
    expect(after.frameHeight).toBe(after.contentHeight);
  });
});
