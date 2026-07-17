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

The recommended standing deployment: a stable HTTPS URL, automatic redeploys on
every push, and zero required configuration on top of the existing `Dockerfile`.

### Create the service

1. Sign in at [render.com](https://render.com) (GitHub login) and authorize access
   to this repository.
2. *New* → *Web Service* → pick the repo. Render detects the `Dockerfile` and sets
   **Language: Docker** — leave it; there is nothing to configure for build or
   start commands.
3. Settings that matter:
   - **Name** — becomes the URL: `https://<name>.onrender.com`.
   - **Branch** — `main`. This is the branch whose pushes redeploy, and the one
     catalog-refresh PRs merge into.
   - **Region** — pick *Frankfurt (EU Central)* for FHNW-local latency.
   - **Instance type** — *Free*.
4. Under *Advanced*, set **Health Check Path** to `/` — the server's health/info
   endpoint (`Program.cs` maps it) returns the catalog manifest as JSON, so Render
   only routes traffic once a complete catalog is loaded, and zero-downtime deploys
   replace instances cleanly.
5. *Deploy Web Service*. The first build takes ~5 minutes (image build + publish);
   watch it under *Logs*.

No environment variables are required: the image binds port 8080
(`ASPNETCORE_URLS` in the `Dockerfile`) and Render auto-detects the bound port.
If detection ever misfires, add an env var `PORT=8080` in the dashboard to pin the
routing. Optional overrides (dashboard → *Environment*): `Catalog__CompiledPath`
to point at a mounted catalog, `BariApi__*` for the live-fetch client.

### Verify

```bash
curl https://<name>.onrender.com/          # health: name, endpoint, catalog manifest
```

Check that `catalog.moduleCount` matches what you expect (10 = the sample catalog;
the real catalog only ships once a compiled `compiled/` directory is committed —
see the refresh workflow below). Then register `https://<name>.onrender.com/mcp`
in ChatGPT or Claude as described in the [README](README.md#using-it-from-chatgpt-or-claude).

### How updates reach the service

- **Auto-deploy is on by default**: every push to the connected branch rebuilds
  the image and redeploys. Merging a *Catalog refresh* PR (see below) is therefore
  all it takes to ship a new catalog.
- **Manual deploys**: dashboard → *Manual Deploy* → *Deploy latest commit*, or
  *Clear build cache & deploy* when a build behaves oddly.
- Auto-deploy can be turned off per service (*Settings → Build & Deploy*) if you
  ever want to batch merges without redeploying.

### Free-tier limits and troubleshooting

- **Spin-down**: free instances sleep after ~15 minutes idle; the next request
  cold-starts the container in ~30–60 s. MCP clients may time out on that first
  request — retrying once is enough, the instance stays warm afterwards.
- **Budgets**: the free plan includes ~750 instance-hours and ~500 build minutes
  per month per workspace — one always-on service plus a handful of catalog
  redeploys fits comfortably.
- **Build fails on a fresh fork**: it shouldn't — the `Dockerfile` falls back to
  `compiled-sample/` when no real `compiled/` catalog is committed yet. If a build
  fails, the *Logs* tab shows the failing Docker step directly.
- **Wrong catalog served**: confirm what `/` reports; if it says 10 modules you
  are still on the sample — merge the catalog-refresh PR and let it redeploy.

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

3. **Push** — the `Dockerfile` bakes `compiled/` into the image automatically as
   soon as it exists in the repo (it falls back to `compiled-sample/` until then).
   Render (option 2) rebuilds and redeploys on every push; on Cloud Run /
   Container Apps re-run the deploy command.

## Automated refresh (GitHub Actions)

`.github/workflows/refresh-catalog.yml` runs the whole loop on demand: ingest,
compile, validate (module/tag count gates), and open a **pull request** with the
updated `compiled/` artifacts and ingested source. Merging that PR is what
triggers the Render redeploy. `codex login` is interactive, so the workflow uses
the OpenAI-compatible provider (`--Llm:Provider=openai`); any OpenAI-compatible
endpoint works, e.g. an OpenCode Zen key from [opencode.ai/auth](https://opencode.ai/auth).

Configure once under *Settings → Secrets and variables → Actions*:

| Kind     | Name           | Example                          |
| -------- | -------------- | -------------------------------- |
| Secret   | `LLM_API_KEY`  | OpenCode Zen API key             |
| Variable | `LLM_ENDPOINT` | `https://opencode.ai/zen/go/v1`  |
| Variable | `LLM_MODEL`    | `deepseek-v4-pro`                |

Use the exact base URL shown alongside your key at
[opencode.ai/auth](https://opencode.ai/auth) — subscription plans (e.g. *go*) use a
plan-scoped path, pay-as-you-go keys use `https://opencode.ai/zen/v1`. Model ids are
plain (`deepseek-v4-pro`), without the `opencode/` prefix used in opencode's own config.

Then trigger it from *Actions → Refresh catalog → Run workflow* (or
`gh workflow run refresh-catalog.yml`). The `force` input recompiles every module
from scratch instead of reusing unchanged ones. Runs whose catalog content is
unchanged (only the `compiledAt` timestamp moved) end without opening a PR. For a
recurring rhythm, uncomment the `schedule:` block in the workflow — the PR flow
stays the same, so nothing deploys without review.

## Notes

- **Cold starts**: on scale-to-zero hosts (options 2–4) the first MCP request after
  idle can time out in the client — retry once and the instance is warm.
- **Statelessness**: the server is stateless over the compiled artifacts, so
  scale-to-zero and instance restarts are safe. It hot-reloads a changed
  `manifest.json` at runtime, but on image-based hosts a new catalog arrives as a
  new image anyway (see above).
