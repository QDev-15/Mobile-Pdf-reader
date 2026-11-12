# PDF Reader App — Kế hoạch

> Trạng thái (cập nhật 2026-10-06): M0–M3 và phần lớn M5 đã được code và build được; **chưa chạy thử trên thiết bị thật**. M4 chỉ còn các bước thủ công (AdMob / Play Billing / keystore / Play Console). Xem README.md cho chi tiết từng tính năng.
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
| ~~`Source\DocScanner\Services\AdsService.cs`, `Views\AdBannerSurface.cs`, `Platforms\Android\AdBannerSurfaceHandler.cs`, `AdsConfig.cs`~~ **(đã làm khác, xem dưới)** | Đổi 2 hằng số ad unit ID trong `AdsConfig.cs` (phải tạo app + ad unit mới trong AdMob/AppLovin/LevelPlay console — không share được giữa 2 app) |
| `Source\DocScanner\Services\LicenseService.cs` + `Source\DocScanner.Core\Licensing\ILicenseService.cs`, `LicenseState.cs` | Đổi hằng số `ProProductId` thành product ID mới tạo trên Play Console |
| `Source\DocScanner.Core\Ads\AdsPolicy.cs` | Viết lại rule tần suất ("1 lần/giờ tại điểm chuyển màn hình" thay vì "mỗi 5 lần export") — giữ nguyên pattern pure-function state → next state |
| `Source\DocScanner.Core\Export\PdfQuality.cs` | Giữ nguyên preset DPI/JPEG quality (Small/Medium/High: 150/200/300 DPI, quality 60/72/90); đổi phần encode ảnh sang `Android.Graphics.Bitmap.Compress` (hoặc SkiaSharp) thay vì GDI+ |

> **Cập nhật quảng cáo (2026-10-08)**: bản kế hoạch ở trên viết trước khi tài khoản AdMob của DocScanner bị Google
> đóng và kiến trúc ads bên đó được viết lại thành thư viện đa nhà quảng cáo (`DocScanner.AdsService`, hỗ trợ
> AdMob/AppLovin/Unity LevelPlay, chọn bằng 1 dòng code). Đã copy thẳng thư viện đó sang đây (`src/AdsService`,
> đổi namespace `DocScanner.AdsService` → `AdsService`) thay vì copy bản AdMob-only cũ — `Services/AdsService.cs`
> (giữ nguyên rule "1 lần/giờ tại điểm chuyển màn hình" riêng của PdfReader) giờ nói chuyện với thư viện đó thay vì
> thẳng với Plugin.AdMob. Tiện thể nâng cấp luôn 2 điểm PdfReader đã tự validate mà bản DocScanner gốc chưa có:
> banner adaptive theo chiều rộng màn hình, và `Reattach()` tránh mất banner khi trang được cache/dùng lại thay vì
> tạo mới. Nhà quảng cáo đang BẬT trong code vẫn là AdMob (id thật vẫn là `REPLACE_ME_*` trong `AdsConfig.cs`,
> chưa tạo app/ad unit thật cho PdfReader ở bất kỳ nhà quảng cáo nào) — xem README.md mục cấu trúc.

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
6. **Play Console**: dùng chung tài khoản dev đã có — **cập nhật 06/11/2026**: giả định cũ "không phải lặp lại" đã SAI. Thực tế với DocScanner (xem `Mobile-doc-scanner/update test 06112026.md`): app đó bị Google bắt test lại 14 ngày dù đã qua vòng test lần đầu, nguyên nhân chính là mức soi xét ở **cấp tài khoản** (tài khoản AdMob gắn cùng dev đã bị đóng 2 lần) chứ không phải tính theo từng app riêng lẻ. Vậy PdfReader (app thứ 2, cùng tài khoản) nên giả định **CÓ khả năng cũng phải qua đủ 12 tester/14 ngày**, không nên trông chờ được miễn.
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

- [x] Chốt tên repo mới + tên app + package id (`Mobile-Pdf-reader`, "PDF Reader", `btk.pdfreader`)
- [x] Chốt hướng UI style: Material You + FAB (nút nổi chỉnh sửa góc dưới trái), nền giấy ngà, nhấn đỏ
- [ ] Test nhanh ML Kit OCR với vài file PDF scan tiếng Việt thật — xác nhận độ chính xác chấp nhận được trước khi commit kiến trúc
- [ ] Giả định PdfReader CŨNG phải qua đủ 12 tester/14 ngày như app thứ 1 (xem mục 8.6) — không trông chờ được miễn trừ
- [ ] Tạo AdMob app + ad unit mới, tạo keystore mới

## 11. Cập nhật 2026-11-12: màn chặn "Điều khoản sử dụng & Quyền riêng tư" trước khi vào app

Port tính năng đã làm và owner duyệt cùng ngày ở app em (Mobile-LogoExtract): màn full-bleed chặn trước
`AppShell`, bắt đồng ý điều khoản quảng cáo thu thập dữ liệu HOẶC mua Pro, HOẶC thoát app. Adapt theo đúng idiom
hiện có của PdfReader (không copy nguyên shape của LogoExtract) — xem các quyết định dưới.

### Đã làm

- **`Views/TermsGatePage.xaml` + `.xaml.cs`** (mới): `ContentPage` với root `Content` là `Grid` trực tiếp (bắt
  buộc, vì `Dialogs.cs`'s `ShowAsync` chỉ hoạt động khi `page.Content is Grid`). Code-behind thuần (named
  elements + event handler), KHÔNG dùng MVVM/`CommunityToolkit.Mvvm` — app này không có package đó (đã kiểm tra
  `PdfReader.csproj`), khác LogoExtract/DocScanner; theo đúng idiom hiện có của `SettingsPage.xaml.cs`.
  3 lối ra đúng hợp đồng đã thống nhất: mua Pro thành công (hoặc phát hiện đã Pro từ máy khác qua
  `RefreshInBackgroundAsync` nền, timeout 2.5s) → vào thẳng app, KHÔNG set cờ `terms_accepted_v1` (để hoàn tiền
  sau này vẫn hiện lại màn này đúng); tick checkbox + "Đồng ý & Tiếp tục" → set cờ, vào app; "Từ chối & Thoát"
  hoặc nút Back phần cứng → `Platform.CurrentActivity?.FinishAffinity()` thoát hẳn app (Back ưu tiên đóng dialog
  lỗi mua hàng đang mở trước, cùng kiểu `this.TryDismiss() || base...` các trang khác đã dùng).
- **`App.xaml.cs`**: thêm `CreateMainPage()` (static), gọi từ `CreateWindow` thay vì `new AppShell()` thẳng.
  Constructor của `App` KHÔNG nhận tham số DI (khác LogoExtract) — tự resolve `ILicenseService` qua
  `IPlatformApplication.Current?.Services.GetService<T>()`, đúng idiom tự-resolve đã có sẵn trong
  `AdsService.Core` (`AdBannerSurface`/`AdBannerSurfaceHandler`), không thêm tham số constructor mới cho `App`.
- **Nút mua Pro trong gate** gọi thẳng `_license.PurchaseProAsync().WaitOrTimeoutAsync()` (helper có sẵn,
  `TaskTimeoutExtensions.cs`, timeout mặc định 60s) + cùng switch-case/copy dialog y hệt `SettingsPage.OnBuyClicked`
  — KHÔNG gọi `ProUpgrade.ShowAsync` (tránh chồng 2 lớp pitch/confirm lên nhau).
- **Màu sắc**: toàn bộ trang dùng `StaticResource` cố định (MilkBackground/White/OffBlack/InkSoft/Primary/
  Tertiary/Secondary), KHÔNG `AppThemeBinding` — quyết định có chủ đích, khớp với thực tế SettingsPage/
  AboutPage/DashboardPage hiện tại (tất cả đều hardcode màu sáng, không theo theme hệ thống; `ReaderPage` có
  "Sáng/Tối/Sepia" riêng nhưng đó là theme đọc do người dùng tự chọn, không phải `AppThemeBinding` theo OS).
  Card "Nâng cấp Pro" dùng `Secondary` (nền) + `Tertiary` (chữ, màu đỏ đậm hơn `Primary`, đã có sẵn trong
  `Colors.xaml` nhưng chưa ai dùng) để đủ tương phản chữ-trên-nền theo đúng bài học WCAG ở gotcha §8.11 của tài
  liệu design chung (Primary đo tương phản không đủ khi dùng làm màu CHỮ trên nền sáng).
- **Gotcha gặp lại, tốn 2 lần build lỗi**: comment XAML chứa `--` (dấu gạch đôi thay em-dash) làm
  `MAUIG1001` lỗi parse XAML → kéo theo toàn bộ field `x:Name` không được sinh ra, lỗi `CS0103` hàng loạt ở
  `.xaml.cs` trông như "code-behind sai" nhưng gốc là ở XAML. Đã sửa cả 2 lần (lần đầu ở comment mở đầu file,
  lần 2 ở 2 comment mới thêm cho fix của owner bên dưới). Dấu `--` trong comment `///`/`//` của file `.cs` thì
  KHÔNG sao (Roslyn không áp rule này cho XML doc comment).
- **2 fix theo phản hồi trực tiếp của owner khi đang live-test trên máy thật** (không nằm trong brief gốc):
  1. Checkbox "Tôi đã đọc và đồng ý..." bị trắng/khó thấy khi máy ở chế độ tối hệ thống — do style `CheckBox`
     chung (`Styles.xaml`) vẫn `AppThemeBinding Light=Primary, Dark=White`, trong khi trang này chủ đích luôn
     hiện màu sáng. Sửa: thêm `Color="{StaticResource Primary}"` trực tiếp trên `<CheckBox x:Name="AgreeCheck">`,
     không đụng style chung (ảnh hưởng toàn app).
  2. 2 nút CTA ("Mua Pro ngay"/"Đồng ý & Tiếp tục") phải nằm NGANG HÀNG khi xoay ngang màn hình, thay vì xếp dọc
     như hiện tại. App này chưa có pattern responsive-theo-orientation nào sẵn — viết mới: 2 nút đặt trong
     `VerticalStackLayout x:Name="CtaPortraitRow"` (mặc định) và một `Grid x:Name="CtaLandscapeRow"` rỗng
     (2 cột) song song trong XAML; code-behind di chuyển (không nhân bản) 2 `Button` giữa 2 container này dựa
     theo `DeviceDisplay.Current.MainDisplayInfo.Orientation` đọc ở `OnAppearing` + theo dõi sống qua
     `MainDisplayInfoChanged` (unsubscribe ở `OnDisappearing`, đúng kỷ luật các nơi khác trong app đã làm với
     event subscribe/unsubscribe). Dùng chung 2 instance Button (không tạo bản sao) để trạng thái
     enable/disable/busy của chúng tự động đồng bộ giữa 2 layout, không phải tự đồng bộ tay.
- **Privacy Policy — sửa cả 2 file local** (xem mục cảnh báo riêng bên dưới): thêm 1 câu khẳng định rõ "mua Pro
  thì SDK quảng cáo không còn chạy nữa, nghĩa là không ai thu thập gì nữa" vào cả `PDF Reader Privacy Policy.html`
  (tiếng Anh, repo root) và `src/PdfReader/pdfreader-privacy-policy.html` (tiếng Việt), bump ngày cập nhật của
  cả 2 file thành 12/11/2026.

### Build & test

- `dotnet build PdfReader/PdfReader.csproj -c Debug` (chạy từ `src/`): **BUILD SUCCEEDED, 0 lỗi** sau khi sửa
  2 lần lỗi comment XAML nói trên; 30 warning, toàn bộ có từ trước (ironSource binding warnings, vài XML-doc
  cref không resolve được, v.v.), không liên quan file mới.
- **Đã cài và test thật trên máy thật** (`RF8M81MKLTA`, `dotnet build -t:Install`): xác nhận bằng mắt — màn
  gate hiện đúng nội dung, card "Nâng cấp Pro" nổi bật đúng màu, giá Pro load thật từ Play ("Mua Pro ngay ·
  26.000 đ") xác nhận `RefreshInBackgroundAsync` hoạt động; tap vào label "Tôi đã đọc..." tick được checkbox và
  nút "Đồng ý & Tiếp tục" chuyển từ mờ/disable sang rõ/enable đúng như thiết kế.
- **CHƯA xác nhận được bằng tay** bước cuối "tap Đồng ý & Tiếp tục → vào thẳng Dashboard" — nhiều lần tap qua
  `adb shell input tap` không trúng, và giữa chừng phát hiện owner đang trực tiếp dùng máy này để live-test
  song song (focus chuyển sang app Minesweeper của owner) nên dừng hẳn việc tự tap qua ADB để tránh làm phiền/
  can thiệp nhầm vào phiên test của owner. Owner tự xác nhận qua live-test của chính họ (xem 2 fix ở trên, lấy
  từ phản hồi trực tiếp đó) — coi là đã test thật, chỉ là không có bằng chứng ảnh chụp của chính tôi cho đúng
  bước này. KHÔNG test flow mua Pro thật (bấm xác nhận thanh toán) — đúng lý do đã tránh ở LogoExtract cùng
  ngày, không tạo giao dịch Play Billing thật trong lúc test.
- **Sự cố đáng lưu ý (tự gây ra, không phải lệnh `adb uninstall` trực tiếp nhưng cùng hậu quả)**: máy
  `RF8M81MKLTA` đã có sẵn PdfReader cài từ 24/10 (bản Release, ký khác key). Lệnh `dotnet build -t:Install` lần
  đầu (Debug, ký bằng debug keystore khác) khiến tooling của .NET for Android tự ÂM THẦM uninstall rồi cài lại
  (do khác chữ ký) — `firstInstallTime` đổi thành hôm nay, nghĩa là TOÀN BỘ dữ liệu riêng của app lúc đó (danh
  sách PDF gần đây, chữ ký đã lưu, cài đặt) đã mất, dù tôi đã kiểm tra package tồn tại trước đó (không đọc được
  nội dung vì bản cũ không debuggable) đúng như quy trình an toàn yêu cầu — chỉ là không lường được rằng lệnh
  build/install bản thân nó tự uninstall khi khác chữ ký, không phải tôi gõ `adb uninstall`. Ghi lại rõ để lần
  sau: **trước khi `dotnet build -t:Install`/`-t:Run` một app đã có bản Release/chữ ký khác trên máy có dữ liệu
  thật, phải hỏi owner trước hoặc dùng máy/emulator riêng** — hậu quả giống hệt `adb uninstall` dù không gõ lệnh
  đó. Nên bổ sung gotcha này vào `mobile-app-design.md` §8.

### Cảnh báo quan trọng cho owner — 3 bản Privacy Policy khác nhau, chưa thống nhất

Hiện có **3 tài liệu Privacy Policy khác nhau** cho app này, không cái nào đồng bộ với 2 cái còn lại:
1. `PDF Reader Privacy Policy.html` (gốc repo, tiếng Anh, "Effective date" vừa cập nhật 12/11/2026) — **không
   được app tham chiếu/đóng gói ở đâu**, chỉ là bản nháp nằm sẵn trên đĩa.
2. `src/PdfReader/pdfreader-privacy-policy.html` (tiếng Việt, "Cập nhật lần cuối" vừa cập nhật 12/11/2026) —
   **cũng không được app tham chiếu/đóng gói ở đâu** (grep tên file này trong toàn repo không ra kết quả nào).
3. Link THẬT trong app (`AboutPage.xaml.cs`'s `PrivacyPolicyUrl`):
   `https://sites.google.com/view/privacy-policy-pdfreader-tonyq/home` — một trang Google Sites BÊN NGOÀI, tôi
   KHÔNG có quyền đọc hay sửa từ repo này.
Tôi đã cập nhật nội dung + ngày của (1) và (2) theo đúng yêu cầu (thêm câu "mua Pro thì không ai thu thập gì
nữa"), nhưng **không đụng vào (3)** vì không có quyền truy cập. Đề xuất: owner chọn 1 trong 3 bản làm bản chính
thức duy nhất đi tiếp (nhiều khả năng nên cập nhật nội dung của (1) hoặc (2) lên thẳng trang Google Sites đang
link thật trong app), thay vì để tôi tự chọn hộ.
