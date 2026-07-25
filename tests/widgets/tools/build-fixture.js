// Regenerates the widget test fixtures from the compiled catalog:
//   fixtures/plan-semester.json  → plan_semester (semester-planner widget)
//   fixtures/start.json          → get_started  (start widget)
//
// The fixture is committed so the widget tests stay deterministic while the
// catalog keeps moving (refresh-catalog rewrites compiled/ wholesale). It is
// generated from real data rather than hand-written so the payload keeps the
// exact shape ModuleCatalogTools.PlanSemester emits — see the SemesterPlanData
// / PlannableModule / ModuleSummary records there.
//
//   node tools/build-fixture.js [path/to/compiled/modules.json]
//
// What the scenarios need from it: several module types (the widget only draws
// the accordion when there is more than one category), enough rows that opening
// a category outgrows the frame, and a handful of modules with timed lessons in
// the target semester so the timetable renders clickable blocks.
const fs = require("fs");
const path = require("path");

const SEMESTER = "26HS";
const PER_TYPE = 8;         // modules kept per category
const SUMMARY_CHARS = 160;  // trimmed: the fixture is read by humans in diffs

const src = process.argv[2] ||
  path.join(__dirname, "..", "..", "..", "compiled", "modules.json");
const fixtureDir = path.join(__dirname, "..", "fixtures");
const out = path.join(fixtureDir, "plan-semester.json");
const startOut = path.join(fixtureDir, "start.json");

const all = JSON.parse(fs.readFileSync(src, "utf8"));
const matching = (m) => (m.offerings || [])
  .filter((o) => String(o.semesterId || "").toUpperCase() === SEMESTER);
const newestLessons = (offerings) =>
  (offerings.find((o) => (o.lessons || []).length > 0) || {}).lessons || [];
const timed = (lessons) => lessons.some((l) => l.day && l.start);

function summarize(m) {
  const offs = matching(m);
  const text = m.summary || m.description || "";
  return {
    code: m.code,
    title: m.title,
    titleEn: m.titleEn || null,
    ects: m.ects,
    level: m.level,
    moduleType: m.moduleType || null,
    offeredIn: m.offeredIn || [],
    offerings: offs.map((o) => ({
      semesterId: o.semesterId,
      languages: o.languages || [],
      weekdays: o.weekdays || [],
      lessons: o.lessons || [],
    })),
    languages: m.languages || [],
    weekdays: [...new Set(offs.flatMap((o) => o.weekdays || []))],
    lessons: newestLessons(offs),
    tags: (m.tags || []).slice(0, 5),
    summary: text.slice(0, SUMMARY_CHARS),
    summaryEn: null,
    audience: m.audience || "",
    prerequisites: m.prerequisites || [],
    prerequisiteGroups: m.prerequisiteGroups || [],
    recommended: m.recommended || [],
    prerequisiteNotes: m.prerequisiteNotes || null,
    url: m.url || null,
  };
}

// Offered this semester, split the way PlanSemester splits it: modules with
// open structured prerequisites are blocked, the rest are eligible.
const offered = all.filter((m) => matching(m).length > 0);
const byType = new Map();
for (const m of offered.sort((a, b) => a.code.localeCompare(b.code))) {
  if ((m.prerequisiteGroups || []).length > 0) continue;
  const key = m.moduleType || "Weitere";
  if (!byType.has(key)) byType.set(key, []);
  byType.get(key).push(m);
}

// Keep the modules with a real timetable slot first — they are what the
// navigation scenarios click on — then fill each category up to PER_TYPE.
const eligible = [];
for (const [, mods] of byType) {
  const withSlots = mods.filter((m) => timed(newestLessons(matching(m))));
  const without = mods.filter((m) => !timed(newestLessons(matching(m))));
  for (const m of [...withSlots, ...without].slice(0, PER_TYPE)) {
    eligible.push({ module: summarize(m), interestMatches: [], missingRecommended: [] });
  }
}
eligible.sort((a, b) => a.module.code.localeCompare(b.module.code));

const blocked = offered
  .filter((m) => (m.prerequisiteGroups || []).length > 0)
  .slice(0, 3)
  .map((m) => ({ module: summarize(m), missingPrerequisiteGroups: m.prerequisiteGroups }));

// Preselect the timetable modules: the grid only draws selected classes.
const proposed = eligible
  .filter((e) => timed(e.module.lessons))
  .slice(0, 6)
  .map((e) => e.module.code);

const data = {
  semester: SEMESTER,
  eligible,
  blocked,
  totalEligibleEcts: eligible.reduce((a, e) => a + e.module.ects, 0),
  ectsTarget: 30,
  notOfferedCount: all.length - offered.length,
  completedCount: 0,
  proposed,
  proposedDropped: [],
  proposedClasses: {},
  note: "",
};

const categories = [...new Set(eligible.map((e) => e.module.moduleType))];
if (categories.length < 2) throw new Error("fixture needs >1 module type for the accordion");
if (proposed.length < 2) throw new Error("fixture needs timetable blocks to click");

fs.mkdirSync(fixtureDir, { recursive: true });
fs.writeFileSync(out, JSON.stringify(data, null, 1) + "\n");
console.log(`${out}: ${eligible.length} eligible in ${categories.length} categories ` +
  `(${categories.join(", ")}), ${blocked.length} blocked, ${proposed.length} preselected`);

// StartData for the start widget. Its completed-modules picker autocompletes
// over this list client-side, which is what the overflow sizing test drives.
const start = {
  program: "BSc in Wirtschaftsinformatik",
  moduleCount: all.length,
  tagCount: [...new Set(all.flatMap((m) => m.tags || []))].length,
  compiledAt: "2026-01-01T00:00:00Z",
  semesters: ["26HS", "27FS"],
  modules: all
    .slice()
    .sort((a, b) => a.code.localeCompare(b.code))
    .map((m) => ({ code: m.code, title: m.title, titleEn: m.titleEn || null, ects: m.ects })),
};
fs.writeFileSync(startOut, JSON.stringify(start, null, 1) + "\n");
console.log(`${startOut}: ${start.modules.length} modules for the picker`);
