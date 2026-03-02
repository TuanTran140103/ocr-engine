# OCREngine Service Integration Documentation

This document describes how to integrate with the OCREngine service, specifically its asynchronous processing model and real-time event streaming.

## 1. Overview
OCREngine handles long-running OCR tasks using various LLM models. It uses Hangfire for background jobs and Redis Streams for real-time progress updates.

## 2. API Endpoints

### Submit OCR Task
`POST /api/Ocr/process`
- **Content-Type**: `multipart/form-data`
- **Parameters**:
  - `File`: The PDF or Image file.
  - `ModelId`: `dots`, `chandra`, or `deepseekocr`.
- **Response**:
  ```json
  {
    "taskId": "server-guid",
    "message": "File uploaded and processing started."
  }
  ```

### Get Markdown Result
`GET /api/Ocr/get-markdown/{taskId}`
- **Description**: Downloads the final OCR result in Markdown format.
- **Note**: The file is deleted from the server immediately after download.

### Cancel Task
`POST /api/Ocr/cancel?taskId={taskId}`
- **Description**: Signals the background job to stop processing.

## 3. Event Stream (Redis)
All processing updates are published to a Redis Stream at key `ocr:stream:{taskId}`.

### OcrEvent Schema
| Field | Type | Description |
| :--- | :--- | :--- |
| `taskId` | string | Unique identifier for the task. |
| `status` | enum | Current status of the task (see below). |
| `eventType` | enum | Type of event (see below). |
| `message` | string | Human-readable progress message. |
| `filename` | string | Name of the file being processed. |
| `timestamp` | string | ISO format or `yyyy-MM-dd HH:mm:ss`. |
| `dataJson` | string | JSON string containing type-specific data (see details). |
| `processingTime` | number? | For `Processing` status: time taken for current page. For `Successed` status: total job duration (seconds). |

### EventStatus Meanings
- `Started`: Job has been picked up by a worker and is initializing.
- `Processing`: Job is actively performing OCR or image processing.
- `Successed`: Task completed successfully. Final results are available.
- `Failed`: Task failed due to an error.
- `Canceled`: Task was manually terminated by a user request.

### DataJson Structure by EventType
The `dataJson` field contains different schemas depending on the `eventType`:

#### 1. `Logging`
- **When**: Triggered for every progress milestone.
- **DataJson**: Usually `null` or empty. The progress info is in the `message` field.

#### 2. `SaveLog`
- **When**: Triggered once when the job finishes (Success/Fail/Cancel).
- **DataJson**: A JSON array containing the full execution history.
  ```json
  [
    {
      "taskId": "string",
      "time": "yyyy-MM-dd HH:mm:ss",
      "message": "string",
      "status": "string"
    }
  ]
  ```

#### 3. `GetMarkdown`
- **When**: Triggered precisely when the output file is ready for download.
- **DataJson**: A JSON object containing the relative download URL.
  ```json
  { "url": "get-markdown/{taskId}" }
  ```

## 4. Concurrency & Queues
OCREngine isolates model traffic into dedicated Hangfire queues. Each model has its own concurrency limit.

| ModelId | Hangfire Queue | Description |
| :--- | :--- | :--- |
| `dots` | `dots` | Standard layout-active OCR. |
| `chandra` | `chandra` | High-accuracy document parsing. |
| `deepseekocr` | `deepseekocr` | Advanced multi-view OCR. |

Jobs for one model will not block jobs for another, even if Hangfire workers are saturated.
