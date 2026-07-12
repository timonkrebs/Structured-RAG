# StructuredRAG.Mcp — Module Catalog MCP Server

A remote MCP server that lets AI assistants (ChatGPT, Claude, IDE agents, …) help
students **find study modules** and **plan their semester**.

Design principle: **all inference happens on the client at query time**. The server
only serves precompiled artifacts produced by `StructuredRAG.Compiler` — it makes no
LLM calls, needs no GPU, and is stateless, so it scales trivially and responds in
milliseconds.

## How the client model works with it

1. Read the `catalog://taxonomy` resource (or call `list_tags`) — the closed tag
   vocabulary with descriptions. The client maps the student's interests onto tags itself.
2. Call `search_modules` (structured filters) or `search` (free text) to find candidates.
3. Call `fetch` for full module details.
4. For semester planning, call `plan_semester` with the student's completed modules —
   the server computes eligibility deterministically (prerequisites, offering semester);
   the client combines eligible modules into a plan matching ECTS target and interests.

## Tools

| Tool | Purpose |
|------|---------|
| `search` | Free-text search, German or English (ChatGPT-connector-compatible shape) |
| `fetch` | One module in full: **current official description fetched live** from the FHNW module directory (TTL-cached, deterministic HTTP — still no inference) plus compiled enrichments; falls back to the compiled record when the API is unreachable (`metadata.source`: `live`/`compiled`) |
| `search_modules` | Structured filtering: tags (German canonical or English alias), semester (`HS`/`FS` or concrete `26HS`), level, module type, study program, ECTS range, language |
| `list_tags` | The closed bilingual tag taxonomy with descriptions and module counts |
| `get_catalog_overview` | Taxonomy + full module index as one markdown blob — ideal first call for clients without resource support (ChatGPT) |
| `plan_semester` | Eligible vs. blocked modules for a semester, given completed modules; includes free-text prerequisite notes, weekdays and the ECTS target for the client's planning reasoning |
| `compare_modules` | 2–4 modules side by side (ECTS, semesters, languages, weekdays, tags, prerequisites, summaries) |

## Resources

| URI | Content |
|-----|---------|
| `catalog://index` | Compact overview of all modules (markdown table) — small enough to load fully into context |
| `catalog://taxonomy` | Tag vocabulary with descriptions |
| `catalog://module/{code}` | Full compiled record of one module (JSON) |

## ChatGPT widgets (Apps SDK)

In ChatGPT, `plan_semester` and `compare_modules` don't just return JSON — they render
interactive widgets via the [Apps SDK](https://developers.openai.com/apps-sdk):

- **Semester plan builder** (`ui://widget/semester-planner.html`, on `plan_semester`):
  pick eligible modules via checkboxes, watch a live ECTS meter against the target
  (`ectsTarget` parameter, default 30), get same-weekday hints and see why blocked
  modules are blocked. "Details" fetches the current official description through the
  `fetch` tool; "Send plan to chat" hands the draft back to the model for review.
- **Module comparer** (`ui://widget/module-comparer.html`, on `compare_modules`):
  side-by-side table with tags shared by all modules highlighted; columns can be
  removed or added (the widget re-calls `compare_modules`).

Mechanics: the widget templates are MCP resources with mime type `text/html+skybridge`;
the tools reference them via `_meta["openai/outputTemplate"]`, and `fetch`,
`search_modules` and `compare_modules` carry `_meta["openai/widgetAccessible"]` so the
widgets may call them. Both widgets are single self-contained HTML files
(`Widgets/*.html`, embedded in the assembly) — deterministic vanilla JS, bilingual
(German/English from the client locale), light/dark theme aware, and they persist their
state (selected modules / compared codes) across chat turns. The interaction itself
(ECTS math, weekday hints, shared tags) runs client-side, so the server stays
zero-inference. Clients without Apps-SDK support simply ignore the `_meta` keys and use
the structured JSON.

Registration is the same as for the other developer-mode tools (see below); widgets
require the connector to be added in ChatGPT's developer mode. After changing widget
`_meta`, refresh the connector — ChatGPT caches tool descriptors.

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
(`bariapi.fhnw.ch`, public) and then compiles. Run on a schedule (cron, GitHub
Actions, scheduled container) from the repo root:

```bash
# 1. Ingest: FHNW API -> data/modules.wirtschaftsinformatik.json (raw cache in data/raw/)
dotnet run --project StructuredRAG.Compiler -- ingest \
  --Ingest:Semesters="26HS;27FS" \
  --Ingest:StudyPrograms="BSc in Wirtschaftsinformatik"

# 2. Compile: source JSON -> compiled artifacts (bilingual, prerequisite extraction)
DockerModelRunner__ApiKey=$LLM_API_KEY \
dotnet run --project StructuredRAG.Compiler -- compile \
  --DockerModelRunner:Endpoint=https://<llm-endpoint>/v1 \
  --DockerModelRunner:SimpleModel=<model>

# or both in one go:  dotnet run --project StructuredRAG.Compiler -- all
```

The compiler works with any OpenAI-compatible chat-completions endpoint (set
`DockerModelRunner:ApiKey` for hosted APIs). Because compilation is offline and
infrequent, this is the place to spend on a strong model — taxonomy quality determines
how well the client can search. Repeat runs are cheap: the previous taxonomy is passed
to the model to keep tag names stable, and modules whose source is unchanged
(SourceHash) are reused without LLM calls. The compiler writes `manifest.json` last,
so a watching MCP server only reloads complete catalogs.
