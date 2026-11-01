# LangKinhHuKhong.Editer v4.2 Pro — Multi-Core Video & AI Subtitle Studio

**Windows 10/11 x64 · C# .NET 8 · FFmpeg + FFprobe**

Bản **v4.2 Pro** nâng cấp toàn diện **Giao diện Pro Studio Dark Slate**, **Tối ưu tốc độ Đa nhân CPU / GPU & RAM**, và bổ sung **Màn hình độc lập Create .SRT (MP3 / Video ➔ .SRT khớp thời gian thực)**.

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
