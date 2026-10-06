# One-shot container that applies EF Core migrations, then exits.
# The API never changes the schema itself in Docker/production (see docs/adr/ADR-009-database-migrations.md).

# ---- build: produce a self-contained migration bundle ------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src

COPY dotnet-tools.json ./
RUN dotnet tool restore

COPY Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/Gcs.Domain/Gcs.Domain.csproj           src/Gcs.Domain/
COPY src/Gcs.Contracts/Gcs.Contracts.csproj     src/Gcs.Contracts/
COPY src/Gcs.Application/Gcs.Application.csproj src/Gcs.Application/
COPY src/Gcs.Persistence/Gcs.Persistence.csproj src/Gcs.Persistence/
RUN dotnet restore src/Gcs.Persistence/Gcs.Persistence.csproj

COPY src/Gcs.Domain/      src/Gcs.Domain/
COPY src/Gcs.Contracts/   src/Gcs.Contracts/
COPY src/Gcs.Application/ src/Gcs.Application/
COPY src/Gcs.Persistence/ src/Gcs.Persistence/

# A migration bundle is a single executable containing every migration; it needs no SDK at run time.
RUN RID="linux-$( [ "$TARGETARCH" = "arm64" ] && echo arm64 || echo x64 )" \
 && dotnet ef migrations bundle \
      --project src/Gcs.Persistence \
      --startup-project src/Gcs.Persistence \
      --configuration Release \
      --self-contained \
      --target-runtime "$RID" \
      --output /out/efbundle

# ---- runtime -----------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0 AS runtime
WORKDIR /app
COPY --from=build /out/efbundle ./efbundle

USER $APP_UID

# Uses the same variable as the API, so one secret configures both.
ENTRYPOINT ["/bin/sh", "-c", "exec ./efbundle --connection \"$ConnectionStrings__Postgres\""]
