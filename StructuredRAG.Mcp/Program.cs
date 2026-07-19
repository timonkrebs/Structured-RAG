using ModelContextProtocol.Server;
using StructuredRAG.Fhnw;
using StructuredRAG.Mcp;
using StructuredRAG.Mcp.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<CatalogStore>();
builder.Services.AddHttpClient<BariApiClient>(client => client.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddSingleton<LiveModuleFetcher>();

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new() { Name = "module-catalog", Version = "1.0.0" };
    })
    // Stateless streamable HTTP: no session affinity needed, which keeps the server
    // trivially scalable and compatible with ChatGPT / claude.ai remote connectors.
    .WithHttpTransport(o => o.Stateless = true)
    .WithToolsFromAssembly()
    .WithResourcesFromAssembly();

// The initialize instructions embed a snapshot of the compiled catalog (size, tag
// vocabulary with counts) so every client can map interests onto tags without a first
// round-trip. PostConfigure gives DI access to the store; the resulting options value
// is cached after first use — a hot-reloaded catalog refreshes tool results and
// resources, but these instructions only refresh on process restart.
builder.Services.AddOptions<McpServerOptions>().PostConfigure<CatalogStore>((options, store) =>
    options.ServerInstructions = $"""
        FHNW study module catalog for students (bilingual German/English; student
        questions are usually German). All catalog data is precompiled — you (the
        client model) do the reasoning.

        PERSONA: you are talking to ONE student about THEIR OWN studies — address
        them directly ("du"/"you"), never ask about "the students" in the third
        person. BIAS TO ACTION: when interests, prior experience or constraints are
        unknown, do NOT run a questionnaire — neither in chat nor via a host-side
        ask-user-input mechanism. Make a sensible proposal from the catalog
        (mandatory/foundational modules first), state your assumptions in
        one line, and let the student correct you. Ask at most ONE short clarifying
        question, and only when the answer would genuinely change the plan; the
        plan-builder widget exists precisely so students adjust a concrete proposal
        instead of answering questions upfront.

        Recommended flow:
        0. Nothing known about the student yet (greeting, app just added, vague
           ask)? Call get_started FIRST, before any other call or question — its
           widget collects completed modules and the ECTS target and starts the
           next flow as a chat message.
        1. Map the student's interests onto the tag vocabulary in the catalog snapshot
           below. Tag descriptions and the full module list are one call away
           (list_tags, get_catalog_overview) or attachable as resources
           (catalog://taxonomy, catalog://index). When in doubt, get_catalog_overview
           is full recall: every module in one call.
        2. Use search_modules to find modules: boolean tag filters (allOfTags /
           anyOfTags / noneOfTags) plus semester, level, module type, study program,
           ECTS range, language and free text. RECALL OVER PRECISION: missing a
           relevant module is far worse than reviewing extra ones, and the compact
           default format makes wide result sets cheap. Tags are compiled by an LLM
           and approximate — a relevant module may lack the tag you expect — so put
           every plausibly relevant tag into anyOfTags and shortlist yourself from
           the compact rows; reserve allOfTags/noneOfTags for intersections or
           exclusions the student explicitly asked for. Deterministic facts
           (semester, level, ECTS, language, module type) are exact and safe to
           filter hard on. Only narrow further when 'total' is unmanageable, and
           use includeFacets=true to pick the narrowing tag from real counts
           instead of guessing. 'total' always counts all matches before the
           'limit' cut; formats: 'compact' (default), 'full', 'codes'. Cross-check
           with the free-text search tool when a tag sweep looks thin — it scans
           summaries, typical student questions and descriptions, not tags.
        3. Use fetch for full details of a module — it returns the CURRENT official
           catalog description (live from the FHNW module directory) plus compiled
           enrichments; check metadata.source ('live' vs 'compiled').
        4. For semester planning call plan_semester with the student's completed modules
           and the concrete semester (e.g. '26HS'). Combine eligible modules into a plan
           that fits the ECTS target and interests — and mind each module's
           prerequisiteNotes (requirements that are free text, not module codes).
           Structured prerequisites come as groups of interchangeable alternatives
           (language variants of the same course): any ONE member satisfies its group.
           Eligible modules carry lesson time slots where published (lessons: day,
           start-end, one entry per weekly slot; slots sharing the same number form
           one parallel class — the student attends ONE class); use them to propose
           a clash-free weekly timetable.
           When the student asks how to reach a specific module ("when can I take X?"),
           call plan_path with the target code and a concrete start semester.
        5. In widget-capable hosts (ChatGPT via the OpenAI Apps SDK; Claude and others
           via the MCP Apps extension), plan_semester, compare_modules and plan_path
           additionally render interactive widgets (semester plan builder / comparison
           table / path timeline); other clients simply use the structured JSON results.
        The compiled catalog has a compilation date (see manifest / catalog info); for
        authoritative current details always rely on fetch results and the official URL.

        {store.InstructionsSnapshot()}
        """);

var app = builder.Build();

app.MapMcp("/mcp");

// Lightweight health/info endpoint for load balancers and humans.
app.MapGet("/", (CatalogStore store) => Results.Ok(new
{
    name = "StructuredRAG module catalog MCP server",
    endpoint = "/mcp",
    catalog = store.Manifest
}));

app.Run();
