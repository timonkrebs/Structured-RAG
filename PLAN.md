# Plan: Real FHNW data — bariapi ingestion, bilingual compile, live fetch-through

> **Status: implemented.** Everything this plan described has shipped — the
> `StructuredRAG.Fhnw` ingestion client, bilingual compilation with prerequisite
> extraction, the MCP server updates and the live fetch-through. The current,
> authoritative documentation is [README.md](README.md) and
> [StructuredRAG.Mcp/README.md](StructuredRAG.Mcp/README.md); this file only tracks
> what happened to the plan's deferred items.

## Shipped beyond the original scope

Several items the plan explicitly deferred have since been implemented:

- **Interactive widgets** — `plan_semester`, `compare_modules` and `plan_path` render
  self-contained HTML widgets (`StructuredRAG.Mcp/Widgets/`) via both the OpenAI
  Apps SDK (ChatGPT) and the standardized MCP Apps extension (Claude, VS Code, …).
- **`plan_path` tool** — deterministic fastest-path scheduling to a target module:
  missing transitive prerequisites placed into the earliest possible semesters,
  respecting prerequisite order and the HS/FS offering rhythm.
- **Pluggable compile LLM (`ILlmClient`)** — the compiler no longer assumes a hosted
  OpenAI-compatible API. The **preferred transport is the OpenAI Codex CLI**
  (`--Llm:Provider=codex-cli`, `StructuredRAG.Core/Services/CodexCliService.cs`): it
  runs `codex exec` headless and reuses a ChatGPT login (`codex login`), so no API
  key needs to be managed. Any OpenAI-compatible endpoint remains available as the
  alternative (`Llm:Provider=openai`, `DockerModelRunnerService`).

## Still open

- Scheduled ingest + compile (GitHub Action / cron container)
- MCP server authentication (OAuth per the MCP spec authorization flow)
- Hochschule für Technik data source (not covered by bariapi — the app spans only the
  6 schools Wirtschaft, Pädagogik, Musik, Gestaltung/Kunst, Soziale Arbeit, Psychologie)
- Embedding/vector search
