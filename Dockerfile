FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app
# .NET 8+ images listen on 8080 by default (ASPNETCORE_HTTP_PORTS)
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
# Copy project files first so the restore layer is cached between builds
COPY ["src/PMS.API/PMS.API.csproj", "PMS.API/"]
COPY ["src/PMS.Application/PMS.Application.csproj", "PMS.Application/"]
COPY ["src/PMS.Core/PMS.Core.csproj", "PMS.Core/"]
COPY ["src/PMS.DataAccess/PMS.DataAccess.csproj", "PMS.DataAccess/"]
COPY ["src/PMS.Shared/PMS.Shared.csproj", "PMS.Shared/"]
RUN dotnet restore "PMS.API/PMS.API.csproj"
COPY ["src", "."]

FROM build AS publish
RUN dotnet publish "PMS.API/PMS.API.csproj" -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "PMS.API.dll"]
