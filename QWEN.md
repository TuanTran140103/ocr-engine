# OCREngine Project Context

## Project Overview

**OCREngine** là một Microservice OCR (Optical Character Recognition) xây dựng trên **.NET 9**, chuyên chuyển đổi tài liệu (PDF, Hình ảnh) sang định dạng Markdown chất lượng cao. Hệ thống hỗ trợ xử lý bất đồng bộ qua hàng đợi, tích hợp nhiều mô hình AI OCR và cơ chế điều phối tài nguyên thông minh.

### Kiến Trúc Chính

1. **API Layer** (`Controllers/`): Tiếp nhận yêu cầu upload file, lưu trữ tạm và đẩy vào hàng đợi
2. **Background Processing** (`Applications/Jobs/`): Xử lý OCR bất đồng bộ qua Hangfire + Redis
3. **Concurrency Management** (`Infrastructure/`): Redis throttling để tránh rate limit API bên thứ 3
4. **OCR Pipeline**: Render PDF → Resize → Auto-Orientation → OCR → Markdown

### Multi-model Support

| Model | Queue Name | Concurrency |
|-------|-----------|-------------|
| DeepSeek OCR | `deepseekocr` | 20 |

---

## Project Structure

```
D:\Project\net_core\webAPI\
├── OCREngine/              # Main API Project
│   ├── Applications/       # Business logic, Interfaces, Background Jobs
│   ├── Controllers/        # API Endpoints (OcrController, OrientationController)
│   ├── Infrastructure/     # External services (Redis, OCR Engines, Lua Scripts)
│   ├── Models/             # Data Models, DTOs, Enums
│   ├── Helpers/ & Utils/   # Utility classes
│   ├── Program.cs          # Application entry point & DI configuration
│   ├── appsettings.json    # Configuration (Redis, LLM models, External services)
│   ├── Dockerfile          # Multi-stage Alpine build (~100MB runtime)
│   └── docker-compose.yml  # Docker orchestration (app + doc-ori service)
│
└── OcrEngine.Test/         # Unit Test Project (xUnit)
    └── TestData/
        └── ocr_result.json
```

---

## Building and Running

### Prerequisites

- **.NET 9 SDK**
- **Redis Server** (đang chạy)
- **Docker & Docker Compose** (nếu chạy container)

### Chạy bằng Docker (Khuyên dùng)

```powershell
# Từ thư mục OCREngine
docker-compose up -d --build
```

- API: `http://localhost:5258`
- Docs (Scalar): `http://localhost:5258/docs`
- Hangfire Dashboard: `http://localhost:5258/hangfire`

### Chạy Local (Development)

```powershell
# Từ thư mục OCREngine
dotnet run --project OCREngine.csproj

# Hoặc mở solution trong Visual Studio/Rider
```

### Chạy Unit Tests

```powershell
# Từ thư mục OcrEngine.Test
dotnet test

# Chạy với coverage
dotnet test --collect:"XPlat Code Coverage"
```

---

## Key Configuration (appsettings.json)

### Hangfire + Redis
```json
"Hangfire": {
  "RedisConnection": "192.168.1.9:6379,abortConnect=false,password=MySecurePassword123",
  "DashboardPath": "/hangfire",
  "WorkerCount": 2
}
```

### LLM Models
```json
"LlmModels": {
  "DeepSeek": {
    "ModelName": "deepseek-ocr2",
    "ApiKey": "no-ApiKey",
    "BaseUrl": "https://...",
    "Concurrency": 20
  }
}
```

### External Services
```json
"ExternalServices": {
  "DocOri": {
    "BaseUrl": "http://192.168.1.9:8001",
    "BatchSize": 5
  }
}
```

---

## API Endpoints

| Method | Endpoint | Description |
|--------|----------|-------------|
| `POST` | `/api/ocr/process` | Upload file (PDF/Image) để OCR |
| `POST` | `/api/ocr/cancel?taskId=xxx` | Hủy job đang xử lý |
| `GET` | `/api/ocr/get-markdown/{taskId}` | Tải kết quả Markdown |
| `DELETE` | `/api/ocr/clear-streams` | Xóa Redis streams (debug) |

### Example: Upload File

```bash
curl -X POST http://localhost:5258/api/ocr/process \
  -F "file=@document.pdf" \
  -F "modelId=deepseekocr"
```

Response:
```json
{ "taskId": "server-abc-123", "message": "File uploaded and processing started." }
```

---

## Development Conventions

### Code Style

- **Nullable Reference Types**: Bật (`<Nullable>enable</Nullable>`)
- **Implicit Usings**: Bật (`<ImplicitUsings>enable</ImplicitUsings>`)
- **Logging**: Sử dụng `Serilog` với structured logging
- **Dependency Injection**: Sử dụng keyed services cho OCR providers

### Testing Practices

- **Framework**: xUnit
- **Mocking**: Moq
- **Test Data**: Lưu trong `TestData/` với `CopyToOutputDirectory`
- **Naming**: `ClassNameTests.cs`, method theo pattern `Method_Scenario_ExpectedBehavior`

### Background Job Queues

Mỗi model OCR có queue riêng trong Hangfire:
- Worker được cấu hình với `options.Queues = new[] { queue }`
- Server name format: `OCREngine-WORKER-{MachineName}-{queue}`

---

## Key Technologies

| Category | Technology |
|----------|------------|
| Framework | .NET 9 (ASP.NET Core Web API) |
| Background Jobs | Hangfire + Redis Storage |
| Image Processing | SkiaSharp, SixLabors.ImageSharp, PDFtoImage |
| OCR API | Azure AI Inference SDK |
| Logging | Serilog (Console + File JSON) |
| API Docs | Scalar (OpenAPI 3.1) |
| Testing | xUnit, Moq |
| Container | Docker (Alpine Linux) |

---

## Troubleshooting

### Redis Connection Issues
- Kiểm tra Redis đang chạy: `redis-cli ping` → `PONG`
- Update `RedisConnection` trong `appsettings.json` hoặc biến môi trường

### SkiaSharp trên Alpine
- Dockerfile đã install đủ dependencies: `icu-libs`, `fontconfig`, `freetype`, `libpng`, `libjpeg-turbo`
- Set `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false`

### Worker Not Processing Jobs
- Kiểm tra Hangfire Dashboard tại `/hangfire`
- Verify queue name khớp giữa `Enqueue` và `AddHangfireServer`
- Check Redis keys: `redis-cli KEYS "ocrengine:hangfire:*"`

---

## Related Files

- **Program.cs**: DI configuration, Hangfire setup, CORS, Serilog
- **OcrController.cs**: API endpoints cho upload/cancel/download
- **OcrBackgroundJob.cs**: Background processing logic
- **appsettings.json**: Cấu hình Redis, LLM models, External services
- **docker-compose.yml**: Orchestration cho OCR app + DocOri service
