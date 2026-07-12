---
name: verify
description: Build, run and drive the module-catalog MCP server and its widgets to verify changes end-to-end
---

# Verifying Structured-RAG changes

## Server (MCP surface)

- This codespace has only .NET 9/10 runtimes; the net8.0 apps need `DOTNET_ROLL_FORWARD=LatestMajor` to run (builds are unaffected).
- Run: `DOTNET_ROLL_FORWARD=LatestMajor ASPNETCORE_URLS=http://localhost:5599 dotnet run --project StructuredRAG.Mcp` (add `Catalog__CompiledPath=<dir>` to serve a different catalog; default is `../compiled-sample`).
- `GET /` returns health + catalog manifest. JSON-RPC via `POST /mcp` with header `Accept: application/json, text/event-stream`; responses come as SSE (`data:` lines). The server is stateless — `tools/call` works without an initialize handshake.
- Gotcha: never `pkill -f StructuredRAG.Mcp` — the pattern matches your own shell's command line and kills it. Keep the PID from launch and `kill` that.

## Widgets (GUI surface)

- `Widgets/*.html` are **embedded resources** — rebuild StructuredRAG.Mcp and restart the server after editing, then fetch what is actually served via `resources/read` (`ui://module-catalog/semester-planner` etc.).
- Headless browser: `npm i playwright-core` in a scratch dir; the chromium headless shell is already cached in `~/.cache/ms-playwright`.
- OpenAI host mode: `page.addInitScript` defining `window.openai = { theme, locale, toolOutput, callTool, setWidgetState, sendFollowUpMessage }` **before** `page.goto`, with `toolOutput` = real `structuredContent` from a `tools/call`.
- MCP Apps mode: parent page with the widget in an iframe, answering `ui/initialize` and pushing `ui/notifications/tool-result` over postMessage; assert on `ui/update-model-context` requests for draft persistence.
- Useful sample-catalog fixtures: `stat` + `pmgt` are a Friday time-clash pair in 26HS, `webec` has two parallel classes in 27FS (class picker), `cysl` has no published times (degradation path).

## Ingest (offline pipeline)

- `dotnet run --project StructuredRAG.Compiler -- ingest --Ingest:RawCacheTtlHours=876000` re-maps from the `data/raw/` cache without refetching details (search/facet calls still hit the live API).
- Post-checks on `data/modules.wirtschaftsinformatik.json`: no dummy dates (`1899-12-30` / `1900-01-01`) and no emails (`@fhnw.ch`) may appear.
