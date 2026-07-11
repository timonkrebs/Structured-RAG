# Plan: Real FHNW data — bariapi ingestion, bilingual compile, live fetch-through

## Context

The module-catalog MCP system (merged architecture: offline compiler → static artifacts → zero-inference MCP server) currently runs on hand-written sample data. This session mapped the real FHNW Modulbeschreibungen backend — a **public, auth-free JSON API** at `https://bariapi.fhnw.ch/cit_modulbeschreibungen/prod` fronting the Lucene index. The goal now: ingest real modules, run a real compilation, and serve fresh official details at query time.

**User decisions:** pilot scope = **BSc in Wirtschaftsinformatik** (HSW); compiled artifacts = **bilingual (DE + EN)**; compile LLM = **hosted OpenAI-compatible API** (user provides endpoint + key at run time).

**Verified API facts (from live probes this session):**
- `POST /api/search` body `{searchQuery: {searchText, facetQuery[]}, pagingQuery: {skip, take}}` — returns shallow results (`title`, `planSemesterModulId` only); result count caps at 1000 → enumerate per (semester × studyProgram) slice.
- `facetQuery` items must echo **complete facet objects** from `POST /api/search/facets` (`{name, values: [{displayValueEnglish, displayValueGerman, value}]}`) — matching fails with `value` alone.
- `GET /api/PlanSemesterModul/{planSemesterModulId}` → full bilingual record (`title/titleEN`, `keyIdea`, `courseContent` as **HTML fragments**, `requirements` free-text HTML, `performanceRecords` (JSON strings), `ects`, `studyLevel`, `locations`, `studyPrograms`, `moduleResponsibles` incl. emails).
- `GET /api/PlanSemester/LatestPlanSemester` → currently `27FS` (only 189 modules published yet); **26HS** is the semester students plan now and has full data → default ingest semesters: `26HS` + `27FS`.
- Detail page URL for link-outs: `https://modulbeschreibungen.webapps.fhnw.ch/detail/{planSemesterModulId}?uiLanguage=de`.
- Key identity: `moduleId` (stable across semesters) vs `planSemesterModulId` (per-semester offering, e.g. `26FS_9521316`).
- Hochschule für Technik is **not** in this app (out of scope; noted for user).

## Implementation

### 1. New project `StructuredRAG.Fhnw` (class library, net8.0)
Shared by Compiler (ingestion) and Mcp (fetch-through). Not in Core — Core stays generic.
- `BariApiClient.cs`: typed client for the four endpoints above; throttled (SemaphoreSlim ≈4 concurrent + small delay), 3× retry with backoff. Base URL + politeness settings from config section `BariApi`.
- `BariDtos.cs`: response DTOs (facets, search, detail).
- `SourceModuleMapper.cs`: detail DTO → `SourceModule`. Includes `StripHtml` helper (regex-based tag strip + entity decode). `Description` = keyIdea + courseContent (stripped); assessment summarized from `performanceRecords`; language parsed ("Deutsch" → `de`). **Drop emails and teacher lists** — keep only module-responsible name (privacy posture: don't route personal data through third-party AI clients).

### 2. Model extensions (`StructuredRAG.Core/Models/Catalog/`) — all additive, sample data stays valid
- `SourceModule`: add `ModuleId`, `TitleEn`, `DescriptionEn`, `RequirementsText`/`RequirementsTextEn` (free text for LLM extraction), `Offerings: [{SemesterId, PlanSemesterModulId}]`, `StudyPrograms[]`, `ModuleType`, `Locations[]`, `ResponsibleName`. Existing `OfferedIn` (HS/FS types) derived from offerings when ingesting.
- `CompiledModule`: mirror the above + bilingual compiled fields `SummaryEn`, `AudienceEn`, `TypicalQuestionsEn`; `PrerequisiteNotes` (original free text); `Prerequisites` becomes LLM-extracted **and validated** module ids; `SourceHash` (for incremental compile); `Url` (detail link of newest offering).
- `TagDefinition`: add `NameEn`, `DescriptionEn` (canonical `Name` stays German; tools match either).

### 3. Compiler (`StructuredRAG.Compiler/Program.cs` → subcommands `ingest` | `compile` | `all`)
- **ingest**: facets → for each configured (semester × program) slice: paged search (take 100, warn if a slice hits the 1000 cap) → distinct ids → detail fetch each, with raw-response disk cache `data/raw/{planSemesterModulId}.json` (skip refetch unless `--Ingest:RefreshRaw=true`) → dedupe by `ModuleId` merging offerings → write `data/modules.wirtschaftsinformatik.json`. Config: `Ingest:Semesters` (default `26HS,27FS`), `Ingest:StudyPrograms` (default `BSc in Wirtschaftsinformatik`).
- **compile** (changes in `StructuredRAG.Core/Services/KnowledgeCompilationService.cs`):
  - Bilingual prompts: phase 1 emits DE+EN tag names/descriptions; phase 2 emits summary/audience/questions in both languages in one call (one JSON object — no double LLM cost beyond output tokens).
  - **Prerequisite extraction**: phase 2 receives `RequirementsText` + the in-scope module list (id + title) and returns resolved prerequisite ids + leftover free-text notes.
  - **Taxonomy stability**: if `taxonomy.json` already exists in the output dir, feed it to phase 1 as the base vocabulary ("evolve minimally, keep existing names").
  - **Incremental**: skip re-enriching modules whose `SourceHash` matches the previous `modules.json` (unless `--Compiler:Force=true`).
  - **Validation before writing** (hard-fail): every prerequisite id exists in the catalog; every tag in vocabulary; warn-list modules with 0 tags. Print a compile report.
- `DockerModelRunnerService` (`StructuredRAG.Core`): add optional `DockerModelRunner:ApiKey` → `Authorization: Bearer` header (hosted APIs); drop the nonstandard `timestamp` message fields while touching it.

### 4. MCP server updates (`StructuredRAG.Mcp`)
- `CatalogStore.cs`: score/search over EN fields too; index markdown gains studyProgram/moduleType columns.
- `Tools/ModuleCatalogTools.cs`:
  - `search_modules`: new filters `studyProgram`, `moduleType`; `semester` accepts both type (`HS`/`FS`) and concrete id (`26HS`) — matched against offerings.
  - `plan_semester`: same semester semantics; response includes `PrerequisiteNotes` so the client can reason over unstructured constraints; keep deterministic.
  - `fetch`: **live fetch-through** via `BariApiClient` (1 h in-memory TTL cache) → returns current official content + `Url`; falls back to the compiled record (marked `"source": "compiled"`) if bariapi is unreachable.
- `Program.cs`: register `BariApiClient`; extend `ServerInstructions` (bilingual catalog, `compiledAt` staleness note, "fetch returns authoritative live data").

### 5. Wiring & docs
- Add `StructuredRAG.Fhnw` to `StructuredRAG.sln`; project refs from Compiler + Mcp.
- `appsettings.json` updates (Compiler: `Ingest:*`, `DockerModelRunner:ApiKey` placeholder reading env `DockerModelRunner__ApiKey`; Mcp: `BariApi:BaseUrl`).
- README(s): ingest→compile→serve walkthrough for the pilot; note HT not covered by this source.
- Keep `compiled-sample/` + `data/modules.sample.json` working (new fields optional).

## Execution order
1. `StructuredRAG.Fhnw` project + client + mapper; solution wiring.
2. Model extensions.
3. Compiler subcommands + ingestion; **run real ingest** (≈ ≤300 throttled requests) → inspect JSON.
4. Compilation service changes; verify pipeline with the existing mock-LLM harness (scratchpad `mock_llm.py` pattern).
5. **Real compile** — needs `DockerModelRunner:Endpoint`, `:SimpleModel` (model name), and `DockerModelRunner__ApiKey` from the user; I'll ask for these when reaching this step. Review taxonomy quality (10–40 bilingual tags).
6. Mcp changes; end-to-end smoke test.

## Verification
- Ingest: module count plausible for the program (× 2 semesters), no HTML tags left in text fields, offerings merged per `ModuleId`, raw cache populated.
- Compile (mock, then real): artifacts written; validation report clean; spot-check German + English summaries and prerequisite resolution (e.g. a module whose requirements name another in-scope module).
- Serve: run Mcp against new artifacts; JSON-RPC smoke (reuse this session's curl harness): German query `"Maschinelles Lernen"` via `search`; `search_modules` with a taxonomy tag + `semester=26HS`; `plan_semester` blocks a module on its extracted prerequisite; `fetch` returns live bariapi content with the official URL; kill network→`fetch` falls back to compiled.
- `dotnet build` clean; sample-catalog path still works (Mcp with default `compiled-sample`).

## Out of scope (explicitly deferred)
ChatGPT Apps-SDK widget, scheduled GitHub Action, Mcp auth, Hochschule für Technik data source, embedding/vector search.
