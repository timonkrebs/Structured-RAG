# Structured-RAG
Metadata-Enriched-RAG with auto-generated tags for improved query generation

## Overview

This project implements a containerized .NET solution that uses **Docker Model Runner** (running Gemma 3) to generate optimized tags for entities stored in a SQL Server database. The tags are designed for Retrieval-Augmented Generation (RAG) workflows, where the LLM analyzes user queries, selects relevant tags, and filters entities before performing vector search.

## Features

- **Automated Tag Generation**: Uses LLM (Docker Model Runner) to generate relevant tags for entities
- **Tag Consistency**: Considers existing tags to maintain consistency across the system
- **RAG Query Processing**: LLM-driven tag selection for optimal entity retrieval
- **Containerized Architecture**: Fully containerized with Docker Compose
- **EF Core with SQL Server**: Entity Framework Core for database management

## Architecture

The solution consists of three main components:

1. **SQL Server**: Stores entities and their generated tags
2. **Docker Model Runner**: Provides LLM capabilities for tag generation and query processing
3. **.NET Application**: Orchestrates tag generation and RAG query processing

## Prerequisites

- Docker Desktop or Docker Engine with Docker Compose
- At least 8GB RAM available for containers
- 10GB free disk space (for model storage)

## Getting Started

### 1. Clone the Repository

```bash
git clone https://github.com/timonkrebs/Structured-RAG.git
cd Structured-RAG
```

### 2. Start the Services

```bash
docker-compose up --build
```

This will:
- Start SQL Server
- Build and run the .NET application
- Seed sample data
- Generate tags for entities
- Run a demo RAG query

### 3. Monitor the Logs

Watch the application logs to see:
- Database initialization
- Sample data seeding
- Tag generation progress
- RAG query demonstration

## Project Structure

```
Structured-RAG/
├── StructuredRAG.Api/
│   ├── Models/
│   │   ├── Entity.cs              # Entity model
│   │   └── Tag.cs                 # Tag model
│   ├── Data/
│   │   └── ApplicationDbContext.cs # EF Core DbContext
│   ├── Services/
│   │   ├── DockerModelRunnerService.cs     # LLM interaction service
│   │   ├── TagGenerationService.cs # Tag generation logic
│   │   └── RagQueryService.cs     # RAG query processing
│   ├── Program.cs                  # Application entry point
│   └── appsettings.json           # Configuration
├── Dockerfile                      # Application container
├── docker-compose.yml              # Multi-container orchestration
└── README.md
```

## How It Works

### Tag Generation Process

1. **Load Entities**: Retrieves entities from SQL Server using EF Core
2. **Get Existing Tags**: Fetches all existing tags to maintain consistency
3. **Generate Tags**: Uses Docker Model Runner (Gemma 3) to generate 3-7 optimized tags per entity
4. **Store Tags**: Saves new tags to the database

### RAG Query Process

1. **Receive Query**: User submits a natural language query
2. **Load Available Tags**: Retrieves all tags from the database
3. **Select Relevant Tags**: LLM analyzes query and selects relevant tags
4. **Filter Entities**: Queries entities that match the selected tags
5. **Generate Response**: LLM generates answer based on filtered entities

## Configuration

### Database Connection

Edit `appsettings.json` or set environment variable:

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=sqlserver;Database=StructuredRAG;User Id=sa;Password=YourStrong@Passw0rd;TrustServerCertificate=True"
  }
}
```

### Docker Model Runner Endpoint

Edit `appsettings.json` or set environment variable:

```json
{
  "DockerModelRunner": {
    "Endpoint": "http://model-runner.docker.internal/engines/llama.cpp/v1"
  }
}
```

## Customization

### Adding New Entities

Modify `Program.cs` to add your own entities:

```csharp
var entities = new[]
{
    new Entity
    {
        Name = "Your Entity Name",
        Content = "Your entity content...",
        CreatedAt = DateTime.UtcNow
    }
};
```

### Adjusting Tag Generation

Modify `TagGenerationService.cs` to customize:
- Number of tags generated
- Tag generation prompt
- Tag selection criteria

### Changing the Model

Edit `docker-compose.yml` to use a different model in the `models` section:

```yaml
models:
  llm:
    model: ai/gemma3:2b
```

Update `DockerModelRunnerService.cs`:

```csharp
var request = new
{
    model = "ai/gemma3:2b",  // Change model name
    // ...
};
```

## Stopping the Application

```bash
docker-compose down
```

To remove volumes (data will be lost):

```bash
docker-compose down -v
```

## Troubleshooting

### Container Startup Issues

If containers fail to start, ensure:
- Docker has enough resources (8GB RAM minimum)
- Port 1433 is available
- No other SQL Server instances are running

### Database Connection Issues

Verify SQL Server is healthy:

```bash
docker-compose ps
docker-compose logs sqlserver
```

## License

See LICENSE file for details.

## Contributing

Contributions are welcome! Please open an issue or submit a pull request.
