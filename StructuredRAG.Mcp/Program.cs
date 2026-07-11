using StructuredRAG.Mcp;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<CatalogStore>();

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new() { Name = "module-catalog", Version = "1.0.0" };
        options.ServerInstructions = """
            Study module catalog for students. All data is precompiled — you (the client
            model) do the reasoning. Recommended flow:
            1. Read the catalog://taxonomy resource (or call list_tags) to see the tag
               vocabulary, then map the student's interests onto tags.
            2. Use search_modules (structured filters) or search (free text) to find modules.
            3. Use fetch for full details of a specific module.
            4. For semester planning call plan_semester with the student's completed modules,
               then combine eligible modules into a plan that fits their ECTS target and interests.
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
