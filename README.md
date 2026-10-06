# Mobile-Pdf-reader

App đọc PDF (.NET MAUI, Android) — xem [PDFREADER-PLAN.md](PDFREADER-PLAN.md) cho kế hoạch đầy đủ.

## Cấu trúc

```
PdfReader.slnx
src/
  PdfReader/            MAUI app (net10.0-android). Dashboard + Reader, Ads/License/Downloads services.
  PdfReader.Core/        Logic thuần C# (Ads policy, Licensing, PdfQuality) — unit-test được, không đụng Android.
  ImageCore.Shared/      Thư viện xử lý ảnh thuần C# (copy từ Mobile-doc-scanner/DocScanner.Shared), chưa dùng tới.
```

## Build

```
dotnet build PdfReader.slnx -c Debug
```

Debug dùng interpreter nên chậm hơn máy thật thấy rõ — xem trực tiếp trên thiết bị, đừng kết luận "app chậm" từ Debug build.

## Trạng thái — M0 (khởi tạo repo + nền tảng)

Đã có:
- Scaffold solution MAUI, Android-only (`btk.pdfreader`)
- Dashboard: danh sách PDF gần mở (dạng list, thumbnail trang đầu thật), FAB mở PDF mới
- Reader: render PDF cơ bản qua `PdfRenderer` (Android), chuyển trang Trước/Sau
- Mở PDF từ "Open with" / "Share" của app khác (VIEW/SEND, `application/pdf`)
- Services tái dùng/điều chỉnh từ DocScanner: AdsService, LicenseService (Play Billing), AndroidDownloadsService, AndroidAppUpdate (Play In-App Update)

Chưa có (đúng theo roadmap, xem mục 9 trong plan):
- M1: lazy-render nhiều trang, zoom/pan, dark mode đọc, lưu vị trí đọc dở
- M2: OCR (ML Kit) + copy text + tìm kiếm
- M3: edit tools (highlight/vẽ tay/chữ ký/text box), merge/split, export
- M4: AdMob/Play Billing ID thật (đang là test ID placeholder), keystore release riêng, Play Console

## TODO trước khi build Release / lên Play

- [ ] Tạo AdMob app + 2 ad unit ID thật, điền vào `AdsConfig.cs` và `PdfReader.csproj` (`AndroidManifestPlaceholders`)
- [ ] Tạo product ID Pro thật trên Play Console, điền vào `LicenseService.ProProductId`
- [ ] Tạo keystore ký app riêng (không dùng chung với DocScanner) — xem `PdfReader.csproj` phần Release signing
