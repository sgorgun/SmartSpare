#!/usr/bin/env bash
# =============================================================================
# Builds the container image of every deployable host (STAGE_4_PLAN 8.1): the gateway, the six module services and the
# migration job (ADR 0020). SDK container publishing, no Dockerfiles; the base image, repository names and the default
# tag come from src/Bootstrapper/Directory.Build.props.
#
#   tools/build-images.sh                 # into the local Docker daemon, tag "dev"
#   TAG=<sha> REGISTRY=<acr>.azurecr.io tools/build-images.sh gateway inventory   # selected images, pushed (stage 10)
# =============================================================================
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

declare -A PROJECTS=(
  [gateway]=src/Bootstrapper/SmartSpare.Gateway
  [identity]=src/Bootstrapper/Services/SmartSpare.Services.Identity
  [inventory]=src/Bootstrapper/Services/SmartSpare.Services.Inventory
  [fleet]=src/Bootstrapper/Services/SmartSpare.Services.Fleet
  [procurement]=src/Bootstrapper/Services/SmartSpare.Services.Procurement
  [forecasting]=src/Bootstrapper/Services/SmartSpare.Services.Forecasting
  [reporting]=src/Bootstrapper/Services/SmartSpare.Services.Reporting
  [migrations]=src/Bootstrapper/SmartSpare.MigrationRunner
)
ORDER=(gateway identity inventory fleet procurement forecasting reporting migrations)

IMAGES=("$@")
[ ${#IMAGES[@]} -eq 0 ] && IMAGES=("${ORDER[@]}")

ARGS=(--configuration Release -t:PublishContainer -p:ContainerImageTags="${TAG:-dev}")
[ -n "${REGISTRY:-}" ] && ARGS+=(-p:ContainerRegistry="$REGISTRY")

for image in "${IMAGES[@]}"; do
  project="${PROJECTS[$image]:-}"
  if [ -z "$project" ]; then
    echo "unknown image: $image (images: ${ORDER[*]})" >&2
    exit 2
  fi
  echo "== $image ($project)"
  dotnet publish "$project" "${ARGS[@]}"
done

echo "== OK: ${IMAGES[*]}"
