#!/usr/bin/env bash
# One-time creation of the free Azure resources for the PMS backend.
# Run it in Azure Cloud Shell (Bash) at https://shell.azure.com, or locally after `az login`.
#
#   App Service plan  F1 (Free, Linux)  - 60 CPU minutes/day, 1 GB RAM, sleeps when idle
#   Azure SQL         free offer        - 100,000 vCore seconds + 32 GB per month, auto-pauses when used up
#
# Usage: [LOCATION=<region>] bash setup-azure.sh [app-name]
set -euo pipefail

SUFFIX=$(tr -dc 'a-z0-9' < /dev/urandom | head -c 6 || true)
APP_NAME="${1:-pms-api-$SUFFIX}"
LOCATION="${LOCATION:-southeastasia}"
RESOURCE_GROUP="${RESOURCE_GROUP:-pms-rg}"
PLAN_NAME="pms-plan"
SQL_SERVER="pms-sql-$SUFFIX"
SQL_DB="parking_management_system"
SQL_ADMIN="pmsadmin"
SQL_PASSWORD="Pms!$(tr -dc 'A-Za-z0-9' < /dev/urandom | head -c 20 || true)"
JWT_SECRET=$(tr -dc 'A-Za-z0-9' < /dev/urandom | head -c 64 || true)

echo ">> Registering resource providers (first run on a new subscription takes a few minutes)"
for ns in Microsoft.Sql Microsoft.Web; do
    az provider register --namespace "$ns" --wait -o none
done

echo ">> Resource group $RESOURCE_GROUP ($LOCATION)"
# Azure for Students only allows a few regions; list them with:
#   az policy assignment list --disable-scope-strict-match --query "[].parameters.listOfAllowedLocations.value" -o tsv
# A resource group's own location is only metadata, so an existing group is reused as-is.
if [ "$(az group exists -n "$RESOURCE_GROUP")" != "true" ]; then
    az group create -n "$RESOURCE_GROUP" -l "$LOCATION" -o none
fi

echo ">> Azure SQL server $SQL_SERVER + free database"
az sql server create -g "$RESOURCE_GROUP" -n "$SQL_SERVER" -l "$LOCATION" \
    -u "$SQL_ADMIN" -p "$SQL_PASSWORD" -o none
# 0.0.0.0 - 0.0.0.0 allows connections from Azure services (the web app)
az sql server firewall-rule create -g "$RESOURCE_GROUP" -s "$SQL_SERVER" \
    -n AllowAzureServices --start-ip-address 0.0.0.0 --end-ip-address 0.0.0.0 -o none
az sql db create -g "$RESOURCE_GROUP" -s "$SQL_SERVER" -n "$SQL_DB" \
    -e GeneralPurpose -f Gen5 -c 2 --compute-model Serverless \
    --use-free-limit --free-limit-exhaustion-behavior AutoPause -o none

CONNECTION_STRING="Server=tcp:$SQL_SERVER.database.windows.net,1433;Database=$SQL_DB;User ID=$SQL_ADMIN;Password=$SQL_PASSWORD;Encrypt=True;TrustServerCertificate=False;MultipleActiveResultSets=True;Connect Timeout=60;"

echo ">> App Service plan $PLAN_NAME (F1 Free, Linux) + web app $APP_NAME"
az appservice plan create -g "$RESOURCE_GROUP" -n "$PLAN_NAME" -l "$LOCATION" --sku F1 --is-linux -o none
az webapp create -g "$RESOURCE_GROUP" -p "$PLAN_NAME" -n "$APP_NAME" --runtime "DOTNETCORE:9.0" -o none
az webapp config set -g "$RESOURCE_GROUP" -n "$APP_NAME" --startup-file "dotnet PMS.API.dll" -o none
az webapp config appsettings set -g "$RESOURCE_GROUP" -n "$APP_NAME" -o none --settings \
    ASPNETCORE_ENVIRONMENT=Production \
    Database__UseInMemoryDatabase=false \
    "Database__ConnectionString=$CONNECTION_STRING" \
    "JwtConfiguration__SecretKey=$JWT_SECRET"

echo ">> Enable basic-auth publishing credentials (used by GitLab zip deploy)"
SUBSCRIPTION_ID=$(az account show --query id -o tsv)
az resource update -o none --set properties.allow=true \
    --ids "/subscriptions/$SUBSCRIPTION_ID/resourceGroups/$RESOURCE_GROUP/providers/Microsoft.Web/sites/$APP_NAME/basicPublishingCredentialsPolicies/scm"

CREDS=$(az webapp deployment list-publishing-credentials -g "$RESOURCE_GROUP" -n "$APP_NAME" \
    --query "[publishingUserName, publishingPassword, scmUri]" -o tsv)
DEPLOY_USER=$(echo "$CREDS" | sed -n 1p)
DEPLOY_PASSWORD=$(echo "$CREDS" | sed -n 2p)
SCM_URL=$(echo "$CREDS" | sed -n 3p | sed -E 's#https://[^@]*@#https://#')
WEBAPP_URL="https://$(az webapp show -g "$RESOURCE_GROUP" -n "$APP_NAME" --query defaultHostName -o tsv)"

cat <<OUT

============================================================
Done. Add these in GitLab: Settings > CI/CD > Variables
(tick "Protect variable" and "Mask variable"):

AZURE_WEBAPP_URL      = $WEBAPP_URL
AZURE_SCM_URL         = $SCM_URL
AZURE_DEPLOY_USER     = $DEPLOY_USER
AZURE_DEPLOY_PASSWORD = $DEPLOY_PASSWORD

Keep these somewhere safe (not in git):
SQL admin             = $SQL_ADMIN / $SQL_PASSWORD
SQL server            = $SQL_SERVER.database.windows.net

Swagger after the first deploy: $WEBAPP_URL/swagger
============================================================
OUT
