# Bài 2 - Webhook + Kafka (dotnet)

## 1) Mục tiêu
Xây dựng 3 service tách vai trò rõ ràng:
- Nhận webhook từ Facebook tại endpoint `/webhook`
- Xác thực request bằng `X-Hub-Signature-256`
- Normalize payload về schema chung
- Publish vào Kafka topic `raw_events`

## 2) Kiến trúc
- `backend-api` (ASP.NET Core, port `3000` when run locally)
- `webhook-service` (ASP.NET Core, port `3001` when run locally)
- `core-service` (ASP.NET Core host, port `3002` when run locally)
- `kafka` + `zookeeper` qua Docker Compose
- `kafka-ui` để quan sát topic và message

Luồng xử lý:
1. Facebook gọi `POST /webhook`
2. Service kiểm tra chữ ký HMAC SHA256
3. Parse payload và normalize (`comment`, `message`)
4. Publish từng event vào topic `raw_events`

## 3) Chạy hệ thống
Từ thư mục gốc project:

Project su dung file `.env` lam nguon cau hinh chinh cho Facebook/Kafka.
Ban can copy `.env.example` thanh `.env` va dien gia tri that truoc khi chay.

Nếu PowerShell báo `docker is not recognized`, chạy 1 lần:

```powershell
$dockerBin = "C:\Program Files\Docker\Docker\resources\bin"
$env:Path = "$env:Path;$dockerBin"
```

Sau đó mở terminal mới (để nhận PATH vĩnh viễn) rồi chạy:

```powershell
docker compose up --build -d
```

Dịch vụ sau khi chạy:
- Backend API: `http://localhost:3000`
- Webhook service: `http://localhost:3001`
- Core service: `http://localhost:3002`
- Kafka UI: `http://localhost:8080`

## 4) Cấu hình Facebook Webhook
### Verify endpoint
Facebook sẽ gọi `GET /webhook` với query:
- `hub.mode=subscribe`
- `hub.verify_token=...`
- `hub.challenge=...`

Service trả về `hub.challenge` nếu token khớp `Facebook:VerifyToken`.

### Cấu hình secret/token
Trong môi trường local (compose) đang dùng mặc định:
- `Facebook__AppSecret=dev-facebook-app-secret`
- `Facebook__VerifyToken=dev-webhook-verify-token`

Khi chạy thật, thay bằng App Secret/Verify Token thực tế.

## 5) Kết hợp Meta Developer vào project
### 5.1 Tạo app trên Meta for Developers
1. Vào Meta for Developers và tạo app loại `Business`.
2. Thêm product `Webhooks`.
3. Trong app, lấy `App ID` và `App Secret`.

### 5.2 Chuẩn bị callback URL cho local
Meta yêu cầu callback URL public HTTPS.
Bạn có thể dùng `ngrok` để expose local:

```powershell
ngrok http 3002
```

Dùng URL ngrok dạng `https://xxxxx.ngrok-free.app/webhook` làm Callback URL.

### 5.3 Cấu hình Webhooks product
1. Chọn object `Page`.
2. Callback URL: URL ngrok + `/webhook`.
3. Verify Token: đặt trùng với `Facebook__VerifyToken`.
4. Subscribe fields: chọn tối thiểu `feed` (comment) và nếu cần inbox thì thêm `messages`.

### 5.4 Lấy Page Access Token
1. Dùng Graph API Explorer hoặc flow OAuth của Meta để lấy Page Access Token.
2. Token cần quyền phù hợp, thường gồm:
  - `pages_manage_metadata`
  - `pages_read_engagement`
  - `pages_messaging` (nếu dùng message event)
3. Lấy thêm `Page ID` từ page của bạn.

### 5.5 Nạp cấu hình Meta vào service
Set biến môi trường cho `backend-api`:

```powershell
$env:Facebook__AppId = "your_app_id"
$env:Facebook__AppSecret = "your_app_secret"
$env:Facebook__VerifyToken = "your_verify_token"
$env:Facebook__PageId = "your_page_id"
$env:Facebook__PageAccessToken = "your_page_access_token"
$env:Facebook__GraphApiVersion = "v22.0"
$env:Facebook__SubscribedFields = "feed,messages"
```

### 5.6 Dùng endpoint tích hợp Meta đã có trong project
- Kiểm tra kết nối token/page:

```powershell
curl "http://localhost:3000/api/facebook/page"
```

- Đăng ký app vào Page subscription (tự động gọi Graph API):

```powershell
curl -X POST "http://localhost:3000/api/facebook/page/subscriptions"
```

- Override field khi cần:

```powershell
curl -X POST "http://localhost:3000/api/facebook/page/subscriptions?fields=feed,messages"
```

Luu y: 2 endpoint `/meta/*` hien tai la endpoint ho tro dev. Khi deploy production, nen bao ve bang authentication va gioi han IP.

## 6) Schema normalize (topic raw_events)
Mỗi message trong Kafka có dạng:

```json
{
  "eventId": "string",
  "source": "facebook",
  "eventType": "comment | message | unknown",
  "pageId": "string | null",
  "actorId": "string | null",
  "objectId": "string | null",
  "content": "string | null",
  "occurredAt": "2026-04-25T11:01:00+00:00",
  "rawPayload": "{...json...}"
}
```

## 7) Test nhanh endpoint (mô phỏng)
### 7.1 Verify token (GET)

```powershell
curl "http://localhost:3001/webhook?hub.mode=subscribe&hub.verify_token=dev-webhook-verify-token&hub.challenge=12345"
```

Kết quả mong đợi: trả về `12345`.

### 7.2 Gửi comment event (POST)
1. Tạo payload file `sample-comment.json`:

```json
{
  "object": "page",
  "entry": [
    {
      "id": "123456789",
      "time": 1714030000000,
      "changes": [
        {
          "field": "feed",
          "value": {
            "item": "comment",
            "comment_id": "cmt_1001",
            "post_id": "post_88",
            "from": { "id": "user_77" },
            "message": "hello from webhook",
            "created_time": 1714030000000
          }
        }
      ]
    }
  ]
}
```

2. Tính chữ ký HMAC SHA256 (PowerShell):

```powershell
$secret = "dev-facebook-app-secret"
$payload = Get-Content .\sample-comment.json -Raw
$hmac = [System.Security.Cryptography.HMACSHA256]::new([Text.Encoding]::UTF8.GetBytes($secret))
$hash = $hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes($payload))
$sig = "sha256=" + ([System.BitConverter]::ToString($hash).Replace("-", "").ToLower())
```

3. Gửi request:

```powershell
curl -X POST "http://localhost:3001/webhook" `
  -H "Content-Type: application/json" `
  -H "X-Hub-Signature-256: $sig" `
  --data-binary "@sample-comment.json"
```

Kết quả mong đợi: JSON phản hồi có `accepted=true` và `published > 0`.

## 8) Kiểm tra message trên Kafka UI
1. Mở `http://localhost:8080`
2. Vào topic `raw_events`
3. Quan sát message đã được normalize.

## 9) Mapping yêu cầu đề bài
- `backend-api` chạy port `3000` khi chạy local bằng `dotnet run`.
- `webhook-service` chạy port `3001` khi chạy local bằng `dotnet run`.
- `core-service` có health endpoint ở port `3002`.
- Endpoint nhận event Facebook: đã có (`GET/POST /webhook`)
- Xác thực request: đã có (`X-Hub-Signature-256`)
- Parse + normalize schema chuẩn: đã có (`comment`, `message`)
- Đẩy vào topic Kafka `raw_events`: đã có (Kafka Producer)
- Có Kafka Compose + Kafka UI: đã có (`docker-compose.yml`)

## 10) Chuyển sang dùng OpenAI API cho `core-service`
### 10.1 Biến môi trường cần thêm
`core-service` hiện đọc cấu hình OpenAI từ nhóm biến sau:

```powershell
$env:OpenAI__ApiKey = "your_openai_api_key"
$env:OpenAI__Model = "gpt-4o-mini"
$env:OpenAI__BaseUrl = "https://api.openai.com/v1"
$env:OpenAI__MaxAttempts = "2"
```

Gợi ý:
- `gpt-4o-mini` là lựa chọn phù hợp để phân loại intent/sentiment/spam.
- Nếu bạn muốn model mạnh hơn, đổi `OpenAI__Model` theo model bạn có quyền dùng.

### 10.2 Cách chạy local sau khi đổi provider
1. Thêm các biến OpenAI vào file `.env` ở thư mục gốc.
2. Xóa hoặc bỏ qua `Gemini:ApiKey` nếu không dùng nữa.
3. Chạy lại hệ thống:

```powershell
docker compose up -d zookeeper kafka backend-api webhook-service core-service
dotnet run --project core-service
```

### 10.3 Kết quả mong đợi
- `core-service` sẽ gọi OpenAI để trả về JSON gồm `isSpam`, `spamReason`, `intent`, `sentiment`, `riskLevel`, `requiresManualReview`, `shouldHide`.
- Nếu OpenAI trả lỗi `401/403`, service sẽ tự rơi về fallback nội bộ để không làm đứt pipeline.

### 10.4 Test nhanh provider OpenAI
Khi đã cấu hình xong, gửi một event test vào Kafka hoặc tạo comment thật trên Page và quan sát log `core-service`.

Nếu log không còn hiện lỗi Gemini mà chỉ còn message xử lý bình thường, nghĩa là bạn đã chuyển provider thành công.