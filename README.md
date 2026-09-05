# Codex Account Manager

Ứng dụng Windows desktop portable quản lý nhiều profile Codex CLI. C#/.NET 8,
WinForms, một cửa sổ, không installer/service, không cần Administrator.

## Chạy bản portable

Mở `dist/CodexAccountManager/CodexAccountManager.exe`. Copy **cả thư mục** để chuyển vị trí.
Bản self-contained Windows x64 đã chứa .NET runtime; máy đích vẫn cần Codex CLI chính thức
và PowerShell 7 trong PATH. Đặt app trên ổ NTFS local có quyền ghi, không đặt dưới Program Files,
thư mục junction/symlink, network share hoặc thư mục shared sessions.

1. Kiểm tra Default Codex Home (mặc định `%USERPROFILE%\.codex`) và Working directory.
2. Nhập Display name, ghi chú tùy chọn; bấm **Add Account**.
3. Đăng nhập trong terminal PowerShell 7 được mở bằng `codex login`.
4. Quay lại app, chọn account và bấm **Check**.
5. **Open Codex** mở terminal riêng; **Resume** chạy `codex resume --all`.

Có thể mở nhiều account đồng thời. Đóng manager không đóng/kill terminal.
Không có tự động polling. **Refresh** kiểm tra dependency và junction; **Check** kiểm tra
login của account được chọn. Hover status bar để xem đường dẫn Codex và PowerShell thực tế.
CLI/Powershell không tìm thấy được báo trong status bar, không làm app crash.

## Dữ liệu và credential

```text
CodexAccountManager/
  CodexAccountManager.exe
  ...runtime files...
  accounts.json
  settings.json
  manager.lock
  logs/app.log
  profiles/<GUID>/
    config.toml
    auth.json          # chỉ Codex CLI tạo sau login
    ...private state...
    sessions -> <Default Codex Home>/sessions
```

App dùng thư mục executable, không phụ thuộc current working directory. Không lưu password,
không đọc/sao chép/hiển thị auth.json. `accounts.json` chỉ chứa metadata và trạng thái chuẩn hóa.
Credential được CLI lưu dạng file theo `cli_auth_credentials_store="file"`; tham số này cũng
được truyền vào từng lệnh để không chuyển credential sang keyring do config override.
Environment credential/remote attachment đã biết được loại khỏi terminal con để tránh kế thừa
identity của process chạy manager. Không sửa biến môi trường Windows hoặc auth/config mặc định.
Các thay đổi thủ công về provider/config/environment trong terminal thuộc quyền kiểm soát người dùng.

JSON ghi nguyên tử bằng temporary file cạnh file đích rồi rename. Không tạo backup thường trực.
File lỗi hoặc version không hỗ trợ bị từ chối, không reset dữ liệu. Chỉ một manager cho mỗi
portable folder; khóa `manager.lock` tự nhả khi process thoát, file tồn tại không có nghĩa còn khóa.
Log tối đa khoảng 1 MiB, chỉ operation/path/mã lỗi; không log raw CLI output hoặc credential.
Codex CLI tự quản lý log/state của nó trong profile; các file đó cũng cần được giữ riêng tư.

## Shared sessions: phạm vi thực tế

Chỉ `profiles/<GUID>/sessions` là **NTFS Directory Junction** trỏ tới thư mục sessions có sẵn.
App không tự tạo/sửa thư mục Codex Home mặc định. Auth, config, history.jsonl, database/index
và các state khác vẫn riêng. Sharing session files **không đảm bảo** resume picker/index của App
và từng CLI giống nhau, đặc biệt khi CLI dùng định dạng hoặc cơ chế migration mới.
Resume `--all` bỏ lọc working directory nhưng không đồng bộ database riêng.

Junction là link đọc/ghi: lệnh Codex sửa/xóa session có thể tác động đến session được chia sẻ.
Cam kết bảo toàn bên dưới áp dụng cho chức năng **Delete account** của manager.
Không junction toàn bộ `.codex` và không tự mở rộng phần được chia sẻ để sửa vấn đề resume.

Junction dùng target tuyệt đối. Chuyển thư mục trên cùng máy giữ target; chuyển sang máy khác
cần target tương ứng. Không tự đổi target của profile có sẵn. Muốn thay Default Codex Home,
Delete các account an toàn trước rồi đổi settings. Chỉ hỗ trợ PowerShell 7; không tự lựa chọn
fallback Windows PowerShell 5.1 hoặc migration phiên bản JSON cũ.

## Trạng thái account và quota

Đã xác minh với Codex CLI **0.153.4**:
- `codex login`: luồng đăng nhập chính thức.
- `codex login status`: trạng thái credential local; **không chứng minh token còn hợp lệ online**.
- `codex resume --all`: mở picker không lọc thư mục.
- Không có standalone subcommand quota trong `codex --help` đã kiểm tra.

Quota hiển thị **Not available**. Không đọc token rồi gọi private API, không scrape UI/API.
`IQuotaProvider` là điểm mở rộng. CLI interactive `/status` hoặc giao thức app-server là các
bề mặt khác, không được giả lập thành subcommand của manager này. Nếu CLI không công bố
login status trong help, Check hiển thị Not available. Lỗi config/CLI/timeout được phân biệt
với Not logged in; output thô không hiển thị. Không suy ra email/account ID từ secret.

## Xóa account và gỡ app

Đóng mọi terminal dùng profile. Chọn **Delete**, đọc tên và đường dẫn trong confirmation.
Manager chặn terminal đang theo dõi còn chạy; sau restart cần người dùng đảm bảo đóng cả
terminal cũ hoặc được mở thủ công. Manager không force-kill Codex.

Quy trình: validate ID/path/ancestors → preflight toàn profile, từ chối reparse bất thường →
verify mount-point tag và target sessions → tháo junction bằng delete không recursive →
verify shared directory còn tồn tại → xóa private tree bằng traversal không theo link →
xóa metadata cuối cùng. Directory handles ngăn rename/reparse khi đang traversal.
Không dùng `Directory.Delete(path, true)` hoặc recursive delete qua junction.

**Trước khi xóa cả thư mục portable, Delete từng account trong app.** Chỉ sau khi tháo hết
junction mới xóa thư mục app. Không giả định mọi công cụ recursive delete đều an toàn với junction.

## Recovery thủ công

Khi một preflight thất bại, manager không xóa file. Nếu hệ thống mất điện/file lock/xâm nhập
đồng thời xảy ra sau detach, private profile có thể bị xóa một phần và metadata vẫn còn.
Manager sẽ từ chối đoán tiếp nếu junction thiếu. Không bấm recursive delete để thử khắc phục.

1. Đóng manager và tất cả terminal dùng profile; xác nhận đúng portable folder và account ID.
2. Trong PowerShell 7, dùng `Get-Item -LiteralPath '<absolute profile>\sessions' -Force |
   Format-List FullName,LinkType,Target` để kiểm tra, không dùng wildcard.
3. Nếu sessions là thư mục thật/symlink/sai target: giữ nguyên dữ liệu, xác định nguyên nhân.
   Không chuyển/xóa tự động. Nếu link đã tháo sau một lần Delete thất bại và thư mục `sessions`
   hoàn toàn không còn, có thể tạo lại đúng junction bằng `New-Item -ItemType Junction -Path
   '<absolute profile>\sessions' -Target '<verified default home>\sessions'`, kiểm tra lại target,
   rồi dùng Delete trong app.
4. Nếu private profile đã mất nhưng record vẫn còn do lỗi ghi JSON: xác minh shared sessions
   còn nguyên, sau đó sửa **chỉ record đúng ID** trong accounts.json khi manager đã đóng.
5. Nếu Add thất bại trước khi lưu record, thư mục GUID có thể còn nhưng không hiện trong grid.
   Kiểm tra config và junction; thêm record đúng schema ở dưới để Delete trong app. Không xóa
   cả cây chưa kiểm tra. Nếu tạo junction đã thất bại và chỉ để lại sessions rỗng bình thường,
   chỉ xóa riêng thư mục rỗng đó (không recursive), tạo junction đã xác minh rồi đăng ký record.
6. JSON lỗi: sửa cú pháp/version/schema trong file hiện có. Không sửa nội dung auth.json.

```json
{
  "version": 1,
  "accounts": [{
    "id": "0123456789abcdef0123456789abcdef",
    "displayName": "Recovered account",
    "profilePath": "profiles/0123456789abcdef0123456789abcdef",
    "sharedSessions": true,
    "createdAt": "2026-09-05T00:00:00Z",
    "lastCheckedAt": null,
    "loginStatus": "Not checked",
    "note": ""
  }]
}
```

## Build, tests, publish

Cần .NET 8 SDK và PowerShell 7. Không cần Visual Studio, Node dependency cho manager,
database hoặc test framework từ NuGet. Integration tests chạy trên Windows NTFS và dùng CLI/Pwsh
được cài trên máy. Fixture chỉ chứa dữ liệu giả trong thư mục test riêng và được dọn không theo junction.

```powershell
pwsh -File ./scripts/build.ps1
# SDK tại vị trí riêng trên máy triển khai này:
pwsh -File ./scripts/build.ps1 -DotNet D:\VSYS\.dotnet-sdk\dotnet.exe
# Chỉ build/test:
pwsh -File ./scripts/build.ps1 -SkipPublish
```

Script build Release, chạy integration tests, publish self-contained win-x64 vào
`dist/CodexAccountManager`. Publish không xóa accounts/settings/profiles của bản có sẵn;
đóng manager trước khi cập nhật binary. Không chạy build vào thư mục đang dùng bằng terminal.
Không đóng gói credential/data vào ZIP hoặc commit Git.

Test bao gồm junction thật, độc lập profile, sentinel sống sau Delete, missing/broken/wrong link,
thư mục thật, nested junction, ancestor junction, traversal, atomic JSON và khóa manager,
quoting Unicode/ký tự shell, process environment, official CLI status trên profile trống.

Manual acceptance cần tài khoản thật: browser login, hai terminal đăng nhập riêng đồng thời,
resume một session App, rồi đóng terminal và Delete profile thử. Build/tests không thay thế bước này.

## Nguồn thiết kế

- [Kế hoạch triển khai](docs/IMPLEMENTATION_PLAN.md)
- [Codex configuration](https://learn.chatgpt.com/docs/config-file/config-advanced)
- [Codex authentication](https://learn.chatgpt.com/docs/auth)
- [CLI reference](https://learn.chatgpt.com/docs/developer-commands?surface=cli)
- [Windows reparse point operations](https://learn.microsoft.com/en-us/windows/win32/fileio/reparse-point-operations)
