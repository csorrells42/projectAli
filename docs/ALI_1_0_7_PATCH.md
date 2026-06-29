# Ali 1.0.7 Patch

Ali 1.0.7 expands the bundled source library so the assistant has a broader approved catalog for source-backed answers without changing the guarded retrieval model.

## Included

- Bumps Ali app, installer payload, and Visual Studio companion metadata to `1.0.7`.
- Expands the bundled curated source seed from `1001` to `2000` entries.
- Adds `999` carefully selected entries split evenly across nine areas:
  - weather
  - sports
  - local news
  - regional news
  - national news
  - international news
  - science
  - history and National Geographic
  - military history
- Keeps repair/install behavior additive: missing bundled sources are merged into the local catalog without deleting user-added entries.
- Adds regression checks that the delivered catalog contains at least `2000` sources and keeps broad coverage across the new categories.

## Local Sync

Chris's local Ali source catalog should be synced with this `2000`-entry bundled seed during patch validation so the development copy and installed copy stay aligned.
