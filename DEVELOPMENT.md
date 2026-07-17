# Development Guide

The solution implements one pipeline: offline knowledge compilation feeding a
zero-inference MCP server for FHNW study-module search and semester planning.

## Solution Layout

```
StructuredRAG.sln
├── StructuredRAG.Core/        # Shared library
│   ├── Models/Catalog/        #   SourceModule, CompiledModule, TagDefinition, manifest
│   └── Services/
│       ├── ILlmClient.cs                  # LLM transport abstraction used by the compiler
│       ├── CodexCliService.cs             # ILlmClient: OpenAI Codex CLI (`codex exec`) — preferred
│       ├── DockerModelRunnerService.cs    # ILlmClient: OpenAI-compatible HTTP endpoint
│       └── KnowledgeCompilationService.cs # Taxonomy design + module enrichment (offline)
├── StructuredRAG.Compiler/    # Offline pipeline CLI: ingest | compile | all
├── StructuredRAG.Fhnw/        # FHNW Modulbeschreibungen API (bariapi) client + mapping
└── StructuredRAG.Mcp/         # Stateless MCP server (tools, resources, widgets)
```

## Data Flow

```
1. ingest   StructuredRAG.Compiler -- ingest
            FHNW bariapi -> data/modules.wirtschaftsinformatik.json (raw cache in data/raw/)
   ↓
2. compile  StructuredRAG.Compiler -- compile
            LLM designs a closed tag taxonomy, enriches each module bilingually (DE/EN),
            extracts prerequisite links -> compiled/{taxonomy,modules,manifest}.json
   ↓
3. serve    StructuredRAG.Mcp
            Zero-inference MCP server over the compiled artifacts; watches manifest.json
            and reloads automatically; `fetch` passes through live to bariapi (TTL cache)
```

## Key Services

- **`BariApiClient`** (`StructuredRAG.Fhnw`): typed, throttled client for the public
  FHNW Modulbeschreibungen API; `SourceModuleMapper` converts detail records to
  `SourceModule` (HTML stripped, personal data dropped except the responsible's name).
- **`KnowledgeCompilationService`** (`StructuredRAG.Core`): two LLM phases — taxonomy
  design over the whole catalog, then per-module enrichment (summary, audience,
  typical questions, prerequisite extraction; DE + EN in one call). Keeps tag names
  stable across runs by feeding the previous taxonomy back in, and skips modules with
  an unchanged `SourceHash`.
- **`CatalogStore`** (`StructuredRAG.Mcp`): in-memory catalog with free-text scoring
  over DE/EN fields; reloads when `manifest.json` changes.
- **Tools/Resources/Widgets** (`StructuredRAG.Mcp`): see
  [StructuredRAG.Mcp/README.md](StructuredRAG.Mcp/README.md) for the tool reference.
  The widget HTML files in `Widgets/` are embedded in the assembly and served as MCP
  resources for both the OpenAI Apps SDK (ChatGPT) and the MCP Apps extension.

## LLM Transport (`ILlmClient`)

The compiler is the only component that calls an LLM, via the `ILlmClient`
abstraction. Select the provider with `Llm:Provider`:

- **`codex-cli` (preferred)** — `CodexCliService` runs the official OpenAI Codex CLI
  headless (`codex exec --sandbox read-only`). Authentication comes from the CLI's own
  ChatGPT login (`codex login`), so a ChatGPT subscription powers the compilation
  without managing an API key. Config: `CodexCli:Command` (default `codex`),
  `CodexCli:Model`, `CodexCli:ExtraArgs`, `CodexCli:TimeoutSeconds` (default 600).
  Per-call latency is much higher than a raw HTTP endpoint — fine for the offline
  compile, unsuitable for anything interactive.
- **`openai` (default)** — `DockerModelRunnerService` calls any OpenAI-compatible
  chat-completions endpoint: Docker Model Runner locally, or a hosted API. Config:
  `DockerModelRunner:Endpoint`, `DockerModelRunner:SimpleModel`,
  `DockerModelRunner:ApiKey` (for hosted APIs), `DockerModelRunner:TimeoutSeconds`.

## Configuration

All compiler settings live in `StructuredRAG.Compiler/appsettings.json` and can be
overridden via environment variables or `--Section:Key=value` arguments:

| Section | Purpose |
|---------|---------|
| `Llm:Provider` | `codex-cli` or `openai` (see above) |
| `CodexCli:*`, `DockerModelRunner:*` | Provider settings (see above) |
| `BariApi:*` | Base URL, max concurrency, politeness delay |
| `Ingest:*` | Semesters (`26HS;27FS`), study programs, raw cache path/TTL, output path |
| `Compiler:*` | Source path, output path, `Force` (recompile unchanged modules) |

The MCP server (`StructuredRAG.Mcp/appsettings.json`) needs `Catalog:CompiledPath`
(default `../compiled-sample`) and `BariApi:*` for the live fetch-through.

## Development Workflow

```bash
# Run the MCP server against the hand-compiled sample catalog
dotnet run --project StructuredRAG.Mcp        # endpoint: http://localhost:<port>/mcp

# Full pipeline against real FHNW data
dotnet run --project StructuredRAG.Compiler -- ingest
dotnet run --project StructuredRAG.Compiler -- compile --Llm:Provider=codex-cli
Catalog__CompiledPath=compiled dotnet run --project StructuredRAG.Mcp

# Containerized server (sample catalog baked in, real catalog mountable)
docker build -t structured-rag-mcp .
docker run -p 8080:8080 structured-rag-mcp
```

The server is plain streamable HTTP, so it can be smoke-tested with `curl` JSON-RPC
requests against `/mcp` (`initialize`, `tools/list`, `tools/call`); `GET /` returns a
health/info document. To test widget rendering, register the server in ChatGPT
developer mode or Claude via a public tunnel (e.g. `ngrok http 5210`).

## Testing

There is no test project yet. To add one:

```bash
dotnet new xunit -n StructuredRAG.Tests
dotnet sln add StructuredRAG.Tests
```

Good first targets: `SourceModuleMapper` (HTML stripping, mapping), the deterministic
MCP tools (`plan_semester`, `plan_path` scheduling), and `KnowledgeCompilationService`
with a fake `ILlmClient`.

## Production Considerations

1. **Security**: the MCP server ships without authentication — put it behind OAuth
   (MCP authorization flow) or a reverse proxy; keep API keys (if any) in secrets
   management.
2. **Freshness**: run `ingest` + `compile` on a schedule (cron, GitHub Actions); the
   server picks up new artifacts automatically via the `manifest.json` timestamp.
3. **Monitoring**: add logging/metrics around bariapi fetch-through latency and
   compile-run reports.
