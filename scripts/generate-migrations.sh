#!/usr/bin/env bash
# Run this locally (requires the .NET 9 SDK and the dotnet-ef tool).
# Migrations are NOT checked into this repo: a hand-written migration file
# risks silently drifting from the real EF model snapshot, which is worse
# than no migration at all. This script generates a real one from your
# actual current model.
set -euo pipefail

if ! dotnet tool list --global | grep -q dotnet-ef; then
  echo "Installing dotnet-ef..."
  dotnet tool install --global dotnet-ef
fi

echo "Generating InitialCreate migration from the current model..."
dotnet ef migrations add InitialCreate \
  --project src/Veritas.Web \
  --startup-project src/Veritas.Web \
  --output-dir Migrations

echo "Applying migration to the database in ConnectionStrings:Postgres..."
dotnet ef database update \
  --project src/Veritas.Web \
  --startup-project src/Veritas.Web

echo "Done. Commit the generated src/Veritas.Web/Migrations/ folder."
