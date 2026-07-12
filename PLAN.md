# Plan: Lektionen → timetable support in plan_semester

## Context

The FHNW bariapi delivers per-module-instance lesson data — `moduleInstances[]` with
`day`, `startTime`/`endTime` (dummy-date ISO like `1899-12-30T08:15:00` or
`1900-01-01T08:15:00`; only the clock time matters), `number` (Anlass id, e.g.
`…S1.SN/a`), `location`, `language`, `periodicity` (7 = weekly), plus `lecturers[]`
with emails (must keep being dropped). Example: `26HS_9539110` (Advanced Corporate
Finance) = Thursday 08:15–17:00, Windisch. The cached raw data already contains 246
such instances across 118 modules; **38/118 modules have no day/time — everything must
degrade gracefully.**

Today this is lost at the first hop: `ModuleInstanceDto` only declares
`Day/Language/Location`, so times are silently discarded, `plan_semester` exposes
weekday names only, and the semester-planner widget can only warn "same weekday" —
two Friday modules at 08:15–12:00 and 13:15–17:00 are wrongly flagged as clashing.

Goal: carry lesson slots (per parallel class) into the compiled catalog and
`plan_semester`, upgrade clash detection to real time overlap, and render a week
timetable in the semester-planner widget. The server stays deterministic /
zero-inference; the client model gets machine-readable slots to reason over.

Semantics: one `moduleInstances[]` entry = one **Modulanlass** (a class the student
enrolls in). Multiple entries per module = parallel alternatives (pick one); entries
sharing the same `number` belong to the same class (attend all its slots).

## Changes

### 1. Models — `StructuredRAG.Core/Models/Catalog/SourceModule.cs`

New class (same file, next to `ModuleOffering`), shared by source and compiled since
`CompiledModule.Offerings` reuses `ModuleOffering`:

```csharp
/// <summary>One weekly lesson slot of a class (Modulanlass). Modules can have several
/// parallel classes; slots sharing Number belong to the same class.</summary>
public class Lesson
{
    public string? Number { get; set; }      // official Anlass number — groups parallel classes
    public string? Day { get; set; }         // English day name ("Thursday")
    public string? Start { get; set; }       // "08:15" (24h clock), null if unpublished
    public string? End { get; set; }         // "17:00"
    public string? Location { get; set; }    // per-class location, e.g. "Olten"
    public string? Language { get; set; }    // ISO code, e.g. "de"
    public int? Periodicity { get; set; }    // 7 = weekly; anything else rendered as "non-weekly"
}
```

`ModuleOffering` gains `public List<Lesson> Lessons { get; set; } = new();`
(per-offering — HS/FS schedules differ). `Weekdays` stays as-is for compatibility.
All additive → old `compiled-sample/modules.json` and persisted catalogs still deserialize.

### 2. Ingestion — `StructuredRAG.Fhnw`

- `BariDtos.cs` `ModuleInstanceDto`: add `Number`, `Title`, `StartTime`, `EndTime`
  (as `DateTime?` — both dummy-date variants parse fine), `Periodicity` (`int?`).
  Do NOT add `Lecturers`/`MaxTN` (privacy/noise stays dropped by omission).
- `SourceModuleMapper.cs`: new `ExtractLessons(ModuleDetailDto)` →
  `List<Lesson>`: skip instances with neither day nor times; normalize times via
  `dt?.ToString("HH:mm")`; map language through the existing language-parsing helper;
  order by existing `DayIndex`, then start. Wire into `Map` (fills
  `offering.Lessons`); `Merge` needs no change (lessons live per offering).
  `ExtractWeekdays` stays (keeps `Weekdays` consistent).

### 3. Compiler — `StructuredRAG.Core/Services/KnowledgeCompilationService.cs`

Two coupled fixes to hashing/reuse (today: whole-`SourceModule` hash → a
schedule-only change forces full LLM re-enrichment, and a reused module keeps stale
pass-through data):

- `ComputeSourceHash`: hash only the LLM-input fields (exactly the ones the
  enrichment prompt is built from — verify against the prompt builder during
  implementation; expected: Title/TitleEn, Description/DescriptionEn,
  RequirementsText/…En) serialized as an anonymous object.
- Reuse branch (`CompileAsync`, ~line 66): instead of `compiled.Add(prev)`, add a
  helper that takes `prev`'s LLM outputs (Summary/SummaryEn, Audience/…En, Tags,
  TypicalQuestions/…En, Prerequisites*, PrerequisiteNotes) and rebuilds the record
  from the **current** source's pass-through fields (Offerings incl. Lessons,
  Weekdays, Languages, ECTS, …), mirroring the assignments in `CompileModuleAsync`.
  (*Prerequisites: `module.Prerequisites` wins when the source has structured ones —
  same rule as `CompileModuleAsync`.)
- `CompileModuleAsync` itself needs no change: `Offerings = module.Offerings` already
  passes lessons through. No prompt/LLM change — schedule data never reaches the LLM.
- **Call out:** the hash-shape change invalidates all stored hashes → one full
  recompile on the next `compile` run (one-time; afterwards schedule shifts no longer
  trigger LLM calls).

### 4. MCP server — `StructuredRAG.Mcp`

- `Tools/ModuleCatalogTools.cs` `ModuleSummary`: add
  `IReadOnlyList<Lesson> Lessons`.
  - `From(m)` (no semester — used by `compare_modules`): lessons of the **newest**
    offering (`m.Offerings.FirstOrDefault()?.Lessons`, offerings are newest-first) —
    a cross-semester union would mix HS and FS times.
  - `From(m, semester)`: lessons from the matched offerings (same pattern as the
    existing weekday narrowing at line ~445).
  - `plan_semester`/`plan_path`/`compare_modules` then carry lessons automatically
    (their results embed `ModuleSummary`).
- Tool `[Description]`s of `plan_semester` and `compare_modules`: mention lesson
  time slots ("… weekdays and lesson time slots (day, start–end) where published —
  use them to build a clash-free timetable").
- `Program.cs` `ServerInstructions`: one sentence — eligible modules carry lesson
  slots; combine them into a clash-free weekly timetable when planning.
- `Services/LiveModuleFetcher.cs` `BuildResult`: live record now has lessons (it maps
  via `SourceModuleMapper`); add them to the fetch text/metadata so "Details" in the
  widget and `fetch` callers see current official times.
- `CatalogStore.IndexMarkdown`: unchanged (keep the index compact).

### 5. Widget — `StructuredRAG.Mcp/Widgets/semester-planner.html`

Expected new JSON: `eligible[].module.lessons[] = {number, day, start, end, location,
language, periodicity}` (camelCase automatic). All-vanilla, both hosts, de/en, themes —
follow the existing `Host`/`STR`/CSS-variable patterns in the file.

- **Chips** (render fn ~line 450): when a lesson has times, chip shows
  `Do 08:15–17:00` (reuse `day()` abbreviation); day-only fallback unchanged;
  `periodicity != 7` appends a "non-weekly" marker.
- **Class choice**: group a module's lessons by `number` (null → each its own
  group). >1 group ⇒ render a small selector on the selected module row; default =
  first group. Chosen group is what occupies the timetable.
- **Clash detection** (`clashes()`, ~line 375): for selected modules, compare chosen
  groups' slots — clash iff same day AND `startA < endB && startB < endA` (minutes
  since midnight). Modules without times keep the existing same-weekday warning but
  worded as "possible" clash. Hint line shows times.
- **Week timetable grid**: new section below the module list, rendered only when ≥1
  selected module has timed lessons. Day columns (only days that occur, Mon-first),
  vertical time axis spanning min/max lesson times (rounded to full hours), lesson
  blocks absolutely positioned per column showing code + start–end; overlapping
  blocks get the existing warning color. Pure divs + CSS vars (no dependencies).
- **State**: `{selectedCodes, ectsTarget, chosenClasses: {code → number}}` — old
  persisted state without `chosenClasses` falls back to defaults (backward
  compatible). Include chosen day/times in `draftSummary()` so the host model sees
  the draft timetable.
- **module-comparer.html** (minor): render a lessons/times row from
  `module.lessons` next to the existing weekday chips.

### 6. Data refresh

- `compiled-sample/modules.json`: add `offerings` (with `lessons`) to the 10 sample
  modules — realistic slots incl. one true clash pair, one time-free module, one
  module with two parallel classes — so the widget and smoke tests work without a
  compile run. Bump `compiled-sample/manifest.json` `compiledAt`.
- `data/modules.wirtschaftsinformatik.json`: re-run
  `dotnet run --project StructuredRAG.Compiler -- ingest` (raw cache in `data/raw/`
  already contains the instance data; re-mapping fills lessons).

## Out of scope

Rooms/lecturer schedules, non-weekly date-series expansion (block courses beyond the
periodicity marker), server-side timetable packing (the client model plans — core
architecture principle), `catalog://index` columns.

## Verification

1. `dotnet build` clean (0 warnings).
2. Ingest re-run → spot-check `data/modules.wirtschaftsinformatik.json`: lessons
   present with `"start": "08:15"`-style values, no dummy dates, no emails anywhere
   (`grep -c '@fhnw.ch'` must stay 0), module `9539110` shows Thursday 08:15–17:00.
3. Serve `compiled-sample` (needs `DOTNET_ROLL_FORWARD=LatestMajor` in this
   codespace) → JSON-RPC smoke: `plan_semester {semester:"HS"}` structuredContent
   contains `lessons`; old-shape catalog (pre-change JSON) still loads (compat).
4. Widget: stub harness in the scratchpad (fake `window.openai.toolOutput` +
   postMessage host) exercising: chips with times, clash pair flagged with times,
   parallel-class selector, timetable grid renders/hides, de/en + dark mode.
5. Optional end-to-end: full recompile with `--Llm:Provider=codex-cli`, then
   re-check plan_semester against real data.

---

## Backlog (unchanged, from the previous plan)

- Scheduled ingest + compile (GitHub Action / cron container)
- MCP server authentication (OAuth per the MCP spec authorization flow)
- Hochschule für Technik data source (not covered by bariapi)
- Embedding/vector search
