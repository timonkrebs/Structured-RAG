// The semester planner opens on the student's plan, not on the semester's full
// offering (issue #34): a real catalog answers plan_semester with ~90 eligible
// modules, several of which publish a dozen parallel classes (Anlässe), and the
// selection is mostly steered from the chat anyway. These specs cover the two
// halves of that — which view the widget lands in and how a row summarizes its
// classes — plus the ways out of the plan view, because a view that hides the
// catalog has to make getting back to it obvious and cheap.
const { test, expect } = require("@playwright/test");
const {
  VIEWPORT, openWidget, settle, activeView, listedRows, setView,
  deepestTimetableBlock, rowInView, pushHostUpdate, pushToolResult,
} = require("../harness/widget");
const fixture = require("../fixtures/plan-semester.json");

test.use({ viewport: VIEWPORT });

// Mirrors the widget's chip budget (CHIP_LIMIT in semester-planner.html).
const CHIP_LIMIT = 3;
const classCount = (m) =>
  new Set((m.lessons || []).map((l, i) => l.number || `#${i}`)).size;
/** The eligible module with the most parallel classes — the worst chip case. */
const multiClass = fixture.eligible
  .map((e) => e.module)
  .sort((a, b) => classCount(b) - classCount(a))[0];

const chips = (frame, code) =>
  frame.$$eval(`li[data-mod="${code}"] .chip.day`, (els) => els.map((e) => e.textContent.trim()));
const moreButton = (code) => `li[data-mod="${code}"] button.chip.more`;

/**
 * Expands the category a module sits in. Rows inside a closed <details> are in
 * the DOM (and have a box) but cannot be focused, so anything driving the
 * keyboard has to open the category first — as a student would.
 */
async function openCategoryOf(frame, code) {
  await frame.$eval(`li[data-mod="${code}"]`, (el) => {
    const details = el.closest("details");
    if (details && !details.open) details.querySelector("summary").click();
  });
}

/** A copy of the fixture with one module's lessons replaced. */
function withLessons(code, lessons) {
  const plan = JSON.parse(JSON.stringify(fixture));
  plan.eligible.find((e) => e.module.code === code).module.lessons = lessons;
  return plan;
}

test.describe("plan view", () => {
  test("opens on the proposed plan, with the catalog one click away", async ({ page }) => {
    const frame = await openWidget(page);

    expect(await activeView(frame)).toBe("plan");
    expect((await listedRows(frame)).sort()).toEqual([...fixture.proposed].sort());
    expect(await frame.$$eval("details.cat", (e) => e.length)).toBe(0);
    // Both counts sit on the switch, so what the other view holds is readable
    // without going there.
    const labels = await frame.$$eval(".view-switch button", (e) => e.map((b) => b.textContent));
    expect(labels[0]).toContain(String(fixture.proposed.length));
    expect(labels[1]).toContain(String(fixture.eligible.length + fixture.blocked.length));
  });

  test("opens on the catalog when there is nothing selected yet", async ({ page }) => {
    const frame = await openWidget(page, { proposal: false });

    expect(await activeView(frame)).toBe("all");
    expect(await frame.$$eval("details.cat", (e) => e.length)).toBeGreaterThan(1);
  });

  test("switching views keeps both lists intact", async ({ page }) => {
    const frame = await openWidget(page);

    await setView(frame, "all");
    await settle(page, frame);
    expect(await activeView(frame)).toBe("all");
    expect((await listedRows(frame)).length).toBe(fixture.eligible.length + fixture.blocked.length);

    await setView(frame, "plan");
    await settle(page, frame);
    expect(await activeView(frame)).toBe("plan");
    expect((await listedRows(frame)).sort()).toEqual([...fixture.proposed].sort());
  });

  test("the chosen view survives a host re-render", async ({ page }) => {
    // Hosts re-render the widget whenever their own context changes; a view that
    // resets to the default there would fight the student.
    const frame = await openWidget(page);
    await setView(frame, "all");
    await settle(page, frame);

    await pushHostUpdate(page);
    await settle(page, frame);

    expect(await activeView(frame)).toBe("all");
  });

  test("unticking in the plan view leaves the row in place to undo", async ({ page }) => {
    // The row would otherwise vanish under the cursor, and taking the click back
    // would mean finding the module again in the full catalog.
    const frame = await openWidget(page);
    const code = fixture.proposed[0];
    const box = `li[data-mod="${code}"] input[data-toggle]`;

    await frame.$eval(box, (el) => el.click());
    await settle(page, frame);
    expect(await listedRows(frame)).toContain(code);
    expect(await frame.$eval(box, (el) => el.checked)).toBe(false);
    // Listed, but out of the plan: no ECTS, no timetable block.
    expect(await frame.$$eval(`.tt-block[data-nav="${code}"]`, (e) => e.length)).toBe(0);

    await frame.$eval(box, (el) => el.click());
    await settle(page, frame);
    expect(await frame.$eval(box, (el) => el.checked)).toBe(true);
    expect((await listedRows(frame)).sort()).toEqual([...fixture.proposed].sort());
  });

  test("an emptied plan says how to fill it again", async ({ page }) => {
    const frame = await openWidget(page);
    for (const code of fixture.proposed) {
      await frame.$eval(`li[data-mod="${code}"] input[data-toggle]`, (el) => el.click());
    }
    await settle(page, frame);

    // The unticked rows stay listed until the view is rebuilt — then the plan is
    // empty and has to point somewhere.
    await setView(frame, "all");
    await setView(frame, "plan");
    await settle(page, frame);

    expect(await listedRows(frame)).toEqual([]);
    expect(await frame.$eval("#root .empty", (el) => el.textContent)).not.toEqual("");
    expect(await frame.$eval("#send", (el) => el.disabled)).toBe(true);
  });

  test("a timetable block still lands on its row in the plan view", async ({ page }) => {
    // Issue #35's guarantee has to hold in the view the widget now opens in.
    const frame = await openWidget(page);
    const code = await deepestTimetableBlock(frame);

    const block = await frame.$(`.tt-block[data-nav="${code}"]`);
    await block.scrollIntoViewIfNeeded();
    await page.waitForTimeout(250);
    await block.click();
    await page.waitForTimeout(1800);

    expect(await rowInView(frame, code)).toMatchObject({ visible: true });
  });
});

test.describe("class chips", () => {
  test("a module with many parallel classes summarizes them", async ({ page }) => {
    expect(classCount(multiClass)).toBeGreaterThan(3); // fixture sanity
    const frame = await openWidget(page, { view: "all" });

    const shown = await chips(frame, multiClass.code);
    const weekdays = new Set((multiClass.lessons || []).map((l) => l.day));
    // Weekdays plus one chip standing for the classes themselves — never one
    // chip per class.
    expect(shown.length).toBe(weekdays.size + 1);
    expect(shown.length).toBeLessThan(classCount(multiClass));
    expect(shown[shown.length - 1]).toContain(String(classCount(multiClass)));
    // Nothing is lost: the summarized slots stay reachable on hover.
    const title = await frame.$eval(`li[data-mod="${multiClass.code}"] .chip.more`,
      (el) => el.getAttribute("title"));
    for (const l of multiClass.lessons.filter((x) => x.start)) expect(title).toContain(l.start);
  });

  test("a selected module shows the class it occupies, not the alternatives", async ({ page }) => {
    const frame = await openWidget(page, { view: "all" });
    await frame.$eval(`li[data-mod="${multiClass.code}"] input[data-toggle]`, (el) => el.click());
    await settle(page, frame);

    // The picker below the row is where the other classes live now.
    expect(await frame.$$eval(`li[data-mod="${multiClass.code}"] select.class-pick option`,
      (e) => e.length)).toBe(classCount(multiClass));
    // The row now shows the slots of that one class — a class can have two
    // weekly slots — and none of the other classes' times.
    const chosen = multiClass.lessons.filter((l) => l.number === multiClass.lessons[0].number);
    const slots = new Set(chosen.map((l) => `${l.day}|${l.start}|${l.end}`));
    const shown = await chips(frame, multiClass.code);
    expect(shown.length).toBe(slots.size);
    expect(shown.length).toBeLessThan(classCount(multiClass));
    for (const l of chosen) expect(shown.join(" ")).toContain(l.start);
    // ...and they are exactly what the timetable draws for it.
    expect(await frame.$$eval(`.tt-block[data-nav="${multiClass.code}"]`, (e) => e.length))
      .toBe(chosen.filter((l) => l.day && l.start && l.end).length);
  });

  test("the folded-away classes open from the keyboard, not only on hover", async ({ page }) => {
    // A title on a <span> reaches a hovering mouse and nobody else: on touch and
    // by keyboard the summarized times would be unreachable without changing the
    // plan first.
    const frame = await openWidget(page, { view: "all" });
    await openCategoryOf(frame, multiClass.code);
    await settle(page, frame);
    expect(await frame.$eval(moreButton(multiClass.code), (el) => el.getAttribute("aria-expanded")))
      .toBe("false");

    await frame.press(moreButton(multiClass.code), "Enter");
    await settle(page, frame);

    const shown = await chips(frame, multiClass.code);
    expect(await frame.$eval(moreButton(multiClass.code), (el) => el.getAttribute("aria-expanded")))
      .toBe("true");
    // Every class is accounted for: one chip each, except classes that meet at
    // the same time and place, which share a chip carrying their count.
    const listed = shown.slice(0, -1); // last chip is the collapse button
    const total = listed.reduce((n, label) => n + Number((label.match(/×(\d+)$/) || [, 1])[1]), 0);
    expect(total).toBe(classCount(multiClass));
    expect(listed.length).toBeGreaterThan(CHIP_LIMIT);
    // The classes are told apart by where they meet, like in the picker.
    const locations = new Set(multiClass.lessons.map((l) => l.location).filter(Boolean));
    for (const where of locations) expect(shown.join(" | ")).toContain(where);

    await frame.$eval(moreButton(multiClass.code), (el) => el.click());
    await settle(page, frame);
    expect((await chips(frame, multiClass.code)).length).toBeLessThan(classCount(multiClass));
  });

  test("counts parallel classes even when their times look identical", async ({ page }) => {
    // Five classes at two time slots, told apart only by room. The deduplicated
    // times fit in three chips, but the choice must not disappear with them.
    const frame = await openWidget(page, { view: "all" });
    const lessons = [0, 1, 2, 3, 4].map((i) => ({
      number: `26HS.TEST/${i}`, day: i % 2 ? "Tuesday" : "Monday",
      start: "08:15", end: "10:00", location: `Room ${i}`, periodicity: 7,
    }));
    await pushToolResult(page, withLessons(multiClass.code, lessons));
    await settle(page, frame);

    const shown = await chips(frame, multiClass.code);
    expect(shown).toHaveLength(3); // Mo, Di, and the button
    expect(shown[2]).toMatch(/^5\b/);
  });

  test("a chosen class with many sessions counts sessions, not alternatives", async ({ page }) => {
    const frame = await openWidget(page, { view: "all" });
    const lessons = ["Monday", "Tuesday", "Wednesday", "Thursday"]
      .map((d) => ({ number: "26HS.TEST/A", day: d, start: "08:15", end: "10:00", periodicity: 7 }))
      .concat([{ number: "26HS.TEST/B", day: "Friday", start: "13:15", end: "15:00", periodicity: 7 }]);
    await pushToolResult(page, withLessons(multiClass.code, lessons));
    await settle(page, frame);

    // Unticked, the row is about the two classes to choose between.
    expect((await chips(frame, multiClass.code)).pop()).toMatch(/^2\b/);

    await frame.$eval(`li[data-mod="${multiClass.code}"] input[data-toggle]`, (el) => el.click());
    await settle(page, frame);

    // Ticked, it is about the chosen class alone — four sessions a week, and
    // the alternative is the picker's business.
    expect((await chips(frame, multiClass.code)).pop()).toMatch(/^4\b/);
  });
});
