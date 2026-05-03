# OCREngine API Documentation — Production Specification

Tài liệu đặc tả API production cho OCREngine — Chỉ mô tả input/output.

---

## Thông tin chung

| Thuộc tính | Giá trị |
|-----------|---------|
| **Base URL** | `http://{host}:{port}` (ví dụ: `http://localhost:5258`) |
| **API Prefix** | `/api/ocr` |
| **Content-Type (Request)** | `multipart/form-data` |
| **Content-Type (Response)** | `application/json` |
| **File đầu vào** | PDF (`.pdf`) |
| **Authentication** | Không yêu cầu (production cần bổ sung Bearer token) |

---

## Endpoints

### 1. Submit OCR Task

**Method:** `POST`
**Endpoint:** `/api/ocr/process`
**Content-Type:** `multipart/form-data`

#### Input

| Field | Type | Required | Ràng buộc | Mô tả |
|-------|------|----------|-----------|-------|
| `File` | `IFormFile` | ✅ | File PDF hợp lệ, dung lượng > 0 byte | File tài liệu cần xử lý OCR. Tên file bất kỳ (ASCII/Unicode) |
| `ModelId` | `string` | ✅ | Phải thuộc danh sách supported models | Định danh model OCR sử dụng. Phân biệt chữ hoa/thường, sẽ được trim và lowercase trước khi xử lý |

**Giá trị hợp lệ của `ModelId`:**
- `deepseekocr`
- `chandraocr`

#### Output

**Status: 200 OK**
```json
{
  "taskId": "serverabc-550e8400-e29b-41d4-a716-446655440000",
  "message": "File uploaded and queued."
}
```

| Field | Type | Format | Mô tả |
|-------|------|--------|-------|
| `taskId` | `string` | `{serverName}-{GUID}` | Định danh duy nhất của task. `serverName` = hostname (loại bỏ ký tự không phải alphanumeric, lowercase). Dùng để query kết quả và hủy task |
| `message` | `string` | — | Thông báo xác nhận đã nhận và đưa vào hàng đợi |

**Status: 400 Bad Request**
```
"No file uploaded."
```
hoặc
```
"ModelId 'xxx' is not supported. Supported models: deepseekocr, chandraocr"
```

| Tình huống | Nguyên nhân |
|-----------|-------------|
| `"No file uploaded."` | Field `File` trống hoặc file có `Length = 0` |
| `"ModelId 'xxx' is not supported..."` | `ModelId` không nằm trong danh sách supported models |

**Status: 409 Conflict**
```
"File 'document.pdf' is already being processed or exists in temporary storage."
```

| Tình huống | Nguyên nhân |
|-----------|-------------|
| File cùng tên (`originalFileName`) đã tồn tại trong thư mục tạm hoặc đang được xử lý | Tránh trùng lặp khi client gửi lại request chưa hoàn tất |

**Status: 500 Internal Server Error**
```
"Internal server error during upload."
```

| Tình huống | Nguyên nhân |
|-----------|-------------|
| Lỗi khi lưu file, enqueue job, hoặc exception không xử lý được | File tạm sẽ tự động dọn (cleanup) |

---

### 2. Get Markdown Result

**Method:** `GET`
**Endpoint:** `/api/ocr/get-markdown/{taskId}`

#### Input

| Parameter | Vị trí | Type | Required | Format | Mô tả |
|-----------|--------|------|----------|--------|-------|
| `taskId` | Path | `string` | ✅ | `{serverName}-{GUID}` | Task ID nhận từ response của endpoint `/api/ocr/process` |

#### Output

**Status: 200 OK**
- Content-Type: `application/json`
- Response body: Array of `PageOcrResult`

```json
[
  {
    "pageIndex": 0,
    "markdown": "# Document Title\n\n## Section 1\n\nNội dung văn bản...\n\n![table](table_100_200_500_600.png)",
    "images": {
      "table_100_200_500_600.png": "iVBORw0KGgoAAAANSUhEUgAABkAAAASwCAYAA...",
      "chart_50_50_400_300.jpg": "/9j/4AAQSkZJRgABAQEAYABgAAD..."
    }
  },
  {
    "pageIndex": 1,
    "markdown": "## Page 2\n\nMore content...",
    "images": {}
  }
]
```

**Schema: `PageOcrResult[]`**

| Field | Type | Required | Format | Mô tả |
|-------|------|----------|--------|-------|
| `pageIndex` | `integer` | ✅ | 0-based index | Chỉ số trang trong tài liệu gốc. Trang đầu tiên = 0 |
| `markdown` | `string` | ✅ | Markdown hoặc HTML (UTF-8) | Nội dung văn bản đã nhận dạng. **Có thể chứa reference đến ảnh** dưới dạng key-placeholder. Xem phần **⚠️ Xử lý ảnh trong markdown/HTML** bên dưới |
| `images` | `object` | ✅ | `Dictionary<string, string>` | Dictionary chứa ảnh đã crop/extract từ trang. **Key**: tên file ảnh (format: `{x1}_{y1}_{x2}_{y2}.{ext}` với ext là `png` hoặc `jpg`). **Value**: Base64-encoded string của dữ liệu ảnh (không có data URI prefix). Key này trùng với placeholder trong field `markdown` |

**⚠️ Xử lý ảnh trong markdown/HTML:**

Field `markdown` chứa reference đến ảnh dưới dạng **key-placeholder**. Client **bắt buộc** phải thay thế các key này bằng URL public hoặc local path thực tế để ảnh hiển thị đúng.

**Case 1 — Image Markdown syntax (DeepSeek model):**

Ảnh được nhúng bằng cú pháp Markdown: `![alt](bbox_key)`

```markdown
# Tài liệu

Đoạn văn bản nội dung...

![image/jpeg](120_340_580_720.jpg)

Tiếp tục đoạn văn bản...
```

**Cách xử lý:** Thay thế toàn bộ `bbox_key` trong cú pháp `![alt](bbox_key)` bằng URL/local path thực tế:
```markdown
![image/jpeg](https://cdn.example.com/images/120_340_580_720.jpg)
```
hoặc
```markdown
![image/jpeg](./output/120_340_580_720.jpg)
```

**Case 2 — Image HTML tag (Chandra model):**

Ảnh được nhúng bằng thẻ HTML: `<img src="bbox_key" />`

```html
<h1>Tài liệu</h1>
<p>Đoạn văn bản nội dung...</p>
<img src="120_340_580_720.jpg" />
<p>Tiếp tục đoạn văn bản...</p>
```

**Cách xử lý:** Thay thế toàn bộ `bbox_key` trong attribute `src` của thẻ `<img>` bằng URL/local path thực tế:
```html
<img src="https://cdn.example.com/images/120_340_580_720.jpg" />
```
hoặc
```html
<img src="./output/120_340_580_720.jpg" />
```

**⚠️ Lưu ý quan trọng:**
- Format reference ảnh (Markdown `![]()` hay HTML `<img>`) **không phụ thuộc vào `modelId`** — mà phụ thuộc vào cách model OCR trả về nội dung. Client cần xử lý **cả 2 case** trên để tương thích với mọi model hiện tại và tương lai
- Field `images` chứa base64 data tương ứng với từng `bbox_key`. Client decode base64 → lưu thành file ảnh → thay thế key trong `markdown` bằng URL/path của file đó
- Bbox key format: `{x1}_{y1}_{x2}_{y2}.{ext}` — trong đó x1,y1,x2,y2 là tọa độ pixel của vùng đã crop từ trang, ext là `jpg` hoặc `png`

**⚠️ Lưu ý quan trọng về lifecycle file JSON:**
- File JSON kết quả bị **xóa ngay sau khi HTTP response hoàn tất** (gọi 1 lần duy nhất)
- Client phải lưu trữ kết quả **và xử lý ảnh** ngay khi nhận được response
- Gọi lại endpoint sau khi file đã xóa → `404 Not Found`

**Status: 404 Not Found**
```
"JSON result file not found or task not completed."
```

| Tình huống | Nguyên nhân |
|-----------|-------------|
| Task chưa hoàn thành | Background job vẫn đang xử lý |
| Task không tồn tại | `taskId` sai hoặc chưa từng được tạo |
| File kết quả đã bị xóa | Đã gọi endpoint này trước đó và response đã hoàn tất |

---

### 3. Cancel Task

**Method:** `POST`
**Endpoint:** `/api/ocr/cancel`

#### Input

| Parameter | Vị trí | Type | Required | Format | Mô tả |
|-----------|--------|------|----------|--------|-------|
| `taskId` | Query | `string` | ✅ | `{serverName}-{GUID}` | Task ID cần hủy |

#### Output

**Status: 200 OK — Trường hợp 1: Task đang chạy (Running)**
```json
{
  "message": "Cancellation signal sent for Task serverabc-550e8400-e29b-41d4-a716-446655440000",
  "status": "Running-Canceling",
  "removedFromRedis": true,
  "deletedFromQueue": false
}
```

| Field | Type | Giá trị | Mô tả |
|-------|------|---------|-------|
| `message` | `string` | — | Thông báo xác nhận đã gửi tín hiệu hủy |
| `status` | `string` | `"Running-Canceling"` | Job đang thực thi, đã gửi cancellation signal qua Redis |
| `removedFromRedis` | `boolean` | `true` | Worker đã bị xóa khỏi Redis tracking |
| `deletedFromQueue` | `boolean` | `false` | Job không còn trong queue (đã lấy ra để chạy) |

**Status: 200 OK — Trường hợp 2: Task còn trong queue (Queued)**
```json
{
  "message": "Cancellation signal sent for Task serverabc-550e8400-e29b-41d4-a716-446655440000",
  "status": "Queued-Canceled",
  "removedFromRedis": false,
  "deletedFromQueue": true
}
```

| Field | Type | Giá trị | Mô tả |
|-------|------|---------|-------|
| `status` | `string` | `"Queued-Canceled"` | Job còn trong hàng đợi, đã xóa khỏi Hangfire |
| `removedFromRedis` | `boolean` | `false` | Job chưa chạy nên không có worker trong Redis |
| `deletedFromQueue` | `boolean` | `true` | Job đã bị xóa khỏi Hangfire queue |

**Status: 200 OK — Trường hợp 3: Task đã hoàn thành**
```json
{
  "message": "Task serverabc-550e8400-e29b-41d4-a716-446655440000 may have already completed.",
  "status": "Completed"
}
```

| Field | Type | Giá trị | Mô tả |
|-------|------|---------|-------|
| `message` | `string` | — | Task đã hoàn thành trước khi nhận tín hiệu hủy |
| `status` | `string` | `"Completed"` | Không thể hủy task đã hoàn thành |

**Status: 400 Bad Request**
```
"TaskId is required."
```

| Tình huống | Nguyên nhân |
|-----------|-------------|
| Query parameter `taskId` trống hoặc không truyền | Endpoint yêu cầu `taskId` hợp lệ |

**Status: 404 Not Found**
```json
{
  "message": "Task serverabc-550e8400-e29b-41d4-a716-446655440000 not found. It may have already completed or never existed."
}
```

| Trường hợp | Nguyên nhân |
|-----------|-------------|
| Task đã hoàn thành | Mapping `taskId ↔ jobId` đã bị dọn sau khi job xong |
| Task chưa từng tồn tại | `taskId` sai hoặc chưa được tạo qua `/api/ocr/process` |
| Mapping file bị xóa | File ánh xạ nội bộ bị mất |

---

### 4. Get Supported Models

**Method:** `GET`
**Endpoint:** `/api/ocr/supported-models`

#### Input

Không có parameter.

#### Output

**Status: 200 OK**
```json
["deepseekocr", "chandraocr"]
```

| Type | Format | Mô tả |
|------|--------|-------|
| `string[]` | Array of strings | Danh sách `modelId` hợp lệ, có thể dùng trong endpoint `/api/ocr/process`. Giá trị dynamic theo cấu hình `appsettings.json` (section `LlmModels`) |

---

## Redis Event Stream — Theo dõi tiến độ xử lý (Real-time)

Hệ thống publish events vào **Redis Stream** để client theo dõi tiến độ task bất đồng bộ. **Không có HTTP endpoint** cho việc này — client cần kết nối trực tiếp đến Redis.

### Kết nối

| Thuộc tính | Giá trị |
|-----------|---------|
| **Stream Key** | `ocr:events:stream` |
| **Transport** | Redis Protocol (RESP3) |
| **Library khuyến nghị** | `StackExchange.Redis` (C#), `redis-py` (Python), `ioredis` (Node.js) |

### Redis Command

**Subscribe real-time (blocking):**
```bash
XREAD BLOCK 0 STREAMS ocr:events:stream $
```

**Đọc events mới nhất (non-blocking):**
```bash
XREAD COUNT 100 STREAMS ocr:events:stream $
```

**Đọc toàn bộ history của một task (scan từ đầu):**
```bash
XREAD COUNT 1000 STREAMS ocr:events:stream 0-0
# Sau đó filter client-side theo taskId
```

### Event Model

Mỗi entry trong stream là một object `OcrEvent` serialized JSON:

```json
{
  "taskId": "serverabc-550e8400-e29b-41d4-a716-446655440000",
  "filename": "document.pdf",
  "status": "Processing",
  "eventType": "Logging",
  "message": "Done 3/12 (Page 3) in 4.52s",
  "timestamp": "2026-04-07 14:32:10",
  "dataJson": null,
  "processingTime": 4.52
}
```

| Field | Type | Required | Mô tả |
|-------|------|----------|-------|
| `taskId` | `string` | ✅ | Định danh task. Dùng để filter events thuộc về task cụ thể |
| `filename` | `string` | ✅ | Tên file PDF gốc đang xử lý |
| `status` | `string` (enum) | ✅ | Trạng thái event. Giá trị hợp lệ: `"Started"`, `"Processing"`, `"Succeeded"`, `"Failed"`, `"Canceled"` |
| `eventType` | `string` (enum) | ✅ | Loại event. Giá trị hợp lệ: `"Logging"`, `"SaveLog"`, `"GetMarkdown"` |
| `message` | `string` | ✅ | Thông báo chi tiết. Nội dung thay đổi tùy `status` và `eventType` |
| `timestamp` | `string` | ✅ | Thời gian phát sinh event. Format: `yyyy-MM-dd HH:mm:ss` (múi giờ server) |
| `dataJson` | `string \| null` | ❌ | JSON string chứa dữ liệu bổ sung. Chỉ có giá trị khi `eventType` là `SaveLog` hoặc `GetMarkdown`. Xem bảng **dataJson theo EventType** bên dưới |
| `processingTime` | `number \| null` | ❌ | Thời gian xử lý (giây) của page hiện tại. Chỉ có giá trị khi `status = "Processing"` |

### dataJson theo EventType

| EventType | Status | dataJson content | Ý nghĩa |
|-----------|--------|-----------------|---------|
| `Logging` | `Started` | `null` | Job bắt đầu xử lý |
| `Logging` | `Processing` | `null` | Một page đã hoàn thành. `message` chứa progress: `"Done {current}/{total} (Page {n}) in {time}s"` |
| `Logging` | `Succeeded` | `null` | Tất cả pages đã hoàn thành |
| `SaveLog` | `Succeeded` | `string` (JSON array) | Mảng log nội bộ của toàn bộ job, format: `[{taskId, time, message, status}, ...]` |
| `GetMarkdown` | `Succeeded` | `{"url": "get-markdown/{taskId}"}` | Endpoint URL để tải kết quả JSON |
| — | `Failed` | `null` | Job thất bại. `message` chứa lý do lỗi |
| — | `Canceled` | `null` | Job bị hủy bởi user hoặc hệ thống |

### Lifecycle event điển hình

**Flow thành công:**
```
1. { status: "Started",      eventType: "Logging",   message: "Job Started" }
2. { status: "Processing",   eventType: "Logging",   message: "Done 1/12 (Page 1) in 5.23s", processingTime: 5.23 }
3. { status: "Processing",   eventType: "Logging",   message: "Done 2/12 (Page 2) in 4.81s", processingTime: 4.81 }
   ... (tiếp tục cho từng page) ...
4. { status: "Succeeded",    eventType: "Logging",   message: "OCR Finished successfully", processingTime: 62.5 }
5. { status: "Succeeded",    eventType: "SaveLog",   dataJson: "[{...},{...},...]" }
6. { status: "Succeeded",    eventType: "GetMarkdown", dataJson: "{\"url\":\"get-markdown/serverabc-xxx\"}" }
```

**Flow thất bại:**
```
1. { status: "Started",      eventType: "Logging",   message: "Job Started" }
2. { status: "Processing",   eventType: "Logging",   message: "Done 1/12 (Page 1) in 5.23s", processingTime: 5.23 }
3. { status: "Failed",       eventType: "Logging",   message: "Job Failed: Runaway loop detected: 15000 tokens generated without finishing." }
```

**Flow bị hủy:**
```
1. { status: "Started",      eventType: "Logging",   message: "Job Started" }
2. { status: "Processing",   eventType: "Logging",   message: "Done 3/12 (Page 3) in 4.10s", processingTime: 4.10 }
3. { status: "Canceled",     eventType: "Logging",   message: "Job Canceled" }
```

### Thứ tự EventType trong luồng xử lý

Events luôn xuất hiện theo **trình tự cố định** (dựa trên `ReportEventAsync` trong `OcrBackgroundJob`):

#### Flow thành công — Thứ tự EventType:

```
Logging (Started)
  ↓
Logging (Processing) ← lặp N lần, mỗi lần = 1 page hoàn thành
  ↓
Logging (Succeeded)
  ↓
SaveLog (Succeeded)
  ↓
GetMarkdown (Succeeded) ← EVENT CUỐI CÙNG, file JSON đã sẵn sàng để tải
```

| Bước | EventType | Status | Số lần xuất hiện | Ý nghĩa |
|------|-----------|--------|-----------------|---------|
| 1 | `Logging` | `Started` | 1 lần | Job bắt đầu — PDF đã validate, engine đã resolve |
| 2 | `Logging` | `Processing` | **N lần** (N = tổng số trang) | Mỗi event = 1 page OCR xong. `message`: `"Done {current}/{total} (Page {n}) in {time}s"` |
| 3 | `Logging` | `Succeeded` | 1 lần | Tất cả pages đã OCR xong, JSON đã lưu |
| 4 | `SaveLog` | `Succeeded` | 1 lần | Log summary. `dataJson`: mảng log toàn bộ job |
| 5 | `GetMarkdown` | `Succeeded` | 1 lần | **Tín hiệu cuối cùng**. `dataJson.url`: endpoint tải kết quả. File JSON tồn tại trên server |

#### Flow thất bại — Thứ tự EventType:

```
Logging (Started)
  ↓
Logging (Processing) ← có thể 0 hoặc N lần
  ↓
Logging (Failed) ← EVENT CUỐI CÙNG, job dừng
```

| Bước | EventType | Status | Ý nghĩa |
|------|-----------|--------|---------|
| 1 | `Logging` | `Started` | Job bắt đầu |
| 2 | `Logging` | `Processing` | (Optional) Một số pages đã xử lý trước khi lỗi |
| 3 | `Logging` | `Failed` | **Kết thúc**. `message` chứa lý do lỗi gốc (`ex.GetBaseException().Message`) |

#### Flow bị hủy — Thứ tự EventType:

```
Logging (Started)
  ↓
Logging (Processing) ← có thể 0 hoặc N lần
  ↓
Logging (Canceled) ← EVENT CUỐI CÙNG, job dừng
```

| Bước | EventType | Status | Ý nghĩa |
|------|-----------|--------|---------|
| 1 | `Logging` | `Started` | Job bắt đầu |
| 2 | `Logging` | `Processing` | (Optional) Một số pages đã xử lý trước khi bị cancel |
| 3 | `Logging` | `Canceled` | **Kết thúc**. Job bị hủy qua `/api/ocr/cancel` hoặc shutdown |

### Khi nào gọi `/api/ocr/get-markdown/{taskId}`

Chỉ gọi endpoint này khi nhận được event có **`status = "Succeeded"`** và **`eventType = "GetMarkdown"`**. Lúc này `dataJson.url` chứa đường dẫn để tải kết quả.

```json
// Event "GetMarkdown" — tín hiệu sẵn sàng để tải
{
  "status": "Succeeded",
  "eventType": "GetMarkdown",
  "dataJson": "{\"url\":\"get-markdown/serverabc-550e8400-e29b-41d4-a716-446655440000\"}"
}

// → Gọi ngay: GET /api/ocr/get-markdown/serverabc-550e8400-e29b-41d4-a716-446655440000
```

### ⚠️ Lưu ý quan trọng

- **KHÔNG polling HTTP** — events chỉ có trong Redis Stream. Không có HTTP endpoint để query progress
- **Filter theo `taskId`** — stream chứa events của **tất cả tasks**. Client phải tự filter events theo `taskId` của mình
- **Event `GetMarkdown` là tín hiệu cuối cùng** — chỉ khi nhận được event này thì file JSON kết quả mới tồn tại trên server
- **File JSON tự xóa sau 1 lần download** — gọi `/api/ocr/get-markdown/{taskId}` lần thứ 2 → `404 Not Found`
- **Connection loss** — nếu mất kết nối Redis, client có thể đọc lại từ ID cuối cùng đã thấy: `XREAD STREAMS ocr:events:stream {lastId}`

---

## Models

### OcrSubmitResponse (200 — `/api/ocr/process`)

```json
{
  "taskId": "string",
  "message": "string"
}
```

### PageOcrResult (200 — `/api/ocr/get-markdown/{taskId}`)

```json
{
  "pageIndex": 0,
  "markdown": "string",
  "images": {
    "table_100_200_500_600.png": "string (base64)",
    "chart_50_50_400_300.jpg": "string (base64)"
  }
}
```

### CancelResponse (200 — `/api/ocr/cancel`)

```json
{
  "message": "string",
  "status": "string (Running-Canceling | Queued-Canceled | Completed)",
  "removedFromRedis": true,
  "deletedFromQueue": false
}
```

---

*Last updated: 2026-04-07*
