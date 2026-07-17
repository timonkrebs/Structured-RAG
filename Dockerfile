# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy solution and project files first so `dotnet restore` is layer-cached
COPY StructuredRAG.sln .
COPY StructuredRAG.Core/StructuredRAG.Core.csproj StructuredRAG.Core/
COPY StructuredRAG.Fhnw/StructuredRAG.Fhnw.csproj StructuredRAG.Fhnw/
COPY StructuredRAG.Compiler/StructuredRAG.Compiler.csproj StructuredRAG.Compiler/
COPY StructuredRAG.Mcp/StructuredRAG.Mcp.csproj StructuredRAG.Mcp/
RUN dotnet restore "StructuredRAG.sln"

COPY . .
RUN dotnet publish StructuredRAG.Mcp/StructuredRAG.Mcp.csproj -c Release -o /app/publish /p:UseAppHost=false

# Bake the newest catalog in the build context: the real compiled catalog once it
# has been committed (see .github/workflows/refresh-catalog.yml), the sample until
# then — so the build never breaks on a repo that hasn't compiled yet.
RUN if [ -f compiled/manifest.json ]; then cp -r compiled /catalog; else cp -r compiled-sample /catalog; fi

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

# The catalog selected in the build stage (real compiled/ if committed, sample
# otherwise). To serve another catalog without rebuilding, mount it over the path:
#   docker run -p 8080:8080 -v $(pwd)/compiled:/app/catalog \
#     -e Catalog__CompiledPath=/app/catalog structured-rag-mcp
COPY --from=build /catalog /app/catalog
ENV Catalog__CompiledPath=/app/catalog
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "StructuredRAG.Mcp.dll"]
