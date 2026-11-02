# Thư mục Runtime (Libraries & Tools)

Đặt các thư viện và công cụ thực thi đi kèm ứng dụng vào thư mục **`Runtime`** này:

- `ffmpeg.exe` và `ffprobe.exe` (có thể đặt trực tiếp tại `Runtime\` hoặc `Runtime\bin\`)
- Các thư viện `.dll` (Managed .NET DLL hoặc Native C/C++ DLL) hỗ trợ giải mã, xử lý âm thanh, hình ảnh hoặc AI.

Khi khởi động, **LangKinhHuKhong.Editer** sẽ tự động:
1. Quét và nạp các thư viện `.dll` (cả Managed lẫn Native) từ thư mục `Runtime\`.
2. Thêm `Runtime\` và `Runtime\bin\` vào biến môi trường `PATH` nội bộ của ứng dụng.
3. Ưu tiên tìm `ffmpeg.exe` và `ffprobe.exe` trong thư mục `Runtime\` trước khi tìm trên hệ thống.
