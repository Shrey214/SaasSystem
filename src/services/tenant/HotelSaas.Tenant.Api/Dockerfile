# Multi-stage: the SDK builds, the runtime image ships. The published
# container has no compiler and no source in it.
#
# Build context is the REPOSITORY ROOT, not this folder, because the service
# references Directory.Build.props, Directory.Packages.props and the shared
# BuildingBlocks projects:
#
#   docker build -f src/services/tenant/HotelSaas.Tenant.Api/Dockerfile -t hs-tenant-api .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Manifests first, restore, THEN the sources. Docker caches per layer, so
# editing a .cs file no longer re-downloads every package.
# .editorconfig is NOT optional here. It is what marks
# **/Migrations/*.cs as generated code and turns analysis off for it.
# Without it the container build is STRICTER than the local build and fails
# on analyzer findings in EF's own generated migration - a build that
# succeeds on a developer machine and fails in CI for no visible reason.
COPY Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/BuildingBlocks/ src/BuildingBlocks/
COPY src/services/tenant/ src/services/tenant/

RUN dotnet restore src/services/tenant/HotelSaas.Tenant.Api/HotelSaas.Tenant.Api.csproj

RUN dotnet publish src/services/tenant/HotelSaas.Tenant.Api/HotelSaas.Tenant.Api.csproj \
    --no-restore \
    --configuration Release \
    --output /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# curl is here only so the container can probe itself. The aspnet image
# ships neither curl nor wget, and compose runs healthchecks INSIDE the
# container, so without this depends_on:service_healthy can never pass.
RUN apt-get update \
    && apt-get install --yes --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app .

# The .pdb files ship deliberately. Without them StackFrame.GetFileName()
# and GetFileLineNumber() return nothing, and fault_file / fault_line in
# error_logs - the whole point of ADR-0008 - come back empty.

# $APP_UID is a non-root user the aspnet image already defines. Set AFTER
# apt-get, which needs root, and after COPY.
USER $APP_UID

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

# /health, not /health/ready: liveness must answer even when postgres is
# down, because restarting the process fixes nothing and loses the buffered
# error log. Readiness is the orchestrator's separate concern.
HEALTHCHECK --interval=15s --timeout=3s --start-period=25s --retries=5 \
    CMD curl --fail --silent http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "HotelSaas.Tenant.Api.dll"]
