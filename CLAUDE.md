# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

OCREngine is a .NET 9 microservice that converts PDF/images to high-quality Markdown using AI OCR models. It uses asynchronous processing with Hangfire and Redis for job queuing and concurrency throttling.

## Commands

```powershell
# Build the project
dotnet build OCREngine.csproj

# Run locally (requires Redis running)
dotnet run --project OCREngine.csproj

# Run with Docker (recommended)
docker-compose up -d --build

# Access API docs (Scalar/OpenAPI)
http://localhost:5258/docs

# Access Hangfire dashboard
http://localhost:5258/hangfire
```

## Architecture

### API Layer
- **OcrController** (`Controllers/OcrController.cs`): Main REST endpoints
  - `POST /api/ocr/process` - Upload file, enqueue background job
  - `GET /api/ocr/get-markdown/{taskId}` - Download JSON result
  - `POST /api/ocr/cancel` - Cancel running job
  - `POST /api/ocr/ocr-image` - Synchronous OCR for testing/debugging

### Background Processing
- **OcrBackgroundJob** (`Applications/Jobs/OcrBackgroundJob.cs`): Core OCR pipeline
  - Renders PDF pages to images (300 DPI)
  - Detects image orientation via DocOri service
  - Allocates Redis slots for concurrency control
  - Processes pages in parallel with throttling

### OCR Engines (Keyed Services)
- **IBaseOcrEngine** (`Applications/Interfaces/IBaseOcrEngine.cs`): Interface defining OCR operations
- **BaseOcrEngine**: Abstract base with retry logic, response parsing, image cropping
- **DeepSeekOcrService**: Model-specific implementation

### Concurrency Management
- **IRedisService** (`Applications/Interfaces/IRedisService.cs`): Distributed throttling
  - Uses Redis Lua scripts for atomic slot allocation
  - Key format: `ocr:model:{modelId}:workers`
  - Supports job cancellation via worker removal

### External Services
- **DocOriService** (`Infrastructure/ExternalService/DocOriService.cs`): Orientation detection (runs in local container at `192.168.1.7:8001`)

## Configuration

Configuration in `appsettings.json`:

- `Hangfire.RedisConnection` - Redis connection string
- `LlmModels.DeepSeek` - Model API key, BaseUrl, and concurrency limit
- `ExternalServices.DocOri` - Orientation service URL and batch size

Environment variables are also loaded from `.env` file (via `dotenv.net`).

## Data Flow

1. Client uploads file → OcrController saves temp file, enqueues job to model-specific queue
2. OcrBackgroundJob receives job → Renders PDF to images → Detects orientation
3. Allocates Redis slots → Processes pages in parallel (respecting concurrency limit)
4. Each page: OCR API call → Parse response → Crop images → Convert to Markdown
5. Results saved as JSON → Client polls `GET /api/ocr/get-markdown/{taskId}`

## Key Patterns

- **Keyed Services**: OCR engines are registered as keyed services selected by `LlmSupport` enum
- **Retry Logic**: BaseOcrEngine handles retries for repetition/length limit errors (max 3 attempts)
- **Event Streaming**: Job progress published to Redis Stream `ocr:events:stream`
- **Automatic Cleanup**: Temp files deleted after processing; JSON results deleted after download