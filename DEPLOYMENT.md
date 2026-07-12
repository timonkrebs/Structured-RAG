# Deployment Guide

Free options for getting the MCP server onto a public HTTPS URL so hosted clients
(ChatGPT, Claude) can reach it.

The server is easy to host: a plain ASP.NET Core 8 container (see `Dockerfile`) that
listens on port 8080, ships with the sample catalog baked in, and makes **no LLM calls
at query time** — no GPU, no API keys, minimal CPU/RAM. Any free container host works.

Once deployed, register `https://<your-host>/mcp` in ChatGPT or Claude as described in
the [README](README.md#using-it-from-chatgpt-or-claude).

## Option 1 — GitHub Codespace public port (fastest, zero setup)

If you develop in a Codespace, you already have a public HTTPS endpoint — no deploy
needed. Run the server bound to all interfaces:

```bash
ASPNETCORE_URLS=http://0.0.0.0:8080 dotnet run --project StructuredRAG.Mcp
```

Then make the forwarded port public (or use the *Ports* panel in VS Code):

```bash
gh codespace ports visibility 8080:public -c $CODESPACE_NAME
```

The MCP endpoint is `https://$CODESPACE_NAME-8080.app.github.dev/mcp`.

Best for live testing sessions: the URL stops working when the Codespace stops, and
changes if the Codespace is recreated. Not a standing deployment.

## Option 2 — Render.com free tier (easiest standing deployment)

1. [render.com](https://render.com) → *New* → *Web Service* → connect this GitHub repo.
2. Runtime: **Docker** — Render builds the existing `Dockerfile` as-is (no config
   needed; the free instance type is fine).
3. Every push to the connected branch auto-redeploys.

The service gets a stable `https://<name>.onrender.com` URL. Free instances spin down
after ~15 minutes idle; the first request afterwards cold-starts in ~30–60 s.

## Option 3 — Google Cloud Run / Azure Container Apps (free tiers, more setup)

Both have perpetual free grants (millions of requests per month), scale to zero, and
build straight from the repo. They require a cloud account — GCP needs a card on file;
for Azure, students get [Azure for Students](https://azure.microsoft.com/free/students/)
credits with no credit card.

Google Cloud Run:

```bash
gcloud run deploy structured-rag-mcp --source . --port 8080 --allow-unauthenticated
```

Azure Container Apps:

```bash
az containerapp up --name structured-rag-mcp --source . --ingress external --target-port 8080
```

## Option 4 — Hugging Face Spaces (Docker Space)

Create a [Docker Space](https://huggingface.co/new-space), push this repo to it, and
declare the port in the Space's `README.md` front matter:

```yaml
sdk: docker
app_port: 8080
```

Free CPU tier; the Space sleeps after ~48 h of inactivity and wakes on request.

## Compiling the real catalog for a deployment

Compilation never runs on the deploy host — it is the LLM-heavy offline step and
needs LLM access (a ChatGPT login for codex-cli, or an OpenAI-compatible API key),
which the serving layer deliberately doesn't have. The deployed server only reads
the static JSON artifacts baked into the image. The loop:

1. **Compile where you have LLM access** (Codespace, laptop):

   ```bash
   codex login    # once; reuses your ChatGPT plan, no API key
   dotnet run --project StructuredRAG.Compiler -- all --Llm:Provider=codex-cli
   ```

   `ingest` pulls the FHNW catalog into `data/`, `compile` writes
   `compiled/{manifest,taxonomy,modules}.json`. Re-runs are incremental: modules
   whose source is unchanged reuse the previous LLM enrichments (`sourceHash`),
   so a weekly refresh costs only the changed modules.

2. **Commit the artifacts** — `compiled/` is not gitignored, on purpose: the
   compiled catalog is a build input of the image, and versioning it makes every
   deploy reproducible.

3. **Point the image at them** — add to the `Dockerfile` (after the existing
   `compiled-sample` lines, so the sample stays the fallback):

   ```dockerfile
   COPY compiled /app/catalog
   ENV Catalog__CompiledPath=/app/catalog
   ```

   (Alternatively keep the Dockerfile untouched and set `Catalog__CompiledPath`
   as an environment variable in the Render dashboard.)

4. **Push** — Render (option 2) rebuilds and redeploys automatically; on Cloud
   Run / Container Apps re-run the deploy command.

To automate the daily/weekly rhythm, run steps 1–2 in a scheduled GitHub Actions
workflow and let the push trigger the redeploy. Note that `codex login` is
interactive, so CI should use the OpenAI-compatible provider instead
(`--Llm:Provider=openai` plus endpoint/key from repository secrets).

## Notes

- **Cold starts**: on scale-to-zero hosts (options 2–4) the first MCP request after
  idle can time out in the client — retry once and the instance is warm.
- **Statelessness**: the server is stateless over the compiled artifacts, so
  scale-to-zero and instance restarts are safe. It hot-reloads a changed
  `manifest.json` at runtime, but on image-based hosts a new catalog arrives as a
  new image anyway (see above).
