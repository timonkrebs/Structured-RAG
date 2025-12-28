# Development Guide

## Architecture Overview

### Components

1. **SQL Server Database**
   - Stores entities and their tags
   - Uses EF Core for ORM
   - Connection managed via Entity Framework

2. **Docker Model Runner (Gemma 3)**
   - Provides LLM capabilities
   - Runs as a Docker service (Model Runner)

3. **.NET Application**
   - Console application orchestrating the RAG pipeline
   - Contains services for tag generation and RAG queries
   - Runs migrations and seeds data on startup

### Data Flow

```
1. Application starts
   ↓
2. Database initialization (EF Core)
   ↓
3. Sample data seeding
   ↓
4. Tag generation for entities
   ↓
5. RAG query processing demo
```

## Project Structure

```
StructuredRAG.Api/
├── Models/               # Domain models
│   ├── Entity.cs        # Main entity with content
│   └── Tag.cs           # Tag for RAG optimization
├── Data/                # Database context
│   └── ApplicationDbContext.cs
├── Services/            # Business logic
│   ├── DockerModelRunnerService.cs      # LLM communication
│   ├── TagGenerationService.cs # Tag generation logic
│   └── RagQueryService.cs      # RAG query processing
├── Program.cs           # Entry point and configuration
└── appsettings.json     # Configuration settings
```

## Key Services

### DockerModelRunnerService

Handles communication with the Gemma LLM via Docker Model Runner.

**Key Methods:**
- `GenerateAsync(string prompt)`: Sends a prompt to the LLM and returns the response

### TagGenerationService

Manages tag generation for entities.

**Key Methods:**
- `GenerateTagsForEntityAsync(int entityId)`: Generates tags for a specific entity
- `GenerateTagsForAllEntitiesAsync()`: Generates tags for all untagged entities

**Tag Generation Logic:**
1. Retrieves entity content
2. Fetches existing tags from the database
3. Constructs a prompt for the LLM including existing tags
4. Parses LLM response to extract tags
5. Saves new tags to the database

### RagQueryService

Processes RAG queries using tag-based filtering.

**Key Methods:**
- `ProcessQueryAsync(string userQuery)`: Main entry point for RAG queries

**RAG Query Pipeline:**
1. Fetches all available tags
2. Uses LLM to select relevant tags based on user query
3. Filters entities matching selected tags
4. Generates response using filtered entities as context

## Configuration

### Database Connection

Update `appsettings.json`:

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=sqlserver;Database=StructuredRAG;..."
  }
}
```

Or use environment variables in docker-compose.yml:

```yaml
environment:
  - ConnectionStrings__DefaultConnection=Server=...
```

### LLM Endpoint

Configure the Docker Model Runner endpoint:

```json
{
  "DockerModelRunner": {
    "Endpoint": "http://model-runner.docker.internal/engines/llama.cpp/v1"
  }
}
```

## Development Workflow

### Local Development

1. **Start dependencies:**
   ```bash
   docker compose up sqlserver
   ```
   (Note: LLM is handled by Docker Model Runner if configured in Docker Desktop, or you may need to ensure the environment is set up correctly).

2. **Run application locally:**
   ```bash
   cd StructuredRAG.Api
   dotnet run
   ```

### Full Docker Development

```bash
docker compose up --build
```

### Debugging

1. **Check SQL Server:**
   ```bash
   docker compose logs sqlserver
   ```

2. **Check Application:**
   ```bash
   docker compose logs app
   ```

## Extending the Solution

### Adding New Entity Types

1. Create a new model in `Models/`
2. Add DbSet to `ApplicationDbContext`
3. Update database schema (migrations or EnsureCreated)

### Customizing Tag Generation

Modify `TagGenerationService.BuildTagGenerationPrompt()` to:
- Change number of tags generated
- Add domain-specific instructions
- Adjust tag format

### Implementing Vector Search

To add vector search capabilities:

1. Add a vector embedding package (e.g., Microsoft.ML.OnnxRuntime)
2. Generate embeddings for entity content
3. Store embeddings in database
4. Use filtered entities + vector similarity in RAG query

### Custom RAG Logic

Modify `RagQueryService.ProcessQueryAsync()` to:
- Change tag selection criteria
- Adjust entity filtering logic
- Customize response generation

## Testing

### Manual Testing

1. **Verify Database:**
   - Connect to SQL Server
   - Check `Entities` and `Tags` tables

2. **Test LLM:**
   (If accessible from host)
   ```bash
   curl http://localhost:PORT/engines/llama.cpp/v1 -d '{...}'
   ```

3. **Monitor Logs:**
   - Watch application output for tag generation
   - Verify RAG query results

### Unit Testing

To add unit tests:

1. Create test project:
   ```bash
   dotnet new xunit -n StructuredRAG.Tests
   ```

2. Add test packages:
   ```bash
   dotnet add package Moq
   dotnet add package Microsoft.EntityFrameworkCore.InMemory
   ```

3. Write tests for services using in-memory database

## Performance Considerations

- **LLM Latency**: Model responses can take 5-30 seconds
- **Database Connection**: Use connection pooling (enabled by default)
- **Tag Caching**: Consider caching all tags in memory
- **Batch Processing**: Process multiple entities in parallel

## Troubleshooting

### Application doesn't start

- Check Docker resources (8GB+ RAM recommended)
- Verify all ports are available (1433)
- Check Docker logs for each service

### LLM timeouts

- Increase timeout in DockerModelRunnerService
- Use a smaller model
- Reduce prompt length

### Database connection issues

- Verify SQL Server is healthy
- Check connection string
- Ensure trust server certificate is enabled

## Production Considerations

Before deploying to production:

1. **Security:**
   - Use secrets management for passwords
   - Enable SSL/TLS for database connections
   - Implement authentication/authorization

2. **Performance:**
   - Use a dedicated SQL Server instance
   - Implement caching strategy

3. **Monitoring:**
   - Add application insights/logging
   - Monitor LLM response times
   - Track database performance

4. **Scalability:**
   - Use managed database service
   - Deploy LLM separately with load balancing
   - Consider async/queue-based processing
