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
| `search` | Free-text search (ChatGPT-connector-compatible shape) |
| `fetch` | Full compiled record of one module by code (ChatGPT-connector-compatible shape) |
| `search_modules` | Structured filtering: tags, semester, level, ECTS range, language |
| `list_tags` | The closed tag taxonomy with descriptions and module counts |
| `plan_semester` | Eligible vs. blocked modules for a semester, given completed modules |

## Resources

| URI | Content |
|-----|---------|
| `catalog://index` | Compact overview of all modules (markdown table) — small enough to load fully into context |
| `catalog://taxonomy` | Tag vocabulary with descriptions |
| `catalog://module/{code}` | Full compiled record of one module (JSON) |

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

Run the compiler on a schedule (cron, GitHub Actions, scheduled container):

```bash
dotnet run --project StructuredRAG.Compiler -- \
  --Compiler:SourcePath=data/modules.json \
  --Compiler:OutputPath=/var/catalog/compiled \
  --DockerModelRunner:Endpoint=https://<llm-endpoint>/v1 \
  --DockerModelRunner:SimpleModel=<model>
```

The compiler works with any OpenAI-compatible chat-completions endpoint. Because
compilation is offline and infrequent, this is the place to spend on a strong model —
taxonomy quality determines how well the client can search. The compiler writes
`manifest.json` last, so a watching MCP server only reloads complete catalogs.
