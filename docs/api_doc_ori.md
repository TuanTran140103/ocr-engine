# Document Orientation API Documentation

Tài liệu này cung cấp hướng dẫn chi tiết về cách tích hợp và sử dụng API nhận diện góc xoay tài liệu.

---

## 1. Tổng quan
API sử dụng mô hình **PP-LCNet_x1_0_doc_ori** từ bộ thư viện PaddleOCR để nhận diện góc xoay của ảnh tài liệu. Các góc xoay được hỗ trợ bao gồm: `0`, `90`, `180`, `270` độ.

- **Dịch vụ:** FastAPI
- **Model:** PaddleOCR (DocImgOrientationClassification)
- **Cơ chế xử lý:** Hỗ trợ Streaming và Batch Processing.

---

## 2. Endpoints

### 2.1 Dự đoán đơn lẻ (Single Image)
Dùng khi cần xử lý nhanh từng ảnh một.

- **URL:** `/predict`
- **Method:** `POST`
- **Content-Type:** `multipart/form-data`

| Tham số | Loại | Bắt buộc | Mô tả |
| :--- | :--- | :--- | :--- |
| `file` | `UploadFile` | Có | Dữ liệu nhị phân của ảnh (JPEG, PNG, ...). |

**Ví dụ Request (curl):**
```bash
curl -X POST "http://localhost:8000/predict" \
     -H "Content-Type: multipart/form-data" \
     -F "file=@/path/to/your/image.jpg"
```

**Kết quả trả về (JSON):**
```json
{
  "status": "success",
  "filename": "image.jpg",
  "orientation": "0",
  "confidence": 0.9982
}
```

---

### 2.2 Dự đoán theo đợt (Batch Processing)
Dùng khi cần xử lý số lượng lớn ảnh để tiết kiệm tài nguyên và tăng throughput (thông lượng). Mô hình sẽ chạy Inference đồng thời trên nhiều ảnh (mặc định `batch_size=4`).

- **URL:** `/predict-batch`
- **Method:** `POST`
- **Content-Type:** `multipart/form-data`

| Tham số | Loại | Bắt buộc | Mô tả |
| :--- | :--- | :--- | :--- |
| `files` | `List[UploadFile]` | Có | Danh sách nhiều file ảnh (gửi nhiều field cùng tên `files`). |

**Ví dụ Request (curl):**
```bash
curl -X POST "http://localhost:8000/predict-batch" \
     -H "Content-Type: multipart/form-data" \
     -F "files=@img1.jpg" \
     -F "files=@img2.jpg"
```

**Kết quả trả về (JSON):**
```json
{
  "status": "success",
  "predictions": [
    {
      "filename": "img1.jpg",
      "orientation": "0",
      "confidence": 0.99
    },
    {
      "filename": "img2.jpg",
      "orientation": "180",
      "confidence": 0.95
    }
  ],
  "processed": 2,
  "total_uploaded": 2
}
```

---

## 3. Hướng dẫn tích hợp cho các Service khác

### 3.1 Tối ưu hóa Streaming
API được thiết kế để nhận dữ liệu theo dạng stream giúp giảm thiểu việc tiêu tốn RAM. Đối với các service gọi đến, **không nên** nạp file vào memory rồi mới gửi, thay vào đó hãy pipe stream trực tiếp.

#### Ý nghĩa các trường dữ liệu:
- **`file.filename`:** Không bắt buộc phải truyền chính xác. Trường này chủ yếu dùng để phản hồi lại trong JSON để client phân biệt kết quả của các file khác nhau (đặc biệt trong chế độ batch). Nếu không truyền, API vẫn giải mã (decode) được ảnh dựa trên nội dung bytes.

### 3.2 Ví dụ bằng Python (Dùng thư viện `requests`)
```python
import requests

url = "http://localhost:8000/predict"
path = "test.jpg"

with open(path, "rb") as f:
    files = {"file": (path, f, "image/jpeg")}
    response = requests.post(url, files=files)
    print(response.json())
```

### 3.3 Ví dụ bằng C# (.NET HttpClient)
```csharp
using var client = new HttpClient();
using var content = new MultipartFormDataContent();
using var fileStream = File.OpenRead("document.png");
using var streamContent = new StreamContent(fileStream);

content.Add(streamContent, "file", "document.png");
var response = await client.PostAsync("http://localhost:8000/predict", content);
var result = await response.Content.ReadAsStringAsync();
Console.WriteLine(result);
```

---

## 4. Error Handling
Các mã lỗi thường gặp:

- **400 Bad Request:**
    - `Cannot decode image`: Dữ liệu gửi lên không phải là ảnh hợp lệ hoặc bị hỏng.
- **422 Unprocessable Entity:**
    - Thiếu field `file` hoặc `files` trong request.
- **500 Internal Server Error:**
    - Lỗi phát sinh trong quá trình model Inference hoặc lỗi hệ thống.

---

## 5. Lưu ý về Hiệu suất
- Mô hình chạy trên **CPU** mặc định với số luồng được cấu hình tối ưu (thường là 4 threads).
- Khi xử lý Batch, nên giới hạn số lượng ảnh mỗi lần gửi (khoảng 10-20 ảnh) để tránh hiện tượng timeout request nếu phần cứng hạn chế.
- `confidence` (độ tin cậy) là giá trị từ 0 đến 1. Thông thường, các dự đoán đúng sẽ có độ tin cậy > 0.9.
