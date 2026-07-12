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

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

# The sample catalog is baked in as the default; for a real catalog, mount the
# compiler output and point Catalog__CompiledPath at it:
#   docker run -p 8080:8080 -v $(pwd)/compiled:/app/catalog \
#     -e Catalog__CompiledPath=/app/catalog structured-rag-mcp
COPY compiled-sample /app/compiled-sample
ENV Catalog__CompiledPath=/app/compiled-sample
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "StructuredRAG.Mcp.dll"]
