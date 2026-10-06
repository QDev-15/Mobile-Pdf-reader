| `Services/PdfOpener.cs`, `PdfLibrary.cs` | Mọi đường mở file (picker / Open with / Recent / kết quả) → copy vào kho riêng của app, hỏi mật khẩu nếu có. Mở từ app khác (`external`) đi thẳng vào Reader (qua `LaunchPage`, không qua Dashboard) và đóng Reader là thoát app. |
# Mobile-Pdf-reader

App đọc PDF (.NET MAUI, Android) — xem [PDFREADER-PLAN.md](PDFREADER-PLAN.md) cho kế hoạch đầy đủ.

## Cấu trúc

```
PdfReader.slnx
src/
  PdfReader/            MAUI app (net10.0-android): trang Dashboard / Reader / Quản lý trang, view đọc native.
  PdfReader.Core/       Logic thuần C# (chạy test được trên PC, không đụng Android): mô hình chú thích + undo,
                        chữ & tìm kiếm, thao tác cấu trúc PDF (PDFsharp), đọc text layer (PdfPig), ghi PDF ảnh/
                        searchable, AdsPolicy, Licensing, PdfQuality.
  ImageCore.Shared/     Thư viện xử lý ảnh thuần C# (copy từ Mobile-doc-scanner), chưa dùng tới.
tests/
  PdfReader.Core.Tests/ xUnit — 33 test cho PdfReader.Core.
```

Điểm chính trong `src/PdfReader/` (Views: Dashboard, Reader, Sắp xếp trang, Cài đặt, Giới thiệu; `Dialogs.cs` là bộ pop-up bo góc dùng chung):

| File | Vai trò |
|---|---|
| `Platforms/Android/PdfCanvasView.cs` | View native vẽ cả tài liệu thành dải trang liên tục: zoom/pan/fling, render lười (chỉ trang đang xem + lân cận, vùng zoom nét), chọn chữ, tìm kiếm, công cụ vẽ. Một view giữ toàn bộ cảm ứng vì zoom, vẽ nét và chọn chữ cùng cần ngón tay. |
| `Platforms/Android/AnnotationPainter.cs` | Một hàm vẽ chú thích dùng chung cho màn hình và khi xuất → hai bên luôn khớp. |
| `Services/PdfSession.cs` | Tài liệu đang mở: renderer, kích thước trang, text layer, chú thích (tự lưu sau mỗi thay đổi). |
| `Services/TextLayerService.cs` | Chữ của trang: text layer nếu có (PdfPig), không thì OCR (ML Kit) và nhớ kết quả trên đĩa. |
| `Services/PdfExporter.cs` | Xuất PDF kèm chú thích, nén, PDF tìm kiếm được, trang → ảnh, đặt mật khẩu. |
| `Services/PdfOpener.cs`, `PdfLibrary.cs` | Mọi đường mở file (picker / Open with / Recent / kết quả) → copy vào kho riêng của app, hỏi mật khẩu nếu có. |

## Build & test

```
dotnet build PdfReader.slnx -c Debug
dotnet test tests/PdfReader.Core.Tests
```

Debug dùng JIT nên chậm hơn máy thật thấy rõ — xem trực tiếp trên thiết bị, đừng kết luận "app chậm" từ Debug build.
Hướng dẫn ký bản Release: [KEYSTORE-README.md](KEYSTORE-README.md).

## Trạng thái

**M0–M3 và phần lớn M5 đã viết xong và build được; chưa chạy thử trên thiết bị/emulator thật** (máy dev không có
adb/emulator trong lúc làm). Phần Core có test tự động; phần Android (viewer, cảm ứng, OCR, export) mới chỉ được
biên dịch — cần test tay trên máy thật trước khi tin.

Đã có:
- **Đọc (M1)**: cuộn liên tục mọi trang, pinch-zoom / kéo / vuốt, chạm đúp phóng to, render lười + vùng zoom nét,
  chế độ Sáng / Tối / Sepia, nhớ trang đang đọc, PDF có mật khẩu (hỏi mật khẩu rồi mở khoá bản sao trong app),
  mục lục (bookmark).
- **Chữ (M2)**: giữ lâu để chọn chữ rồi kéo để mở rộng, sao chép; trang scan tự OCR (ML Kit, offline) và nhớ kết
  quả; tìm kiếm cả tài liệu (không phân biệt hoa/thường, bỏ dấu tiếng Việt; trang scan được hỏi có OCR không).
- **Chỉnh sửa (M3)**: nút nổi ở góc dưới trái → bút, dạ quang, thêm chữ, chữ ký (vẽ một lần, lưu dùng lại), hoàn
  tác / làm lại; trên chữ đã chọn: tô sáng, gạch chân, gạch ngang, che (redact); chạm vào chú thích để chọn, kéo
  để di chuyển, đổi cỡ, sửa chữ, xoá. Quản lý trang: xoay / xoá / sắp xếp (kéo hoặc mũi tên). Gộp nhiều PDF (menu ⋮
  ở Dashboard), tách theo khoảng trang. Xuất PDF mới (Nhỏ / Vừa / Cao) → lưu Tải xuống / chia sẻ / mở tiếp.
- **Tier 2/3 (M5)**: nén PDF, PDF tìm kiếm được (OCR + chữ ẩn), che nội dung, watermark, đánh số trang, trang → ảnh
  (PNG/JPG), đặt mật khẩu.
- **Quảng cáo (M4, phần code)**: banner; interstitial tối đa 1 lần/giờ và chỉ ở lúc chuyển màn hình (đóng / mở
  file), lần đầu cài đặt chỉ bắt đầu tính giờ chứ không hiện ngay.

Chưa có:
- Cắt trang (crop), điền form PDF, thêm ảnh/stamp, hỗ trợ cỡ chữ lớn (accessibility), Dashboard dạng lưới.
- Với PDF đã có chú thích khi xuất: các trang có chú thích bị chuyển thành ảnh (mất text layer của riêng các trang
  đó); các trang khác giữ nguyên.

## TODO trước khi build Release / lên Play (việc phải làm thủ công)

- [ ] Tạo AdMob app + 2 ad unit ID thật, điền vào `AdsConfig.cs` và `PdfReader.csproj` (`AndroidManifestPlaceholders`)
- [ ] Tạo product ID Pro thật trên Play Console, điền vào `LicenseService.ProProductId`
- [ ] Tạo keystore ký app riêng — xem [KEYSTORE-README.md](KEYSTORE-README.md)
- [ ] Test tay trên thiết bị thật: zoom/pan, chọn chữ, OCR tiếng Việt, vẽ/chữ ký, export, mật khẩu, PDF lớn
- [ ] Privacy policy, store listing, kiểm tra Play Console có bắt lặp vòng 12 tester / 14 ngày cho app thứ 2 không
