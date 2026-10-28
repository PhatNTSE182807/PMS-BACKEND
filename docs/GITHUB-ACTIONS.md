# CI/CD on GitHub Actions (GitLab mirror)

GitLab (`git.fsoft-academy.edu.vn`) stays the main repository: the team pushes, opens merge
requests and submits work there as before. A GitLab **push mirror** copies every branch to a
private GitHub repository, and [`.github/workflows/ci.yml`](../.github/workflows/ci.yml) runs
there on GitHub's hosted runners. No team machine has to stay on, and images go to GitHub
Container Registry because the school GitLab has no registry.

> Ask the mentor first whether the code may be copied to a private GitHub repository.

| Job | Runs on | What it does |
|-----|---------|--------------|
| `build` | every push and pull request | `dotnet restore` + `dotnet build` (Release) |
| `unit-tests`, `integration-tests` | every push and pull request | test results and Cobertura coverage kept as artifacts for 7 days |
| `docker-image` | every push; pushes only on `develop`, `main` and tags | builds the `Dockerfile`; pushes `ghcr.io/<owner>/<repo>/pms-api` tagged with the short SHA and branch (`latest` on `main`) |
| `deploy-azure` | push to `production`, when `AZURE_DEPLOY_ENABLED` is `true` | `dotnet publish` → zip deploy to Azure App Service → waits for `/swagger` to answer 200 |

## One-time setup

### 1. Create the GitHub repository

Create an **empty, private** repository (no README, no licence), e.g. `pms-backend`.
The frontend gets its own one (see `document/GITHUB-ACTIONS.md` in the frontend repo).

### 2. Create a GitHub token for the mirror

GitHub → Settings → Developer settings → **Fine-grained personal access tokens** → Generate:

- Repository access: only the mirror repositories.
- Permissions: **Contents: Read and write** and **Workflows: Read and write** (without
  "Workflows", GitHub refuses pushes that touch `.github/workflows/`).
- Pick an expiry date and write it down: the mirror stops when the token expires.

### 3. Turn on the push mirror in GitLab

GitLab project → **Settings → Repository → Mirroring repositories** → Add new:

- Git repository URL: `https://github.com/<owner>/pms-backend.git`
- Mirror direction: **Push**
- Authentication method: **Username and Password**: your GitHub user name, and the token as the password
- Leave **Mirror only protected branches** unticked, so feature branches are tested too
- Keep divergent refs: ticked

Click **Mirror repository**, then the refresh button to push once now. Afterwards GitLab
pushes within a few minutes of every push. The run appears under the GitHub repo's **Actions** tab.

### 4. Deploy settings in GitHub

GitHub repo → **Settings → Secrets and variables → Actions**. Use the same four values as the
GitLab variables (printed by `deploy/azure/setup-azure.sh`, see [CI-CD.md](CI-CD.md)):

| Kind | Name | Value |
|------|------|-------|
| Variable | `AZURE_WEBAPP_URL` | `https://<app>.azurewebsites.net` |
| Variable | `AZURE_SCM_URL` | `https://<app>.scm.azurewebsites.net` |
| Secret | `AZURE_DEPLOY_USER` | publishing user name (it starts with `$`; paste it as is) |
| Secret | `AZURE_DEPLOY_PASSWORD` | publishing password |

Optional: Settings → **Environments** → `production` → required reviewers, if a person should
approve each deploy.

### 5. Move deploys from GitLab to GitHub

Both pipelines can deploy `production`. Switch on exactly one, or every release is deployed twice:

1. GitHub: add the repository **variable** `AZURE_DEPLOY_ENABLED` = `true`.
2. GitLab: Settings → CI/CD → Variables: add `AZURE_DEPLOY_DISABLED` = `true`.

To go back, remove both variables. Until step 1 is done, GitHub runs build, tests and the image
but never deploys.

### 6. The GitLab runner

GitLab CI keeps running on the team's project runner when it is on. Once GitHub Actions is green,
the runner can be paused (GitLab → Settings → CI/CD → Runners → pause), or left as a second check.

## Releasing

Same as before: merge `develop` into `production` on GitLab (or `git push origin develop:production`).
The mirror copies it to GitHub, tests run, then `deploy-azure` deploys. Follow it under
**Actions**, and in Azure Portal → Web app → Deployment Center → **Logs**.

## Docker image

Images are private by default: GitHub → your profile → **Packages** → `pms-api`. Pull it with a
token that has `read:packages`:

```bash
docker login ghcr.io -u <github-user>
docker pull ghcr.io/<owner>/pms-backend/pms-api:develop
```
