# syntax=docker/dockerfile:1
# Build context: repository root  (docker build -f docker/Dockerfile .)

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore first, using only project files, so the layer is cached until dependencies change.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Orchestrator.Domain/Orchestrator.Domain.csproj src/Orchestrator.Domain/
COPY src/Orchestrator.Application/Orchestrator.Application.csproj src/Orchestrator.Application/
COPY src/Orchestrator.Infrastructure/Orchestrator.Infrastructure.csproj src/Orchestrator.Infrastructure/
COPY src/Orchestrator.Api/Orchestrator.Api.csproj src/Orchestrator.Api/
RUN dotnet restore src/Orchestrator.Api/Orchestrator.Api.csproj

COPY src/ src/
RUN dotnet publish src/Orchestrator.Api/Orchestrator.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# curl is only used by the container health check.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true
EXPOSE 8080

COPY --from=build /app/publish .

# Non-root user that ships with the .NET 8 images.
USER app

HEALTHCHECK --interval=15s --timeout=3s --start-period=20s --retries=5 \
    CMD curl --fail --silent http://localhost:8080/health/live || exit 1

ENTRYPOINT ["dotnet", "Orchestrator.Api.dll"]
