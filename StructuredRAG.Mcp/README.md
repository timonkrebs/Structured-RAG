# StructuredRAG.Mcp — Module Catalog MCP Server

A remote MCP server that lets AI assistants (ChatGPT, Claude, IDE agents, …) help
students **find study modules** and **plan their semester**.

Design principle: **all inference happens on the client at query time**. The server
only serves precompiled artifacts produced by `StructuredRAG.Compiler` — it makes no
LLM calls, needs no GPU, and is stateless, so it scales trivially and responds in
milliseconds.

## How the client model works with it

1. Map the student's interests onto the tag vocabulary that arrives with the MCP
   initialize instructions (tag names + module counts); `catalog://taxonomy` or
   `list_tags` add the tag descriptions.
2. Call `search_modules` (boolean tag filters + structured criteria; `includeFacets`
   returns per-tag counts of the match set for the next narrowing step) or `search`
   (free text) to find candidates. The server instructions steer the client
   recall-first: wide `anyOfTags` sweeps over stacked `allOfTags` intersections,
   since compiled tags are approximate and the compact format makes wide results cheap.
3. Call `fetch` for full module details.
4. For semester planning, call `plan_semester` with the student's completed modules —
   the server computes eligibility deterministically (prerequisites, offering semester);
   the client combines eligible modules into a plan matching ECTS target and interests.

## Tools

| Tool | Purpose |
|------|---------|
| `search` | Free-text search, German or English (ChatGPT-connector-compatible shape) |
| `fetch` | One module in full: **current official description fetched live** from the FHNW module directory (TTL-cached, deterministic HTTP — still no inference) plus compiled enrichments; falls back to the compiled record when the API is unreachable (`metadata.source`: `live`/`compiled`) |
| `search_modules` | Boolean tag filtering (`allOfTags`/`anyOfTags`/`noneOfTags`; German canonical or English alias) plus semester (`HS`/`FS` or concrete `26HS`), level, module type, study program, ECTS range, language and free text. Returns `total`, the matches as `compact` (default) / `full` / `codes` with optional `limit`, and per-tag counts of the match set via `includeFacets` (faceted drill-down) |
| `list_tags` | The closed bilingual tag taxonomy with descriptions and module counts |
| `get_catalog_overview` | Taxonomy + full module index as one markdown blob — ideal first call for clients without resource support (ChatGPT) |
| `plan_semester` | Eligible vs. blocked modules for a semester, given completed modules; includes free-text prerequisite notes, weekdays and the ECTS target for the client's planning reasoning |
| `compare_modules` | 2–4 modules side by side (ECTS, semesters, languages, weekdays, tags, prerequisites, summaries) |
| `plan_path` | Fastest way to reach a target module: missing transitive prerequisites scheduled into the earliest possible semesters (prerequisite order + HS/FS offering rhythm), earliest completion semester, total ECTS |

## Resources

| URI | Content |
|-----|---------|
| `catalog://index` | Compact overview of all modules (markdown table) — small enough to load fully into context |
| `catalog://taxonomy` | Tag vocabulary with descriptions |
| `catalog://module/{code}` | Full compiled record of one module (JSON) |

## Interactive widgets (ChatGPT Apps SDK + MCP Apps)

`plan_semester`, `compare_modules` and `plan_path` don't just return JSON — in hosts
with widget support they render interactive UI:

- **Semester plan builder** (on `plan_semester`): opens on the plan itself — the
  modules the assistant proposed, their week timetable and a live ECTS meter against
  the target (`ectsTarget` parameter, default 30) — with a switch to the semester's
  full offering, where eligible modules are picked via checkboxes inside the category
  accordion. Clash hints, class (Anlass) pickers and the reasons blocked modules are
  blocked come along; "Details" fetches the current official description through the
  `fetch` tool, and "Send plan to chat" hands the draft back to the model for review.
- **Module comparer** (on `compare_modules`): side-by-side table with tags shared by
  all modules highlighted; columns can be removed or added (the widget re-calls
  `compare_modules`).
- **Path planner** (on `plan_path`): semester-by-semester timeline to a target module —
  "when can I take Machine Learning at the earliest?" Waiting semesters are shown when
  the HS/FS offering rhythm forces a gap; marking a prerequisite as already completed
  re-plans the path live (the widget re-calls `plan_path`).

The same self-contained HTML files (`Widgets/*.html`, embedded in the assembly)
serve **both host conventions** — each widget detects its host at runtime. The
host bridge that abstracts the two, the shared helpers and the design tokens live
in `Widgets/_host.js` and `Widgets/_tokens.css`, inlined into each page at serve
time (see DEVELOPMENT.md), so the pages stay self-contained without four copies:

| | OpenAI Apps SDK (ChatGPT) | [MCP Apps extension](https://modelcontextprotocol.io/extensions/apps/overview) (Claude, VS Code, …) |
|---|---|---|
| Template resource | `ui://widget/*.html`, mime `text/html+skybridge` | `ui://module-catalog/*`, mime `text/html;profile=mcp-app` |
| Tool link | `_meta["openai/outputTemplate"]` | `_meta.ui.resourceUri` |
| Widget ↔ host | `window.openai` | JSON-RPC 2.0 over `postMessage` (`ui/initialize`, `tools/call`, `ui/message`, `ui/open-link`, …) |
| Draft persistence | `setWidgetState` across turns | `ui/update-model-context` — the model sees the student's current draft |

The widgets are deterministic vanilla JS, bilingual (German/English from the host
locale) and light/dark theme aware; all interaction (ECTS math, weekday hints, shared
tags) runs client-side, so the server stays zero-inference. `fetch`, `search_modules`,
`compare_modules` and `plan_path` carry `_meta["openai/widgetAccessible"]` so the
ChatGPT widgets may call them (MCP Apps tool calls need no extra flag; `visibility`
defaults allow them). Hosts without widget support simply ignore the `_meta` keys and use the
structured JSON.

Registration is the same as for the other developer-mode tools (see below). ChatGPT
requires developer mode and caches tool descriptors — refresh the connector after
changing widget `_meta`.

## Running

```bash
dotnet run --project StructuredRAG.Mcp
```

The MCP endpoint is `http://<host>:<port>/mcp` (streamable HTTP, stateless).
`GET /` returns a health/info document. The compiled artifact directory is configured
via `Catalog:CompiledPath` (defaults to `../compiled-sample`). The server picks up a
newly compiled catalog automatically by watching the `manifest.json` timestamp — no
restart needed after a compiler run.

## Registering in clients

The server must be reachable over HTTPS on the public internet for hosted clients
(deploy it, or use a tunnel like `ngrok http 5210` for testing).

- **ChatGPT (web)**: Settings → Connectors → Advanced → *Developer mode*, then add a
  custom connector with URL `https://<your-host>/mcp`. The `search`/`fetch` tools follow
  the connector contract, so the server also works for ChatGPT's search-based features;
  the richer tools (`search_modules`, `plan_semester`) are available in developer mode.
- **Claude (claude.ai / Claude Code)**: add a custom connector / `claude mcp add
  --transport http module-catalog https://<your-host>/mcp`. Claude also consumes the
  MCP resources, so the whole catalog index and taxonomy can be attached to a chat.

> Note: this sample ships without authentication. For production, put the server
> behind OAuth (the MCP spec's authorization flow) or at minimum a reverse proxy.

## Updating the catalog (daily/weekly)

The pipeline ingests directly from the official FHNW Modulbeschreibungen API
(`bariapi.fhnw.ch`, public) and then compiles. In this repo the automated path is
`.github/workflows/refresh-catalog.yml` (on-demand run that opens a review PR —
see `DEPLOYMENT.md`); to run it by hand from the repo root:

```bash
# 1. Ingest: FHNW API -> data/modules.wirtschaftsinformatik.json (raw cache in data/raw/)
dotnet run --project StructuredRAG.Compiler -- ingest \
  --Ingest:Semesters="26HS;27FS" \
  --Ingest:StudyPrograms="BSc in Wirtschaftsinformatik"

# 2. Compile: source JSON -> compiled artifacts (bilingual, prerequisite extraction).
#    Preferred LLM transport: the OpenAI Codex CLI in headless mode — it reuses a
#    ChatGPT login instead of an API key (once: npm i -g @openai/codex && codex login)
dotnet run --project StructuredRAG.Compiler -- compile --Llm:Provider=codex-cli

#    Alternative: any OpenAI-compatible chat-completions endpoint
DockerModelRunner__ApiKey=$LLM_API_KEY \
dotnet run --project StructuredRAG.Compiler -- compile \
  --DockerModelRunner:Endpoint=https://<llm-endpoint>/v1 \
  --DockerModelRunner:SimpleModel=<model>

# or ingest + compile in one go:  dotnet run --project StructuredRAG.Compiler -- all
```

The compiler talks to the LLM through the `ILlmClient` abstraction with two providers:
`Llm:Provider=codex-cli` shells out to `codex exec` (configured via `CodexCli:Command`,
`CodexCli:Model`, `CodexCli:ExtraArgs`, `CodexCli:TimeoutSeconds`; per-call latency is
higher than a raw HTTP endpoint, which is fine for this offline path), and
`Llm:Provider=openai` (the default) works with any OpenAI-compatible chat-completions
endpoint (set `DockerModelRunner:ApiKey` for hosted APIs). Because compilation is
offline and infrequent, this is the place to spend on a strong model — taxonomy quality determines
how well the client can search. Repeat runs are cheap: the previous taxonomy is passed
to the model to keep tag names stable, and modules whose source is unchanged
(SourceHash) are reused without LLM calls. The compiler writes `manifest.json` last,
so a watching MCP server only reloads complete catalogs.
