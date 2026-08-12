# syntax=docker/dockerfile:1
ARG DOTNET_TAG=10.0

# ---------- Build ----------
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_TAG} AS build
ARG MATFILE_VERSION=""
WORKDIR /src

# Erst nur die Projektdateien -> Restore bleibt gecacht, solange sich Pakete nicht ändern.
COPY Directory.Build.props version.props ./
COPY src/MatFile.Web/MatFile.Web.csproj src/MatFile.Web/
RUN dotnet restore src/MatFile.Web/MatFile.Web.csproj

COPY . .
RUN dotnet publish src/MatFile.Web/MatFile.Web.csproj \
        --no-restore -c Release -o /app/publish \
        ${MATFILE_VERSION:+-p:MatFileVersion=$MATFILE_VERSION}

# ---------- Runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_TAG} AS runtime

# cifs-utils/nfs-common werden nur für optionale OS-Level-Mounts gebraucht (benötigen CAP_SYS_ADMIN).
ARG INSTALL_MOUNT_TOOLS=true
# ffmpeg ist nur für Video-Thumbnails nötig und vergrößert das Image deutlich.
ARG INSTALL_FFMPEG=false

RUN set -eux; \
    apt-get update; \
    apt-get install -y --no-install-recommends ca-certificates tzdata curl; \
    if [ "$INSTALL_MOUNT_TOOLS" = "true" ]; then \
        apt-get install -y --no-install-recommends cifs-utils nfs-common; \
    fi; \
    if [ "$INSTALL_FFMPEG" = "true" ]; then \
        apt-get install -y --no-install-recommends ffmpeg; \
    fi; \
    rm -rf /var/lib/apt/lists/*

ENV ASPNETCORE_HTTP_PORTS=2222 \
    MATFILE_DATA=/data \
    DOTNET_RUNNING_IN_CONTAINER=true \
    TZ=Europe/Berlin

WORKDIR /app
COPY --from=build /app/publish ./
COPY docker/entrypoint.sh /usr/local/bin/entrypoint.sh
RUN chmod +x /usr/local/bin/entrypoint.sh && mkdir -p /data

EXPOSE 2222
VOLUME ["/data"]

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD curl -fsS http://127.0.0.1:2222/healthz || exit 1

ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
CMD ["dotnet", "/app/MatFile.Web.dll"]
