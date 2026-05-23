# Honua QGIS Plugin Demo Script

This script is the reviewable recording plan for the launch demo video.
It keeps the first public pass focused on the core analyst flow:
connect a Honua server, browse discovered layers, and add them to the QGIS
canvas.

## Audience And Outcome

- Persona: QGIS analyst evaluating whether Honua server layers can be used
  without a custom desktop workflow.
- Goal: Show that a clean QGIS profile can connect to a Honua server and
  load both vector and raster layers through native QGIS providers.
- Success criterion: A viewer can see the path from install to map canvas
  and understands that the day-one flow is URL plus API key, browse, then
  double-click to add.
- Target runtime: 2:45, with a hard ceiling of 3:00.
- Recorded flow: local demo stack only; no production tenant, customer data,
  personal account, or real API key appears on screen.

## Pre-Recording Environment Checklist

Complete this checklist before starting the screen capture.

- QGIS version: QGIS LTR 3.22 or later from the 3.x LTR channel; do not use a
  development build or QGIS 4 preview.
- Plugin package: freshly built `honua_qgis.zip` for the launch candidate
  being demonstrated.
- Demo server: local Honua server reachable at `http://localhost:8080` using
  API key `testkey`.
- Sample payload: seed the local server so discovery returns at least one
  OGC API Features collection named `parcels` or `sample parcels`, and at
  least one WMS layer named `basemap`, `imagery`, or another non-sensitive
  demo layer.
- QGIS profile name: `honua-demo`; create it only for this recording and
  clear any saved connections before the first take.
- Screen capture: 1920 x 1080 at 30 fps, single monitor, QGIS window
  maximized, browser and terminal windows closed unless the shot list calls
  for them.
- Display settings: light QGIS theme, default panel layout, 110% desktop font
  scale, readable cursor, no desktop notifications.
- Layer panel state: start with a blank project and no unrelated map layers.
- PII and secrets: hide menu bars, shell prompts, profile paths, browser
  bookmarks, account names, hostnames, and any real API keys. The only key
  shown is `testkey`.
- Audio and captions: capture silent video unless a voice-over track is
  explicitly scheduled; burn in the caption text from the shot list or keep
  it ready for post-production captions.

## Shot List

| Time | On-Screen Action | Voice-Over / Caption |
| --- | --- | --- |
| 0:00-0:05 | Title card: "Honua QGIS plugin - 3-minute tour" with the Honua plugin name and version. | "Honua brings server-side GIS layers into QGIS through native OGC providers." |
| 0:05-0:18 | Open QGIS with the clean `honua-demo` profile and a blank project. | "This is a clean QGIS profile with no saved Honua connections." |
| 0:18-0:35 | Open `Plugins -> Manage and Install Plugins -> Install from ZIP`, select `honua_qgis.zip`, and confirm the install. | "Install the launch candidate from a single plugin ZIP. There are no extra Python packages to install." |
| 0:35-0:45 | Enable the `Honua` plugin if QGIS prompts for enablement, then close the plugin manager. | "Once enabled, Honua appears under the Web menu and in the toolbar." |
| 0:45-1:10 | Choose `Web -> Honua -> Add Honua Server...`. Fill `Name` with `Local Honua Demo`, `Base URL` with `http://localhost:8080`, and `API key` with `testkey`. | "Add a server with the same details an analyst receives from an operator: a URL and an API key." |
| 1:10-1:25 | Click `Test connection`; wait for `Connection succeeded.` Then click `OK`. | "The test confirms that QGIS can reach the Honua OGC API endpoint before we save it." |
| 1:25-1:42 | Choose `Web -> Honua -> Show Layer Browser`. Show the `Honua` dock on the right side of the QGIS window. | "The layer browser discovers available collections and services from the saved server." |
| 1:42-2:00 | Expand `Local Honua Demo`, then expand `Vector (OGC API Features)`. Double-click the sample parcels collection. | "Vector collections load through QGIS's WFS provider in OGC API Features mode." |
| 2:00-2:18 | Show the new vector layer in the Layers panel and zoom to the visible features on the canvas. | "After the double-click, the layer is a regular QGIS layer that can be styled, filtered, and inspected." |
| 2:18-2:35 | In the `Honua` dock, expand `Raster (WMS)` and double-click the sample WMS layer. | "Raster layers use QGIS's built-in WMS provider, so analysts keep the workflows they already know." |
| 2:35-2:48 | Pan or zoom to show the vector layer over the WMS layer; briefly open the vector attribute table and close it. | "Honua handles discovery. QGIS handles the map, attributes, and analysis." |
| 2:48-2:55 | Outro card with `https://honua.io`, `plugins.qgis.org`, and the release download location. | "Get the plugin from the QGIS plugin registry or the Honua release page." |

## Re-Record Triggers

Re-record the video, not just the captions, when any of these changes land:

- The plugin menu path changes from `Web -> Honua`.
- The action labels change from `Add Honua Server...` or `Show Layer Browser`.
- The add-server dialog title, field labels, success message, or save/test
  behavior changes.
- The required connection fields change, including a move away from URL plus
  API key or a new required authentication step.
- The layer browser title, dock placement, column labels, or tree grouping
  changes from server -> `Vector (OGC API Features)` / `Raster (WMS)` -> layer.
- The user action for loading layers changes from double-clicking a layer row.
- The QGIS provider mapping changes from OGC API Features through `WFS` or WMS
  through `wms`.
- The supported QGIS baseline changes from QGIS 3.x LTR, or the plugin moves
  to a QGIS 4 / Qt 6-only release.
- The demo server seed data changes enough that the recorded layer names,
  geometry, or canvas result no longer match the narration.
- The public distribution location changes from the QGIS plugin registry or
  the launch release download.
