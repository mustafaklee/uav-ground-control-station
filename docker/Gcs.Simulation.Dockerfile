# Simulated vehicle that sends MAVLink over UDP to the GCS (stand-in for PX4 SITL during development).

# ---- build -------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/Gcs.Domain/Gcs.Domain.csproj           src/Gcs.Domain/
COPY src/Gcs.Contracts/Gcs.Contracts.csproj     src/Gcs.Contracts/
COPY src/Gcs.Application/Gcs.Application.csproj src/Gcs.Application/
COPY src/Gcs.Mavlink/Gcs.Mavlink.csproj         src/Gcs.Mavlink/
COPY src/Gcs.Simulation/Gcs.Simulation.csproj   src/Gcs.Simulation/
RUN dotnet restore src/Gcs.Simulation/Gcs.Simulation.csproj

COPY src/Gcs.Domain/      src/Gcs.Domain/
COPY src/Gcs.Contracts/   src/Gcs.Contracts/
COPY src/Gcs.Application/ src/Gcs.Application/
COPY src/Gcs.Mavlink/     src/Gcs.Mavlink/
COPY src/Gcs.Simulation/  src/Gcs.Simulation/
RUN dotnet publish src/Gcs.Simulation/Gcs.Simulation.csproj \
      --configuration Release \
      --no-restore \
      --output /app \
      -p:UseAppHost=false

# ---- runtime -----------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./
USER $APP_UID
ENTRYPOINT ["dotnet", "Gcs.Simulation.dll"]
