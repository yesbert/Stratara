> **Status:** approved

# Write the documentation the backfill found missing

## Why

The backfill enumerated the published surface and mapped it onto the documentation site. Three
capabilities have published API and **no documentation page at all**, and several documented ones
omit the thing a reader most needs.

| # | Gap |
|---|---|
| **D-1** | `event-schema-evolution` (17 public types), `resilience` (6) and `update-change-tracking` (18) have no page. Until this backfill, their specs were the first consumer-facing description of them |
| **PR-1** | A projection replay truncates **every** read model before rebuilding, with no confirmation, no dry run and no per-projection scope — a full read-side outage for the duration. Documented nowhere as an operational hazard |
| **UC-2** | An update command must carry every field it could change: an absent value is a deliberate clear, so a partial update silently clears everything it omits. A real constraint on consumer command shapes |
| **TE-1** | The anchor interval, chaining batch size and settling delay are private constants. The anchor interval is consumer-visible in effect — it sets the submission rate of any external-anchoring integration |
| **AK-1** | API keys record no last-used time, so an operator cannot answer "is this key still in use" before revoking it |
| **EI-2** | A just-in-time provisioned user has no tenant membership, so their first session resolves to the reserved default tenant — correct, and surprising, and unexplained |
| **AK-2** | A machine key's membership row carries the key's identity in the user column, and nothing marks such rows |
| **O-2** | `ConfigureSerilog` deletes a log file under the system temp directory at start-up in Development |
| **P-2** | A repository-global NuGet audit suppression scoped wider than the exposure it covers |

## What Changes

Documentation only. Each page is written **from** the corresponding spec, which is now the source —
see the derivation header the migration added to `docs/`.

No behaviour changes, no requirement changes.
