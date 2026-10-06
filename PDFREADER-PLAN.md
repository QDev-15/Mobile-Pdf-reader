# PDF Reader App — Kế hoạch

> Trạng thái: Ý tưởng / thảo luận ban đầu (2026-10-06). Chưa bắt đầu code.
> App thứ 2 của cùng developer (Nguyễn Hữu Quỳnh), cùng Play Console account với DocScanner.
> **Repo riêng** — tách khỏi `ImageProcessing` để build sạch, không phụ thuộc file/project bên repo DocScanner. Các phần tái dùng được sẽ **copy** sang repo mới (không dùng `ProjectReference` trỏ ngược về repo cũ).

## 1. Ý tưởng gốc (từ owner)

- App đọc PDF, mục tiêu trở thành app mặc định khi mở file PDF trên máy.
- Có OCR để copy được text từ PDF (kể cả PDF scan, không có text layer).
- Giao diện: full màn hình trừ 1 dải trên cùng để hiện banner ads. Toàn bộ phần còn lại hiển thị nội dung PDF.
- Nút nổi nhỏ (floating button) hình edit ở góc dưới trái → bấm vào hiện ra bộ công cụ edit → export ra PDF mới.
- Dashboard khi mở app: danh sách PDF đã từng mở + nút mở PDF mới.
- Ads: banner luôn hiện; interstitial sau mỗi 1 tiếng sử dụng.

## 2. Công nghệ

**Quyết định: .NET MAUI** — tái dùng kinh nghiệm + pattern đã chứng minh hiệu quả từ DocScanner (copy code sang repo mới, không reference ngược). Phần render/OCR giao cho thư viện native, không tự viết engine.

| Việc | Lựa chọn | Lý do |
|---|---|---|
| Render trang PDF | `android.graphics.pdf.PdfRenderer` (Android built-in) qua `Platforms/Android` | Miễn phí, nhanh, chính Google Files dùng |
| Trích xuất text (PDF có text layer) | Thư viện nhẹ, free/MIT | Nhanh + chính xác 100%, không cần OCR cho PDF "số" thường |
| OCR (PDF scan, không có text layer) | Google ML Kit Text Recognition v2 (on-device) | Miễn phí, nhanh, offline, tối ưu sẵn cho mobile |
| Edit + export (annotation) | Android `PdfDocument` API (vẽ lại trang gốc + overlay) | Miễn phí, built-in, linh hoạt cho highlight/vẽ tay/chữ ký |
| Edit cấu trúc (merge/split/rotate/xoá trang) | `PdfSharp` (MIT) | Miễn phí, pure C#, đủ cho nhu cầu reader-editor |

⚠️ **Tránh iText 7 Community** — AGPL license, buộc open-source app hoặc mua license thương mại. Không hợp với app closed-source.

**Build**: áp dụng lại bài học performance từ DocScanner — Release build LLVM AOT, `MauiXamlInflator=SourceGen`, test kỹ trên thiết bị thật (Debug dùng interpreter nên chậm, dễ gây hiểu lầm "app chậm").

## 3. OCR — chiến lược hybrid

1. Mở PDF → kiểm tra từng trang có text layer sẵn không.
2. Có → trích xuất trực tiếp (nhanh, chính xác tuyệt đối, không tốn pin/CPU).
3. Không (ảnh scan thuần) → render trang ra bitmap → chạy ML Kit Text Recognition trên bitmap đó.

⚠️ **Rủi ro cần test sớm**: OCR tiếng Việt có dấu vẫn là điểm yếu chung của hầu hết engine, kể cả ML Kit. Cần test với tài liệu tiếng Việt thật ngay từ giai đoạn đầu, trước khi commit sâu vào kiến trúc.

## 4. Công cụ Edit — theo tier

**Tier 1 — phải có cho bản đầu (MVP)**
- Highlight text, gạch chân/gạch ngang, vẽ tay tự do, thêm text box/ghi chú
- Chữ ký (vẽ tay hoặc đã lưu, đặt bất kỳ đâu trên trang)
- Xoá/xoay/sắp xếp lại trang (kéo thả)
- Merge nhiều PDF thành 1, split PDF thành nhiều file
- Copy text (qua text layer/OCR)
- Export/Share PDF đã chỉnh sửa

**Tier 2 — thêm sau**
- Redact (che đen), watermark/số trang, crop trang, convert trang → ảnh
- Nén PDF giảm dung lượng
- Chế độ đọc tối/sepia, tìm kiếm text, mục lục/outline, lưu vị trí đọc dở

**Tier 3 — nâng cao / về sau**
- OCR toàn bộ file scan → xuất PDF có thể tìm kiếm (searchable PDF) — tính năng Pro tiềm năng
- Fill form PDF, đặt mật khẩu/mã hoá export, thêm stamp/hình ảnh

## 5. UI/UX — 3 hướng style

1. **"Paper & Ink" tối giản** — nền giấy trắng ngà, chữ gần đen, 1 màu nhấn, thanh công cụ tự ẩn khi cuộn.
2. **Material You + FAB** *(đề xuất chọn)* — floating action button chuẩn Material Design, đúng ý tưởng nút edit góc dưới trái đã mô tả.
3. **Dark-first đọc lâu** — nền đen AMOLED mặc định, tông amber/sepia.

**Dashboard**: lưới (grid) thumbnail trang đầu thật của mỗi PDF, không chỉ list tên file. Nút "Open PDF mới" dạng FAB. Mỗi card: tên file, số trang, lần mở gần nhất, % đã đọc.

## 6. Ads & Monetization

- **Banner**: luôn hiện ở dải trên cùng — copy cơ chế ẩn/hiện gọn gàng từ DocScanner.
- **Interstitial**: ⚠️ góp ý điều chỉnh so với ý tưởng ban đầu ("mỗi 1 tiếng" hẹn giờ cứng — dễ cắt ngang lúc đang đọc, rủi ro chính sách AdMob). Đề xuất: giữ giới hạn tần suất "tối đa 1 lần/giờ", nhưng chỉ thực sự hiển thị tại điểm chuyển màn hình tự nhiên (đóng file / mở file mới).
- **Pro IAP tắt quảng cáo**: copy pattern `LicenseService.cs` từ DocScanner, đổi product ID.

## 7. Mức độ tái sử dụng code từ repo DocScanner (ImageProcessing)

Đã khảo sát kỹ — phần lớn "hạ tầng" dùng lại được bằng cách **copy file sang repo mới** (không dùng `ProjectReference` trỏ ngược về repo cũ, để repo mới build độc lập, sạch sẽ).

### Copy nguyên, không sửa gì
| Nguồn (repo ImageProcessing) | Dùng cho |
|---|---|
| `Source\ImageCore.Shared\*` (toàn bộ project — pure C#, không đụng Android) | Copy cả project vào repo mới làm sibling project, PdfReader.csproj reference local. Dùng cho xử lý/nén ảnh trang PDF. |
| `Source\DocScanner\Platforms\Android\AndroidAppUpdate.cs` | Play In-App Update — copy nguyên, chỉ thay lời gọi `Core.Perf.Log(...)` bằng log tương đương của repo mới (hoặc inline). |

### Copy + đổi 1 hằng số/ID
| Nguồn | Đổi gì |
|---|---|
| `Source\DocScanner\Platforms\Android\AndroidDownloadsService.cs` + `Services\IDownloadsService.cs` | Đổi `Subfolder = "DocScanner"` → `"PdfReader"` |
| `Source\DocScanner\Services\AdsService.cs`, `Views\AdBannerSurface.cs`, `Platforms\Android\AdBannerSurfaceHandler.cs`, `AdsConfig.cs` | Đổi 2 hằng số ad unit ID trong `AdsConfig.cs` (phải tạo app + ad unit mới trong AdMob console — không share được giữa 2 app) |
| `Source\DocScanner\Services\LicenseService.cs` + `Source\DocScanner.Core\Licensing\ILicenseService.cs`, `LicenseState.cs` | Đổi hằng số `ProProductId` thành product ID mới tạo trên Play Console |
| `Source\DocScanner.Core\Ads\AdsPolicy.cs` | Viết lại rule tần suất ("1 lần/giờ tại điểm chuyển màn hình" thay vì "mỗi 5 lần export") — giữ nguyên pattern pure-function state → next state |
| `Source\DocScanner.Core\Export\PdfQuality.cs` | Giữ nguyên preset DPI/JPEG quality (Small/Medium/High: 150/200/300 DPI, quality 60/72/90); đổi phần encode ảnh sang `Android.Graphics.Bitmap.Compress` (hoặc SkiaSharp) thay vì GDI+ |

### Không tái dùng — viết mới
- **Keystore ký app** — bắt buộc tạo **keystore mới riêng** cho app này (không dùng chung keystore DocScanner — tách vòng đời bảo mật 2 app). Quy trình tạo (JDK `keytool`, cấu trúc `release/Signing.props` gitignored, `KEYSTORE-README.md`) thì copy nguyên làm template, chỉ chạy lại lệnh tạo keystore mới.
- **`DocumentStore.cs` + `Models.cs`** (cây folder/document của DocScanner) — quá đặc thù mô hình "quét giấy → document → folder", không hợp nhu cầu PDF Reader. Viết model mới đơn giản: `RecentPdfRecord { Uri, DisplayName, PageCount, LastOpenedUtc, ReadProgress }` + JSON store nhỏ gọn.
- **`ImageCoreService` (desktop, GDI+)** — không portable sang Android (Windows-only). Không cần, vì `PdfQuality.cs` đã là bản port C# thuần sẵn dùng được (xem bảng trên).
- Toàn bộ **render PDF, OCR, UI công cụ edit** — lõi khác biệt của app, viết hoàn toàn mới.

## 8. Việc còn thiếu, cần quyết định thêm

1. **Đặt làm app mặc định mở PDF** — khai đúng intent-filter (`VIEW` + mime `application/pdf`, xử lý cả `content://` và `file://`). Android không cho app tự ép thành mặc định — chỉ hiện trong dialog "Open with" để người dùng tự chọn + tick "Always".
2. **PDF nhiều trăm trang** — bắt buộc lazy-render (chỉ vẽ trang đang xem + vài trang lân cận, recycle view khi cuộn xa).
3. **PDF có mật khẩu** — cần màn hình nhập password khi mở file bị khoá.
4. **Quyền truy cập file** — Storage Access Framework (SAF) theo scoped storage Android 11+, không xin quyền storage rộng.
5. **Tên app / package id** — gợi ý giữ namespace `btk.` cho đồng bộ (vd `btk.pdfreader`), cần tên app riêng biệt dễ phân biệt trên Play Store.
6. **Play Console**: dùng chung tài khoản dev đã có — khả năng cao **không phải lặp lại** vòng 12 tester/14 ngày cho app thứ 2 (cần xác nhận trực tiếp khi tạo app mới).
7. **Repo mới**: cần đặt tên + khởi tạo (git init, solution .sln, copy các file ở mục 7 vào đúng vị trí).
8. **Accessibility** — cỡ chữ/reflow text cho người khó đọc chữ nhỏ (nice-to-have, không MVP).
9. **Test đa thiết bị** trước khi public.

## 9. Đề xuất Roadmap (chia giai đoạn kiểu M0–M4 đã dùng cho DocScanner)

- **M0 — Khởi tạo repo + nền tảng**: tạo repo mới, scaffold solution MAUI, copy các file tái dùng (mục 7), render PDF cơ bản (PdfRenderer), mở file từ Downloads/Share intent, dashboard list đơn giản.
- **M1 — Đọc mượt**: lazy-render nhiều trang, zoom/pan, dark mode đọc, lưu vị trí đọc dở.
- **M2 — OCR + copy text**: hybrid text-layer/ML Kit OCR, select & copy text, tìm kiếm trong PDF.
- **M3 — Edit Tier 1**: highlight/vẽ tay/chữ ký/text box, xoá-xoay-sắp xếp trang, merge/split, export.
- **M4 — Monetization + Play**: AdMob banner + interstitial (đúng thời điểm chuyển màn hình), Pro IAP tắt ads, keystore mới, chuẩn bị Play Console (privacy policy, store listing).
- **M5+ — Tier 2/3**: nén PDF, redact, watermark, searchable-PDF export, các tính năng nâng cao khác.

## 10. Việc cần làm trước khi bắt đầu code

- [ ] Chốt tên repo mới + tên app + package id
- [ ] Chốt hướng UI style (1 trong 3 gợi ý ở mục 5, hoặc kết hợp)
- [ ] Test nhanh ML Kit OCR với vài file PDF scan tiếng Việt thật — xác nhận độ chính xác chấp nhận được trước khi commit kiến trúc
- [ ] Xác nhận lại với Play Console xem app thứ 2 có bị bắt lặp lại vòng 12 tester/14 ngày không
- [ ] Tạo AdMob app + ad unit mới, tạo keystore mới
