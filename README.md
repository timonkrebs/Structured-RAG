# Structured-RAG — FHNW Module Catalog MCP Server

An MCP server that lets AI assistants (ChatGPT, Claude, IDE agents, …) help students
**find FHNW study modules** and **plan their semesters**.

The architecture is built for capable client models: **all LLM inference happens
offline at compile time or on the client at query time — the serving layer does
none.** Instead of embedding raw text at query time, a strong model compiles the
catalog once into structured, tagged artifacts ("structured RAG"), and the connected
client model does the reasoning over them.

```
                 daily/weekly (cron)                          query time
┌──────────────┐   ┌──────────────────────┐   ┌───────────┐   ┌──────────────────────┐
│ module data  │ → │ StructuredRAG.       │ → │ compiled/ │ → │ StructuredRAG.Mcp    │ ⇄ ChatGPT / Claude
│ (FHNW API)   │   │ Compiler (LLM-heavy) │   │ JSON      │   │ (zero inference)     │   (does the reasoning)
└──────────────┘   └──────────────────────┘   └───────────┘   └──────────────────────┘
```

- **`StructuredRAG.Compiler`** — offline knowledge compilation, run daily/weekly: one
  LLM pass designs a **closed tag taxonomy** over the whole catalog, then each module is
  enriched against that vocabulary (retrieval-optimized summary, target audience,
  typical student questions). The LLM transport is pluggable (`ILlmClient`): the
  preferred provider is the **OpenAI Codex CLI** (`--Llm:Provider=codex-cli`), which
  reuses a ChatGPT login (`codex login`) instead of an API key; any OpenAI-compatible
  endpoint works as the alternative. Spend on a strong model here — compilation runs
  rarely.
- **`StructuredRAG.Mcp`** — stateless MCP server over the compiled artifacts:
  ChatGPT-connector-compatible `search`/`fetch`, structured `search_modules`,
  `list_tags`, a deterministic `plan_semester` (prerequisite/semester eligibility),
  `compare_modules`, `plan_path` (fastest route to a target module), and MCP resources
  (`catalog://index`, `catalog://taxonomy`) clients can load into context.
- **`StructuredRAG.Fhnw`** — client for the official FHNW Modulbeschreibungen API
  (public), used for ingestion and the live fetch-through at query time.
- **`StructuredRAG.Core`** — shared models, the `ILlmClient` abstraction and the
  knowledge compilation service.

## Quick start

Runs immediately against the hand-compiled sample catalog in `compiled-sample/`
(10 sample modules):

```bash
dotnet run --project StructuredRAG.Mcp     # MCP endpoint at http://localhost:<port>/mcp
```

Or containerized:

```bash
docker build -t structured-rag-mcp .
docker run -p 8080:8080 structured-rag-mcp   # MCP endpoint at http://localhost:8080/mcp
```

## Using it from ChatGPT or Claude

Hosted clients need the server reachable over public HTTPS — deploy it (free options
in [DEPLOYMENT.md](DEPLOYMENT.md)), or tunnel for testing (e.g. `ngrok http 5210`).
Then register it:

- **ChatGPT (web)**: Settings → Connectors → Advanced → *Developer mode* → add a
  custom connector with URL `https://<your-host>/mcp`.
- **Claude (claude.ai / Claude Code)**: add a custom connector, or
  `claude mcp add --transport http module-catalog https://<your-host>/mcp`.

From there, just talk to the assistant — it picks the right tools. Typical
interactions (module codes from the sample catalog):

| You ask (DE or EN) | What happens |
|---|---|
| *"Which modules teach me machine learning?"* | `search` / `search_modules` over the compiled tags and bilingual summaries |
| *"Tell me more about Machine Learning and Data Mining"* | `fetch` returns the **current official description** live from the FHNW module directory, plus compiled enrichments and the official URL |
| *"Plan my autumn semester — I've completed oop1 and stat, target 30 ECTS"* | `plan_semester` computes eligible vs. blocked modules deterministically; the client model assembles the plan |
| *"Compare mldm, webec and clco"* | `compare_modules` returns a side-by-side comparison |
| *"When can I take nlpai at the earliest? I start in 26HS."* | `plan_path` schedules the missing transitive prerequisites (oop1 → algd/stat → mldm → nlpai) into the earliest possible semesters, respecting the HS/FS offering rhythm |

In widget-capable hosts (ChatGPT via the OpenAI Apps SDK; Claude and others via the
standardized MCP Apps extension), `plan_semester`, `compare_modules` and `plan_path`
additionally render **interactive widgets** — a semester plan builder with live ECTS
meter, a comparison table, and a path timeline where marking a prerequisite as
completed re-plans the route live. Other clients simply use the structured JSON.

See [StructuredRAG.Mcp/README.md](StructuredRAG.Mcp/README.md) for the full tool,
resource and widget reference.

## Real data: FHNW module catalog pilot

`StructuredRAG.Fhnw` connects the pipeline to the official FHNW Modulbeschreibungen
API (public). The pilot scope is *BSc in Wirtschaftsinformatik*; ingested source data
lives in `data/modules.wirtschaftsinformatik.json`:

```bash
dotnet run --project StructuredRAG.Compiler -- ingest    # FHNW API -> source JSON
dotnet run --project StructuredRAG.Compiler -- compile --Llm:Provider=codex-cli   # LLM compile -> compiled/
Catalog__CompiledPath=compiled dotnet run --project StructuredRAG.Mcp
```

The compile step above uses the Codex CLI (install and log in once:
`npm i -g @openai/codex && codex login`); drop the `--Llm:Provider` flag to use an
OpenAI-compatible endpoint instead — see
[StructuredRAG.Mcp/README.md](StructuredRAG.Mcp/README.md) for both variants.

The compilation is bilingual (DE/EN), extracts structured prerequisite links from the
official free-text requirements, keeps tag names stable across runs, and skips
unchanged modules. At query time, `fetch` passes through to the live FHNW API so
module details are always current — the compiled catalog is the index, the official
catalog stays the source of truth. Note: the source app covers 6 FHNW schools
(Wirtschaft, Pädagogik, Musik, Gestaltung/Kunst, Soziale Arbeit, Psychologie) —
Hochschule für Technik is not included.

## Project structure

```
Structured-RAG/
├── StructuredRAG.Core/            # Shared library
│   ├── Models/Catalog/            # Source/compiled module + taxonomy models
│   └── Services/
│       ├── ILlmClient.cs                  # LLM transport abstraction (compiler)
│       ├── CodexCliService.cs             # ILlmClient via OpenAI Codex CLI — preferred
│       ├── DockerModelRunnerService.cs    # ILlmClient via OpenAI-compatible HTTP endpoint
│       └── KnowledgeCompilationService.cs # Offline taxonomy + module enrichment
├── StructuredRAG.Compiler/        # Offline pipeline CLI: ingest | compile | all
├── StructuredRAG.Fhnw/            # FHNW Modulbeschreibungen API client + mapping
├── StructuredRAG.Mcp/             # Zero-inference MCP server (tools, resources, widgets)
├── compiled-sample/               # Hand-compiled sample catalog
├── data/                          # Ingested source JSON (+ raw response cache)
├── Dockerfile                     # Container for the MCP server
└── README.md
```

See [DEVELOPMENT.md](DEVELOPMENT.md) for the developer guide and
[PLAN.md](PLAN.md) for the remaining backlog.

> Note: the server ships without authentication. For production, put it behind OAuth
> (the MCP spec's authorization flow) or at minimum a reverse proxy.

## License

See LICENSE file for details.

## Contributing

Contributions are welcome! Please open an issue or submit a pull request.
