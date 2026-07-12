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
        options.ServerInstructions = """
            FHNW study module catalog for students (bilingual German/English; student
            questions are usually German). All catalog data is precompiled — you (the
            client model) do the reasoning. Recommended flow:
            1. Call get_catalog_overview (or read the catalog://taxonomy and catalog://index
               resources) to load the tag vocabulary and module list, then map the student's
               interests onto tags yourself.
            2. Use search_modules (structured filters) or search (free text) to find modules.
            3. Use fetch for full details of a module — it returns the CURRENT official
               catalog description (live from the FHNW module directory) plus compiled
               enrichments; check metadata.source ('live' vs 'compiled').
            4. For semester planning call plan_semester with the student's completed modules
               and the concrete semester (e.g. '26HS'). Combine eligible modules into a plan
               that fits the ECTS target, interests, weekday constraints — and mind each
               module's prerequisiteNotes (requirements that are free text, not module codes).
            5. In widget-capable hosts (ChatGPT via the OpenAI Apps SDK; Claude and others
               via the MCP Apps extension), plan_semester and compare_modules additionally
               render interactive widgets (semester plan builder / comparison table);
               other clients simply use the structured JSON results.
            The compiled catalog has a compilation date (see manifest / catalog info); for
            authoritative current details always rely on fetch results and the official URL.
            """;
    })
    // Stateless streamable HTTP: no session affinity needed, which keeps the server
    // trivially scalable and compatible with ChatGPT / claude.ai remote connectors.
    .WithHttpTransport(o => o.Stateless = true)
    .WithToolsFromAssembly()
    .WithResourcesFromAssembly();

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
