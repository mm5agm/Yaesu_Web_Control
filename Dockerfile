# Yaesu Web Control — CAT-only Linux host (multi-arch: amd64 + arm64).
# Intended for a shack PC or Raspberry Pi as the always-on CAT/web controller.
# SDR spectrum and Voice Control are Windows-only and are not in this image.
#
# Build (current arch):
#   docker build -t ywc:local .
#
# Multi-arch (needs buildx; load one platform, or push a manifest):
#   docker buildx build --platform linux/amd64,linux/arm64 -t ywc:local --load .
#   # --load only supports one platform; for both, push to a registry:
#   docker buildx build --platform linux/amd64,linux/arm64 -t YOUR/ywc:latest --push .
#
# Run: see docker-compose.yml

# syntax=docker/dockerfile:1

ARG DOTNET_VERSION=10.0

# ── Build ────────────────────────────────────────────────────────────────────
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
ARG TARGETARCH
WORKDIR /src

COPY Yaesu_Web_Control.csproj ./
RUN arch="$TARGETARCH"; \
    if [ "$arch" = "amd64" ]; then arch=x64; fi; \
    dotnet restore Yaesu_Web_Control.csproj -r "linux-$arch"

COPY . .
# Publish the CAT-only TFM only (WinForms / voice / SDR worker stay out).
RUN arch="$TARGETARCH"; \
    if [ "$arch" = "amd64" ]; then arch=x64; fi; \
    dotnet publish Yaesu_Web_Control.csproj \
      -c Release \
      -f net10.0 \
      -r "linux-$arch" \
      --self-contained false \
      --no-restore \
      -o /app/publish \
      /p:UseAppHost=true

# ── Runtime ──────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS final
WORKDIR /app

# System.IO.Ports on Linux talks to USB-serial via libudev.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libudev1 \
    && rm -rf /var/lib/apt/lists/*

# Persist settings/logs under XDG_CONFIG_HOME (SpecialFolder.ApplicationData).
# Official aspnet images already ship an `app` user (uid/gid 1000).
ENV DOTNET_RUNNING_IN_CONTAINER=true \
    ASPNETCORE_URLS= \
    XDG_CONFIG_HOME=/data \
    HOME=/home/app \
    TZ=UTC

RUN usermod -aG dialout app \
    && mkdir -p /data \
    && chown -R app:app /data /home/app

COPY --from=build /app/publish .
COPY docker/entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh \
    && chown -R app:app /app

USER app
EXPOSE 8080
VOLUME ["/data"]
ENTRYPOINT ["/entrypoint.sh"]
