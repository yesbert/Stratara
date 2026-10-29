#!/usr/bin/env bash
#
# Adds a migration to the shared identity store (StrataraIdentityStore), the one migration lineage every
# host sharing the store applies. Run it after a change to the store's model — a directory table, or an
# ASP.NET Identity upgrade that changes its schema.
#
# Within a major version a migration may only add: IdentityStoreMigrationTests fails on a drop, a rename
# or an alteration.
#
# Usage:
#   ./scripts/add-identity-store-migration.sh <MigrationName>

set -euo pipefail

name="${1:?Usage: $0 <MigrationName>}"
root="$(cd "$(dirname "$0")/.." && pwd)"
project="$root/src/Stratara.EventSourcing.EntityFrameworkCore"
migrations="$project/IdentityStore/Migrations"

dotnet ef migrations add "$name" \
  --project "$project" \
  --startup-project "$project" \
  --context StrataraIdentityStore \
  --output-dir IdentityStore/Migrations \
  --namespace Stratara.EventSourcing.EntityFrameworkCore.IdentityStore.Migrations

# EF places the model snapshot by its namespace, not by --output-dir.
stray="$project/Stratara/EventSourcing/EntityFrameworkCore/IdentityStore/Migrations/StrataraIdentityStoreModelSnapshot.cs"
if [[ -f "$stray" ]]; then
  mv "$stray" "$migrations/"
  rm -r "$project/Stratara"
fi

# Inside Stratara.EventSourcing.EntityFrameworkCore, "Migration" names the namespace of the migration
# utility; the generated base class has to say which Migration it means.
for file in "$migrations"/*_"$name".cs; do
  [[ "$file" == *.Designer.cs ]] && continue
  sed -i.bak 's/ : Migration$/ : Microsoft.EntityFrameworkCore.Migrations.Migration/' "$file"
  rm "$file.bak"
done

echo "Added $name to $migrations"
