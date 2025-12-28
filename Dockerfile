# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy csproj and restore dependencies
COPY StructuredRAG.Api/StructuredRAG.Api.csproj StructuredRAG.Api/
RUN dotnet restore "StructuredRAG.Api/StructuredRAG.Api.csproj"

# Copy everything else and build
COPY StructuredRAG.Api/ StructuredRAG.Api/
WORKDIR /src/StructuredRAG.Api
RUN dotnet build "StructuredRAG.Api.csproj" -c Release -o /app/build

# Publish stage
FROM build AS publish
RUN dotnet publish "StructuredRAG.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "StructuredRAG.Api.dll"]
