# CI/CD (GitLab)

Pipeline được định nghĩa trong [`.gitlab-ci.yml`](../.gitlab-ci.yml).

| Stage  | Job                                | Chạy khi                          | Mô tả |
|--------|------------------------------------|-----------------------------------|-------|
| build  | `build`                            | mọi push / merge request          | `dotnet restore` + `dotnet build` (Release) |
| test   | `unit-tests`, `integration-tests`  | mọi push / merge request          | Chạy test, xuất báo cáo JUnit + Cobertura (hiện trong tab *Tests* và diff coverage của MR). Integration test dùng in-memory DB (`appsettings.Testing.json`) nên không cần SQL Server |
| docker | `docker-image`                     | push lên `develop`, `main`, `production`, tag | Build `Dockerfile`, push lên GitLab Container Registry: `<registry>/pms-api:<short-sha>`, `:<branch>`, và `:latest` cho `production` |
| deploy | `deploy-production`                | **tự động** khi push lên `production` (sau khi test pass) | SSH vào server, copy `deploy/docker-compose.prod.yml` + `.env`, `docker compose pull && up -d` |

Quy trình release: merge `develop` → `production` (hoặc `git push origin develop:production`), pipeline sẽ build, test, đóng image và tự cập nhật server. Nếu test fail thì không deploy.

## Yêu cầu runner

- Runner Linux, executor **docker**.
- Job `docker-image` dùng Docker-in-Docker → runner phải bật `privileged = true`.
  Nếu runner của trường không cho phép, báo admin hoặc dùng runner riêng.
- Container Registry phải được bật cho project (Settings → General → Visibility → Container registry).

## Biến CI/CD cho deploy

Trước tiên đặt nhánh `production` là **protected branch** (Settings → Repository → Protected branches, chỉ Maintainer được push/merge) — biến **Protected** chỉ được truyền vào pipeline của nhánh protected.

Settings → CI/CD → Variables (đánh dấu **Protected**, **Masked** khi có thể):

| Biến              | Kiểu     | Ví dụ |
|-------------------|----------|-------|
| `DEPLOY_HOST`     | Variable | `203.0.113.10` |
| `DEPLOY_USER`     | Variable | `deploy` (thuộc group `docker`) |
| `DEPLOY_PATH`     | Variable | `/opt/pms` |
| `SSH_PRIVATE_KEY` | **File** | private key, public key tương ứng nằm trong `~/.ssh/authorized_keys` của `DEPLOY_USER` |
| `SSH_KNOWN_HOSTS` | Variable (tuỳ chọn) | kết quả `ssh-keyscan <host>`; nếu bỏ trống thì tin host key lần đầu |
| `PROD_ENV_FILE`   | **File** | nội dung bên dưới |

Nội dung `PROD_ENV_FILE` (dùng cú pháp `__` của ASP.NET Core để ghi đè `appsettings.json`):

```env
ASPNETCORE_ENVIRONMENT=Production
MSSQL_SA_PASSWORD=<mật-khẩu-mạnh>
Database__UseInMemoryDatabase=false
Database__ConnectionString=Server=pms-sqlserver,1433;Database=parking_management_system;User Id=sa;Password=<mật-khẩu-mạnh>;TrustServerCertificate=True;MultipleActiveResultSets=true;
JwtConfiguration__SecretKey=<chuỗi-ngẫu-nhiên-dài>
SmtpSettings__SenderEmail=<account>@gmail.com
SmtpSettings__Username=<account>@gmail.com
SmtpSettings__Password=<app-password>
```

Server chỉ cần cài Docker Engine + Docker Compose plugin. Khi API khởi động, `AutomatedMigration` tự chạy migration và seed dữ liệu.

## Chạy local bằng Docker

```bash
docker compose up --build
```

API: http://localhost:8080/swagger
