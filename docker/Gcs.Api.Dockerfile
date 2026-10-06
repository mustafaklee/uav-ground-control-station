# ---- build -------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, using only project files, so the (slow) restore layer is cached until a dependency changes.
COPY Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/Gcs.Domain/Gcs.Domain.csproj                 src/Gcs.Domain/
COPY src/Gcs.Contracts/Gcs.Contracts.csproj           src/Gcs.Contracts/
COPY src/Gcs.Application/Gcs.Application.csproj       src/Gcs.Application/
COPY src/Gcs.Mavlink/Gcs.Mavlink.csproj               src/Gcs.Mavlink/
COPY src/Gcs.Telemetry/Gcs.Telemetry.csproj           src/Gcs.Telemetry/
COPY src/Gcs.Messaging/Gcs.Messaging.csproj           src/Gcs.Messaging/
COPY src/Gcs.Persistence/Gcs.Persistence.csproj       src/Gcs.Persistence/
COPY src/Gcs.Infrastructure/Gcs.Infrastructure.csproj src/Gcs.Infrastructure/
COPY src/Gcs.Api/Gcs.Api.csproj                       src/Gcs.Api/
RUN dotnet restore src/Gcs.Api/Gcs.Api.csproj

COPY src/ src/
RUN dotnet publish src/Gcs.Api/Gcs.Api.csproj \
      --configuration Release \
      --no-restore \
      --output /app \
      -p:UseAppHost=false

# ---- runtime -----------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_gcServer=1

COPY --from=build /app ./

# The base image ships a non-root "app" user; never run the API as root.
USER $APP_UID
EXPOSE 8080

ENTRYPOINT ["dotnet", "Gcs.Api.dll"]
