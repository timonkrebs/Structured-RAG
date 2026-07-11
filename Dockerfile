# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy solution and project files (all projects referenced by the solution,
# otherwise `dotnet restore` of the solution fails)
COPY StructuredRAG.sln .
COPY StructuredRAG.Core/StructuredRAG.Core.csproj StructuredRAG.Core/
COPY StructuredRAG.Example/StructuredRAG.Example.csproj StructuredRAG.Example/
COPY StructuredRAG.Fhnw/StructuredRAG.Fhnw.csproj StructuredRAG.Fhnw/
COPY StructuredRAG.Compiler/StructuredRAG.Compiler.csproj StructuredRAG.Compiler/
COPY StructuredRAG.Mcp/StructuredRAG.Mcp.csproj StructuredRAG.Mcp/

# Restore dependencies
RUN dotnet restore "StructuredRAG.sln"

# Copy the rest of the source code
COPY . .

# Build the example project
WORKDIR /src/StructuredRAG.Example
RUN dotnet build "StructuredRAG.Example.csproj" -c Release -o /app/build

# Publish stage
FROM build AS publish
RUN dotnet publish "StructuredRAG.Example.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "StructuredRAG.Example.dll"]
