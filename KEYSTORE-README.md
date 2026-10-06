# Ký bản Release (Google Play upload key)

Keystore của PDF Reader phải **riêng**, không dùng chung với DocScanner (plan, mục 7). Thư mục `src/PdfReader/release/`
bị gitignore: keystore và mật khẩu **không bao giờ** được commit.

## 1. Tạo keystore (làm một lần)

Cần JDK (đi kèm Android SDK / Visual Studio), chạy `keytool` trong `src/PdfReader/release/`:

```
keytool -genkeypair -v -keystore pdfreader-upload.keystore -alias pdfreader-upload ^
        -keyalg RSA -keysize 2048 -validity 10000
```

Nhập mật khẩu keystore + key, và họ tên / tổ chức khi được hỏi. **Sao lưu file `.keystore` và mật khẩu**
ở nơi an toàn ngoài máy này — mất nó thì không phát hành bản cập nhật được (nếu bật Play App Signing thì
Google còn cho đặt lại upload key, nhưng vẫn nên giữ bản sao).

## 2. Khai báo cho MSBuild

Tạo `src/PdfReader/release/Signing.props` (đã bị gitignore):

```xml
<Project>
  <PropertyGroup>
    <AndroidKeyStore>true</AndroidKeyStore>
    <AndroidSigningKeyStore>$(MSBuildThisFileDirectory)pdfreader-upload.keystore</AndroidSigningKeyStore>
    <AndroidSigningKeyAlias>pdfreader-upload</AndroidSigningKeyAlias>
    <AndroidSigningKeyPass>MẬT_KHẨU_KEY</AndroidSigningKeyPass>
    <AndroidSigningStorePass>MẬT_KHẨU_KEYSTORE</AndroidSigningStorePass>
    <AndroidPackageFormat>aab</AndroidPackageFormat>
  </PropertyGroup>
</Project>
```

`PdfReader.csproj` tự import file này khi build Release nếu nó tồn tại.

## 3. Build bản phát hành

```
dotnet publish src/PdfReader/PdfReader.csproj -c Release -f net10.0-android
```

Tăng `ApplicationVersion` (versionCode) trong `PdfReader.csproj` trước mỗi lần tải lên Play.
