# LangKinhHuKhong.Editer v4.5 Pro — Multi-Core Video & AI Subtitle Studio

**Windows 10/11 x64 · C# .NET 8 · FFmpeg + FFprobe**

Bản **v4.5 Pro** nâng cấp toàn diện **Chạy ngầm siêu nhẹ dưới System Tray (ẩn khỏi Taskbar)**, **Tối ưu tốc độ Đa nhân CPU / GPU (NVIDIA NVENC · Intel QuickSync QSV · AMD AMF)**, **Bảo vệ chống đầy ổ đĩa & Cân đối Bitrate thông minh cho video dài 10h–35h+**, và **Nhận diện giọng nói AI 1-lượt PCM siêu tốc (MP3 / Video ➔ .SRT)**.

## Điểm mới nổi bật ở bản v4.5 Pro
- **Chạy ngầm siêu nhẹ dưới khay hệ thống (System Tray / Toolbar góc phải)**:
  - Tự động ẩn khỏi thanh **Taskbar** ngay khi bắt đầu Xuất Video hoặc Tạo `.SRT` (hoặc bấm nút `⬇ ẨN KHỎI TASKBAR` / nút thu nhỏ `_`).
  - Tự động giải phóng bề mặt vẽ GDI/DWM và thu gọn vùng nhớ vật lý (`SetProcessWorkingSetSize`), giúp tiến trình giao diện chiếm `0%` CPU/GPU trong suốt thời gian render nhiều giờ.
  - Rê chuột vào biểu tượng góc phải để xem `%` tiến độ thời gian thực; nhấp chuột trái để mở lại cửa sổ hoặc chuột phải để mở thư mục xuất / hủy tác vụ.
- **Tăng tốc phần cứng đa nền tảng GPU & Single-Pass trực tiếp**:
  - Tự động dò và kích hoạt tuần tự: **NVIDIA NVENC HQ** ➔ **Intel QuickSync (`h264_qsv`)** ➔ **AMD Radeon AMF (`h264_amf`)** ➔ **H.264 Đa luồng CPU (`libx264`)**.
  - Chế độ **Direct Single-Pass** nối trực tiếp danh sách video gốc (hỗ trợ cả video đổi độ phân giải giữa chừng), không sinh hàng chục GB tệp tạm `clips/`.
- **Chống lỗi đầy ổ đĩa (`FFmpeg -28: No space left on device`)**:
  - Tự động lưu cấu hình giao diện & ưu tiên ổ đĩa có dung lượng trống lớn nhất, hiển thị dung lượng trống trực tiếp theo thời gian thực.
  - Kiểm tra dung lượng ngay từ giây đầu tiên và tự động tính trần `-maxrate` phù hợp cho các bộ truyện audio dài **10–35+ giờ**.
- **Speech-to-Text (`MP3 / Video ➔ .SRT`) thế hệ mới**:
  - Giải mã `PCM 16kHz` kết hợp quét `silencedetect` chỉ trong **1 lượt FFmpeg duy nhất**, tách phân đoạn tại điểm năng lượng thấp nhất (`micro-pause`) không mất chữ cuối câu, kèm chế độ dự phòng `whisper-cli` offline.

## Hai màn hình làm việc độc lập

### 1. Màn hình `🎬 Render Video`
- Ghép nhiều video nguồn thành 1 danh sách phát (chỉnh tốc độ riêng cho từng video: `0.4×`, `0.5×`, `0.75×`, `1×`, `1.5×`, `2×`)
- Ghép toàn bộ MP3 thành 1 luồng âm thanh liên tục (xử lý song song đa luồng CPU)
- Tự động lặp danh sách video theo đúng tổng thời lượng audio MP3
- Chèn 0–4 ảnh PNG/JPG/WebP ở 4 góc với kích thước & tọa độ độc lập (chỉ giải mã và scale Lanczos 1 lần cho mỗi ảnh tĩnh)
- Xem trước bố cục `16:9` thời gian thực (cache trực tiếp trên RAM, không đọc đĩa lặp lại)
- Chèn cứng (Burn-in) hoặc xuất kèm tệp phụ đề `.SRT` chuẩn UTF-8
- Hỗ trợ mã hóa **H.264 Đa luồng CPU** và **NVIDIA NVENC GPU** (tự động kiểm tra và fallback về CPU an toàn nếu máy không có GPU NVIDIA)
- Xuất ra **1 tệp MP4 duy nhất** (`720p`, `1080p`, `4K`)

### 2. Màn hình `📝 Create .SRT` (Ngoài luồng)
- **Luồng 1 (MP3/Audio ➔ .SRT)**: Chọn 1 tệp `.mp3` / `.wav` / `.m4a` ➔ Quét sóng âm thực tế (`silencedetect`) & nhận diện giọng nói AI đa luồng ➔ Xuất 1 tệp `.srt` khớp thời gian thực.
- **Luồng 2 (Video ➔ MP3 ➔ .SRT)**: Chọn 1 tệp Video (`.mp4`, `.mkv`, `.mov`, `.avi`, `.webm`,...) ➔ Tự động tách ra tệp `.mp3` chuẩn (`192kbps, 48kHz`) khớp thời gian thực ➔ Phân tích & xuất 1 tệp `.srt` khớp thời gian thực.
- Hỗ trợ 3 chế độ:
  1. **Nhận diện giọng nói AI (Speech-to-Text)**: Hỗ trợ Tiếng Việt (`vi-VN`), Tiếng Anh (`en-US`), Trung (`zh-CN`), Nhật (`ja-JP`), Hàn (`ko-KR`).
  2. **Khớp kịch bản văn bản vào sóng âm thực**: Dán văn bản có sẵn để tự căn mốc `Start --> End` theo đúng giọng đọc thực tế.
  3. **Tách khung thời gian theo nhịp nói**: Tạo nhanh khung mốc thời gian chuẩn theo khoảng nghỉ giọng đọc.
- Trình biên tập `.SRT` trực tiếp trên màn hình và nút **`➕ ĐƯA SANG RENDER VIDEO`** chỉ với 1 cú nhấp chuột.

## Build / Publish

```bat
dotnet clean
dotnet build -c Release
publish-win-x64.bat
```

Sau khi publish, tệp thực thi nằm tại `publish\win-x64\LangKinhHuKhong.Editer.exe` (cần có `ffmpeg.exe` và `ffprobe.exe` cạnh tệp `.exe` hoặc trong biến môi trường `PATH` của Windows).
