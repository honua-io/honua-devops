# Client Compatibility Scoreboard

This is the baseline implementation for `honua-devops#6`.

## Source Of Truth

The scoreboard consumes the existing `Honua.Server` client-pack shape:

- `evidence/session.json`
- generated GIS and BI starter artifacts from `scripts/prepare-client-compatibility-pack.sh`

To cover the full eight-client matrix, each pack can also carry `compatibility-results.json` with protocol-level verdicts for the automated clients and web-library targets.

## Generator

Generate the static scoreboard assets with:

```bash
python3 scripts/generate-client-compat-scoreboard.py \
  --packs-root compatibility/releases \
  --catalog compatibility/clients.catalog.json \
  --output-dir compatibility/scoreboard
```

Generated outputs:

- `compatibility/scoreboard/compatibility-matrix.json`
- `compatibility/scoreboard/compatibility-matrix.md`
- `compatibility/scoreboard/index.html`
- `compatibility/scoreboard/compatibility-changes.xml`
- `compatibility/scoreboard/badge.json`

## Current Matrix Coverage

The catalog covers:

- ArcGIS Pro
- QGIS
- Power BI
- Excel
- MapLibre GL JS
- OpenLayers
- Leaflet
- Python (GeoPandas)

Each client maps to the protocol subset that matters for that client rather than pretending all clients consume all surfaces.

## Release Blocking

Use `--hard-fail` to block a release when the latest generated release contains any failing client/protocol verdict:

```bash
python3 scripts/generate-client-compat-scoreboard.py \
  --packs-root compatibility/releases \
  --catalog compatibility/clients.catalog.json \
  --output-dir compatibility/scoreboard \
  --hard-fail
```

Exit code `2` means the scoreboard found a release-blocking compatibility failure.

## Publishing

`.github/workflows/client-compatibility-scoreboard.yml` runs the generator on pull requests and pushes, uploads the generated static site artifact, and deploys the HTML scoreboard to GitHub Pages on `main` and release events.

That gives the repo a public scoreboard page, JSON feed, RSS feed, and badge artifact without needing a separate publishing service for the initial baseline.
