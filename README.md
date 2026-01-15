# ProgramMonitoringApp - Takako OPC Monitoring

Ứng dụng giám sát các tiến trình OPC Data Collection tại Takako, đảm bảo hệ thống hoạt động liên tục bằng cách tự động Restart khi phát hiện treo hoặc mất tín hiệu.

## Tính năng chính

- **Giám sát trạng thái tiến trình**: Tự động phát hiện nếu ứng dụng bị đóng hoặc không phản hồi (Not Responding).
- **Giám sát tín hiệu MongoDB**: Theo dõi trường `LastSignal` (Unix Timestamp hoặc DateTime) trong MongoDB để đảm bảo dữ liệu đang được đẩy lên đều đặn.
- **Cơ chế tự phục hồi (Auto-Recovery)**: Tự động Kill và Khởi động lại các tiến trình bị lỗi.
- **Grace Period (Thời gian chờ)**: Sau khi restart hoặc khi bắt đầu Monitor, ứng dụng sẽ đợi **2 phút** để tiến trình ổn định trước khi bắt đầu kiểm tra tín hiệu.
- **Thông báo đa kênh**: Gửi cảnh báo qua **Discord** và **Telegram** ngay khi có sự cố.
- **Smart Logging**: 
  - Màn hình Console: Hiển thị tiếng Việt **không dấu** để tránh lỗi font hệ thống.
  - Discord/Telegram: Hiển thị tiếng Việt **có dấu** đầy đủ.

## Cấu hình hệ thống

### 1. File `config.json` (Cấu hình chung)
- `IdleWaitingMillis`: Thời gian Monitor tạm nghỉ sau khi ra lệnh mở một app (mặc định 5000ms).
- `LoopIntervalSec`: Vòng lặp quét kiểm tra (khuyên dùng 60 giây).
- `Notify`: Cấu hình Webhook Discord và Bot Telegram.

### 2. File `appsettings.json` (Danh sách ứng dụng cần theo dõi)
Mỗi ứng dụng trong `TargetProcesses` bao gồm:
- `Path`: Đường dẫn đến file `.exe`.
- `WindowTitle`: Tên cửa sổ để kiểm tra trạng thái Not Responding.
- `ThresholdMinutes`: Thời gian tối đa cho phép mất tín hiệu trên MongoDB (ví dụ: 5 phút).
- `MongoFilterJson`: Bộ lọc MongoDB để xác định đúng máy/dây chuyền cần giám sát.
- `LastSignalField`: Tên trường chứa thời gian tín hiệu (thường là `LastSignal`).

## Hướng dẫn vận hành

### Cách Build/Publish (cho Windows x86)
Sử dụng lệnh sau để đóng gói ứng dụng:
```powershell
dotnet publish -c Release -r win-x86 --self-contained true
```
Sản phẩm sau khi build sẽ nằm trong thư mục: `ProgramMonitoringApp\bin\Release\net9.0\win-x86\publish\`

### Quy tắc hoạt động (Rules)
1. **Kiểm tra Tiến trình**: Ưu tiên kiểm tra bằng `WindowTitle`. Nếu không thấy cửa sổ, Monitor sẽ dùng PID để kiểm tra.
2. **Kiểm tra Tín hiệu**: Chỉ kiểm tra MongoDB nếu tiến trình đang chạy và không bị treo.
3. **Quyết định Restart**: Nếu (Treo) HOẶC (Mất tín hiệu > Threshold) HOẶC (Ứng dụng không chạy) -> Thực hiện Restart.
4. **Bỏ qua kiểm tra**: Sau khi Restart, Monitor sẽ không kiểm tra App đó trong 2 phút để tránh việc loop restart khi app chưa kịp đẩy dữ liệu mới.

---
*Phát triển bởi Đội ngũ IOT - Viet Solution*
