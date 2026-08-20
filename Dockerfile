# syntax=docker/dockerfile:1
#
# Bindery is the host only. Downloader stacks live in plugin images, never here.

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS restore
WORKDIR /src

COPY NuGet.config Directory.Build.props ./
COPY src/Bindery.Core/Bindery.Core.fsproj src/Bindery.Core/
COPY src/Bindery.Host/Bindery.Host.csproj src/Bindery.Host/
RUN dotnet restore src/Bindery.Host/Bindery.Host.csproj

FROM restore AS publish
COPY src/Bindery.Core/ src/Bindery.Core/
COPY src/Bindery.Host/ src/Bindery.Host/
RUN dotnet publish src/Bindery.Host/Bindery.Host.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/* \
    && groupadd --system --gid 10001 bindery \
    && useradd --system --uid 10001 --gid bindery --home-dir /app --shell /usr/sbin/nologin bindery \
    && mkdir -p /data /library \
    && chown -R bindery:bindery /app /data /library

COPY --from=publish --chown=bindery:bindery /app/publish ./

USER bindery

ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    Bindery__DataPath=/data \
    Bindery__LibraryPath=/library

EXPOSE 8080
VOLUME ["/data", "/library"]

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD curl --fail --silent --show-error http://127.0.0.1:8080/healthz >/dev/null || exit 1

ENTRYPOINT ["dotnet", "Bindery.Host.dll"]
