> **Status:** approved

# Compose the erasure sweeps into one answerable operation

## Why

Crypto-shredding for erasure is the framework's headline capability, and there is no way to perform
an erasure.

Four separate sweeps exist — the membership directory, the key store, the settings store and the
API-key store — and nothing composes them. A consumer answering an erasure request has to know all
four exist, call each in the right order, and get the ordering right themselves: shredding the key
before sweeping the settings leaves rows nobody can read but which are still there; sweeping the
membership before the API keys leaves keys whose materialised membership is already gone.

There is no single entry point, no documented list of what a complete erasure covers, and no way for
a consumer to verify they did it all. Migration finding **TD-2**, which the backfill rates as the
most consequential gap it found.

## What Changes

- A composed erasure operation covering every plane that holds subject data, with a defined order.
- A statement of what it does **not** cover, which matters as much: read models built by a
  consumer's own projections, and anything in the event stream that is not protected by a scoped key.
- A new requirement in `tenant-directory`, because this is a capability gap against a guarantee the
  framework already advertises, not a defect in an existing behaviour.

## Impact

This is the one follow-up change that adds a capability rather than repairing one. It should be
sized and scheduled deliberately rather than folded into a maintenance wave.
