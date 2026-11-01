# LangKinhHuKhong.Editer v4.1 — UI Refresh

**Windows 10/11 x64 · C# .NET 8 · FFmpeg + ffprobe**

Bản **v4.1** tập trung vào việc **giảm độ sáng**, **thiết kế lại bố cục hiển thị**, **tối ưu text giao diện** và **sửa logo ở header**.

## Thay đổi chính

- Tông màu giảm sáng: nền kem trầm, trắng ngà, đỏ đậm và vàng ấm
- Header được bố trí lại để logo hiển thị rõ hơn, không bị nhỏ/cắt
- Cập nhật text giao diện theo hướng ngắn gọn, dễ hiểu và chuyên nghiệp hơn
- Sidebar và khu vực nội dung được cân lại khoảng cách, nhìn gọn và dễ thao tác hơn
- Toàn bộ **luồng hoạt động được giữ nguyên**, không thay đổi logic xử lý

## Không thay đổi

- Ghép nhiều video thành 1 video cuối
- Ghép toàn bộ MP3 thành 1 audio
- Chèn 4 ảnh PNG ở 4 góc
- Preview bố cục
- Burn / export SRT
- Xuất 1 file MP4 duy nhất

## Build / Publish

```bat
dotnet clean
dotnet build -c Release
publish-win-x64.bat
```

Đây là **source code**, chưa phải EXE build sẵn.

## Bản sửa riêng cho header/logo (v4.1 Header Alignment Fix)

- Header chuyển thành **3 cột trực tiếp**: logo, tên ứng dụng, trạng thái xuất.
- Logo căn giữa trong vùng riêng, sử dụng `PictureBoxSizeMode.Zoom`; không đặt `MinimumSize` có thể đẩy hình xuống hoặc làm vỡ layout.
- Bổ sung `Assets/software-logo-header.png`, là logo gốc **được bỏ vùng trong suốt thừa** và thu nhỏ để tải nhanh hơn. Tệp `Assets/software-logo.png` gốc vẫn giữ nguyên.
- Phần tên sản phẩm và mô tả sử dụng hai `Label` được căn giữa theo chiều dọc, xử lý cả khi cửa sổ thay đổi kích thước.
- **Không chỉnh sửa** bất kỳ tệp nào trong `Core/`, không đổi cách xử lý MP3, SRT, overlay hay export.

Sau khi publish, cần đảm bảo có file `publish\\win-x64\\Assets\\software-logo-header.png` cạnh phần mềm (trong thư mục `Assets`). Nếu dùng bản EXE cũ hoặc chưa copy assets mới, header sẽ dùng fallback logo gốc.
