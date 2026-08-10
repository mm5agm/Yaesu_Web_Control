# Yaesu Web Control — macOS / Linux CAT-only host builds
#
# Default: make help
# Native:  make build | run | publish
# Docker:  make docker | docker-multi | compose-up

.PHONY: help restore build run publish \
	publish-osx-arm64 publish-osx-x64 publish-linux-x64 publish-linux-arm64 \
	dmg dmg-osx-arm64 dmg-osx-x64 dmg-all \
	docker docker-load docker-multi compose-up compose-down clean

PROJ            := Yaesu_Web_Control.csproj
TFM             := net10.0
CONFIG          ?= Release
IMAGE           ?= ywc:local
REGISTRY_IMAGE  ?= ywc:latest
PLATFORMS       ?= linux/amd64,linux/arm64
PUBLISH_DIR     ?= publish
DMG_SCRIPT      := scripts/macos/build-dmg.sh
YWC_SERIAL_DEVICE ?= /dev/ttyUSB0

# Host RID for `make publish` (override with RID=… if needed).
UNAME_S := $(shell uname -s)
UNAME_M := $(shell uname -m)
ifeq ($(UNAME_S),Darwin)
  ifeq ($(UNAME_M),arm64)
    HOST_RID := osx-arm64
  else
    HOST_RID := osx-x64
  endif
else ifeq ($(UNAME_S),Linux)
  ifeq ($(UNAME_M),aarch64)
    HOST_RID := linux-arm64
  else
    HOST_RID := linux-x64
  endif
else
  HOST_RID := linux-x64
endif
RID ?= $(HOST_RID)

help:
	@echo "Yaesu Web Control — macOS / Linux (CAT-only, $(TFM))"
	@echo ""
	@echo "Native"
	@echo "  make restore              Restore NuGet packages"
	@echo "  make build                Build CAT-only host ($(CONFIG))"
	@echo "  make run                  Run CAT-only host → http://localhost:8080"
	@echo "  make publish              Publish for this host (RID=$(HOST_RID))"
	@echo "  make publish-osx-arm64    Publish osx-arm64 (framework-dependent)"
	@echo "  make publish-osx-x64      Publish osx-x64 (framework-dependent)"
	@echo "  make publish-linux-x64    Publish linux-x64"
	@echo "  make publish-linux-arm64  Publish linux-arm64"
	@echo ""
	@echo "macOS DMG (unsigned, self-contained CAT-only .app)"
	@echo "  make dmg                  DMG for this Mac (RID=$(HOST_RID))"
	@echo "  make dmg-osx-arm64        DMG for Apple Silicon"
	@echo "  make dmg-osx-x64          DMG for Intel Mac"
	@echo "  make dmg-all              Both macOS DMGs"
	@echo ""
	@echo "Docker"
	@echo "  make docker               Build image for current arch → $(IMAGE)"
	@echo "  make docker-load          buildx --load one platform (PLATFORM=linux/arm64)"
	@echo "  make docker-multi         Multi-arch build+push (REGISTRY_IMAGE=…)"
	@echo "  make compose-up           docker compose up -d --build"
	@echo "  make compose-down         docker compose down"
	@echo ""
	@echo "Other"
	@echo "  make clean                Remove bin/obj and $(PUBLISH_DIR)/"
	@echo ""
	@echo "Overrides: CONFIG=Debug RID=osx-arm64 IMAGE=… REGISTRY_IMAGE=… PLATFORM=linux/arm64"

restore:
	dotnet restore $(PROJ) -f $(TFM)

build:
	dotnet build $(PROJ) -c $(CONFIG) -f $(TFM)

run:
	dotnet run --project $(PROJ) --framework $(TFM) -c $(CONFIG)

publish: publish-$(RID)

publish-osx-arm64 publish-osx-x64 publish-linux-x64 publish-linux-arm64:
	$(eval _RID := $(patsubst publish-%,%,$@))
	dotnet publish $(PROJ) \
		-c $(CONFIG) \
		-f $(TFM) \
		-r $(_RID) \
		--self-contained false \
		-o $(PUBLISH_DIR)/$(_RID) \
		/p:UseAppHost=true
	@echo "Published → $(PUBLISH_DIR)/$(_RID)"

# Unsigned self-contained .app + DMG (requires macOS). See scripts/macos/build-dmg.sh.
dmg:
	@test "$(UNAME_S)" = "Darwin" || (echo "make dmg requires macOS"; exit 1)
	CONFIG=$(CONFIG) $(DMG_SCRIPT) $(HOST_RID)

dmg-osx-arm64 dmg-osx-x64:
	@test "$(UNAME_S)" = "Darwin" || (echo "make $@ requires macOS"; exit 1)
	$(eval _RID := $(patsubst dmg-%,%,$@))
	CONFIG=$(CONFIG) $(DMG_SCRIPT) $(_RID)

dmg-all:
	@test "$(UNAME_S)" = "Darwin" || (echo "make dmg-all requires macOS"; exit 1)
	CONFIG=$(CONFIG) $(DMG_SCRIPT) all

# Plain docker build (daemon's native architecture).
docker:
	docker build -t $(IMAGE) .

# Single-platform buildx into the local daemon (--load cannot do multi-arch).
PLATFORM ?= linux/arm64
docker-load:
	docker buildx build --platform $(PLATFORM) -t $(IMAGE) --load .

# Multi-arch manifest; requires a registry (Docker Hub, GHCR, etc.).
docker-multi:
	@test "$(REGISTRY_IMAGE)" != "ywc:latest" || \
		(echo "Set REGISTRY_IMAGE to a pushable tag, e.g. REGISTRY_IMAGE=ghcr.io/you/ywc:latest"; exit 1)
	docker buildx build \
		--platform $(PLATFORMS) \
		-t $(REGISTRY_IMAGE) \
		--push .

compose-up:
	YWC_SERIAL_DEVICE=$(YWC_SERIAL_DEVICE) docker compose up -d --build

compose-down:
	docker compose down

clean:
	dotnet clean $(PROJ) -c $(CONFIG) -f $(TFM) || true
	rm -rf bin obj $(PUBLISH_DIR)
