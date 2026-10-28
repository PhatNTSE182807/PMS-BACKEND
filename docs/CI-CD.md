# CI/CD (GitLab → Azure)

Pipeline được định nghĩa trong [`.gitlab-ci.yml`](../.gitlab-ci.yml).

| Stage  | Job                                | Chạy khi                          | Mô tả |
|--------|------------------------------------|-----------------------------------|-------|
| build  | `build`                            | mọi push / merge request          | `dotnet restore` + `dotnet build` (Release) |
| test   | `unit-tests`, `integration-tests`  | mọi push / merge request          | Chạy test, xuất báo cáo JUnit + Cobertura (tab *Tests* và coverage trong MR). Integration test dùng in-memory DB nên không cần SQL Server |
| docker | `docker-image`                     | push lên `develop`, `main`, tag   | Build `Dockerfile` (cần runner `privileged`); chỉ push lên GitLab Container Registry khi server có registry. `git.fsoft-academy.edu.vn` hiện không bật registry nên job chỉ build để kiểm tra Dockerfile |
| deploy | `deploy-azure`                     | **tự động** khi push lên `production` (sau khi test pass) | `dotnet publish` → zip deploy lên Azure App Service → chờ `/swagger` trả 200 |

Quy trình release: merge `develop` → `production` (hoặc `git push origin develop:production`). Test fail thì không deploy.

## Chi phí Azure

Toàn bộ dùng gói miễn phí:

| Dịch vụ | Gói | Giới hạn |
|---------|-----|----------|
| App Service (Linux) | **F1 Free** | 60 phút CPU/ngày, 1 GB RAM, app ngủ khi không có request (lần gọi đầu chậm vài chục giây), không chạy được Docker container nên deploy dạng code |
| Azure SQL Database | **Free offer** | 100.000 vCore-giây + 32 GB mỗi tháng; hết hạn mức thì DB tự tạm dừng tới đầu tháng sau, **không tính tiền** |

Tài khoản: dùng **Azure for Students** (https://azure.microsoft.com/free/students) bằng email `@fpt.edu.vn` — không cần thẻ tín dụng, có $100 credit/12 tháng (chỉ dành cho sinh viên đại học toàn thời gian).
Nếu không đủ điều kiện, tài khoản Azure free thường yêu cầu thẻ — hai gói trên vẫn miễn phí nhưng cần cẩn thận không tạo nhầm gói trả phí.

## Thiết lập lần đầu

### 1. Tạo tài nguyên Azure

Mở **Azure Cloud Shell** (https://shell.azure.com, chọn Bash), upload file [`deploy/azure/setup-azure.sh`](../deploy/azure/setup-azure.sh) rồi chạy:

```bash
bash setup-azure.sh
```

Script tạo resource group `pms-rg` (Southeast Asia), SQL server + database free, App Service plan F1, web app .NET 9, cấu hình connection string / JWT secret, rồi in ra 4 biến cho GitLab và mật khẩu SQL admin (lưu lại chỗ an toàn, không commit).

### 2. Cấu hình GitLab

1. **Settings → Repository → Protected branches**: protect nhánh `production` (chỉ Maintainer push/merge). Biến *Protected* chỉ được truyền vào pipeline của nhánh protected.
2. **Settings → CI/CD → Variables**: thêm 4 biến script in ra, tick **Protect variable**, và **bỏ tick "Expand variable reference"** (username bắt đầu bằng `$`):

   | Biến | Mask |
   |------|------|
   | `AZURE_WEBAPP_URL` | |
   | `AZURE_SCM_URL` | |
   | `AZURE_DEPLOY_USER` | |
   | `AZURE_DEPLOY_PASSWORD` | ✔ |

3. Vào **Build → Pipelines**, chạy lại pipeline của nhánh `production`.

Link backend: `<AZURE_WEBAPP_URL>/swagger` (hiện ở **Operate → Environments → production**).

### Cấu hình thêm (SMTP, …)

Thêm trong Azure Portal → Web app → **Settings → Environment variables**, dùng cú pháp `__` để ghi đè `appsettings.json`, ví dụ `SmtpSettings__Username`, `SmtpSettings__Password`.

### Xem log khi lỗi

Azure Portal → Web app → **Monitoring → Log stream**.

## Chạy local bằng Docker

```bash
docker compose up --build
```

API: http://localhost:8080/swagger
