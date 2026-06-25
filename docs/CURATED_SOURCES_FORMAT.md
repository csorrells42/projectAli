# Curated Sources Format

Ali V1 source search uses an approved local source catalog instead of a paid search provider.

Place the live catalog here:

`%LOCALAPPDATA%\Ali\BootstrapData\Sources\curated_sources.json`

Use a JSON array:

```json
[
  {
    "id": "cdc-respiratory-viruses",
    "topic": "health",
    "name": "CDC Respiratory Viruses",
    "url": "https://www.cdc.gov/respiratory-viruses/",
    "type": "web",
    "trustLevel": "primary",
    "keywords": ["health", "respiratory", "cdc", "virus", "flu", "covid"],
    "notes": "Primary US public-health source.",
    "enabled": true
  }
]
```

Fields:

- `id`: stable unique key, lowercase words with hyphens.
- `topic`: broad bucket such as `health`, `weather`, `government`, `software`, `finance`, `local`.
- `name`: human-readable source name.
- `url`: direct HTTP/HTTPS page, feed, or source endpoint Ali is allowed to retrieve.
- `type`: `web`, `rss`, `docs`, `reference`, `nws-point-forecast`, or another short label.
- `trustLevel`: `primary`, `official`, `standard`, or `watch`.
- `keywords`: words Ali matches against the user question.
- `notes`: short human note about why this source exists.
- `enabled`: `true` to use it, `false` to keep it in the catalog but skip retrieval.

Behavior:

- Ali first asks the local model for a structured source plan: whether sources are needed, the intent, the topic, query terms, and preferred broad catalog topics.
- Ali retrieves matching enabled catalog URLs only when that plan says the current message needs live/source-backed information such as current news, scores, weather, prices, official guidance, or source checking.
- Stable general-knowledge prompts can be answered from the local model without forcing a source lookup.
- Ali does not use a paid/general search provider for this path.
- Ali injects retrieved excerpts into the local model request.
- Ali appends the exact checked source URLs to the answer.
- `nws-point-forecast` sources should use an official `https://api.weather.gov/points/{lat},{lon}` URL. Ali follows the returned NWS forecast URL and summarizes the first forecast periods as a source excerpt.
- If a source lookup was attempted but no approved source/excerpt matched, Ali should say the approved sources did not return enough information.
