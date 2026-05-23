# Honua QGIS Plugin Demo Script

This script is the reviewable recording plan for the first public Honua GIS
Assistant demo. It keeps the pass focused on the current `0.1.0` source-preview
scope: install the plugin, open the local-first assistant panel, refresh local
Ollama models, run a bounded vector-layer query, and show the privacy controls.

Canonical public page: <https://honua.io/qgis-plugin.html>
Source repo: <https://github.com/honua-io/honua-qgis-plugin>

## Audience And Outcome

- Persona: QGIS analyst evaluating a private/local GIS assistant workflow.
- Goal: Show that a clean QGIS profile can install Honua GIS Assistant, keep
  generation local through Ollama, and inspect a bounded query result without
  sending telemetry to Honua.
- Success criterion: A viewer can see the path from install to right-docked
  panel, model refresh, prompt send/cancel, query preview, and local audit
  controls.
- Target runtime: 2:45, with a hard ceiling of 3:00.
- Recorded flow: local demo stack only; no production tenant, customer data,
  personal account, or real API key appears on screen.

## Pre-Recording Environment Checklist

Complete this checklist before starting the screen capture.

- QGIS version: QGIS 3.34 or newer; do not use a development build or QGIS 4
  preview.
- Plugin package: freshly built `dist/honua_gis_assistant-<version>.zip` from
  `python3 scripts/package.py --check`, or a GitHub Release ZIP once the
  release owner publishes one.
- Local model server: Ollama running on `http://127.0.0.1:11434` with
  `qwen2.5-coder` installed, unless the Honua-GIS beta model has a published
  local tag for this recording.
- Sample QGIS project: non-sensitive fixture layer named `Parcels` or
  `Sample Parcels` with a few fields suitable for a bounded `query` bridge
  demo. Do not use customer data.
- QGIS profile name: `honua-demo`; create it only for this recording and
  clear any saved plugin settings before the first take.
- Screen capture: 1920 x 1080 at 30 fps, single monitor, QGIS window
  maximized, browser and terminal windows closed unless the shot list calls
  for them.
- Display settings: light QGIS theme, default panel layout, 110% desktop font
  scale, readable cursor, no desktop notifications.
- Layer panel state: start with only the sample layer needed for the query
  bridge, or load it on camera from a local fixture.
- PII and secrets: hide shell prompts, profile paths, browser bookmarks,
  account names, hostnames, and any real API keys. Do not enable a remote
  endpoint during the public recording.
- Audio and captions: capture silent video unless a voice-over track is
  explicitly scheduled; burn in the caption text from the shot list or keep
  it ready for post-production captions.

## Shot List

| Time | On-Screen Action | Voice-Over / Caption |
| --- | --- | --- |
| 0:00-0:05 | Title card: "Honua GIS Assistant - 0.1.0 early preview" with the Honua plugin name and version. | "Honua brings a local-first GIS assistant panel into QGIS." |
| 0:05-0:18 | Open QGIS with the clean `honua-demo` profile and a blank project. | "This is a clean QGIS profile with no saved Honua connections." |
| 0:18-0:35 | Open `Plugins -> Manage and Install Plugins -> Install from ZIP`, select `honua_gis_assistant-<version>.zip`, and confirm the install. | "Install the early preview from a single plugin ZIP. Marketplace approval is still pending." |
| 0:35-0:45 | Enable **Honua GIS Assistant** if QGIS prompts for enablement, then close the plugin manager. | "Once enabled, the assistant appears in the Plugins menu and toolbar." |
| 0:45-1:00 | Choose `Plugins -> Honua GIS Assistant -> Show Panel`, or click the toolbar action. Show the right-docked panel. | "The panel stays inside QGIS and starts in local mode." |
| 1:00-1:18 | Confirm the Ollama URL is `http://127.0.0.1:11434`, then click **Refresh**. | "The plugin probes the configured Ollama endpoint on a background task." |
| 1:18-1:35 | Show the model selector populated with Honua-GIS if available, otherwise `qwen2.5-coder`. | "The preview prefers Honua-GIS when installed and falls back to qwen2.5-coder." |
| 1:35-1:55 | Load or show the sample `Parcels` layer. Send a prompt asking for a bounded summary or query preview. | "The v0 bridge exposes a constrained vector-layer query tool, not arbitrary project mutation." |
| 1:55-2:15 | Show the streamed response and any result preview. Use **Cancel** only if the request is intentionally still running. | "Generation streams without blocking the QGIS interface." |
| 2:15-2:32 | Toggle or show the **Local audit JSONL** control and resolved path. Keep it disabled unless the recording needs an audit proof. | "Audit records are local, inspectable, and off unless the user enables them." |
| 2:32-2:45 | Briefly open the options page and show the remote endpoint section disabled. Do not enter a token. | "Remote endpoints are optional and off by default; enabling one changes the privacy boundary." |
| 2:45-2:55 | Outro card with `https://honua.io/qgis-plugin.html` and `github.com/honua-io/honua-qgis-plugin`. | "Get source and release status from the public landing page; ZIP and marketplace publishing remain release-owner steps." |

## Re-Record Triggers

Re-record the video, not just the captions, when any of these changes land:

- The plugin menu path changes from `Plugins -> Honua GIS Assistant -> Show Panel`.
- The dock title, Ollama URL field, **Refresh**, **Send**, **Cancel**, model
  selector, or local audit controls change.
- The minimum supported QGIS version changes from 3.34+.
- The model preference changes from Honua-GIS first and `qwen2.5-coder`
  fallback.
- The tool bridge expands beyond the bounded `query` tool or starts mutating
  QGIS layers in the public demo flow.
- Remote endpoints become enabled by default or move out of the QGIS options
  page.
- A GitHub Release ZIP, QGIS marketplace approval, screenshots, or demo video
  publishes; update the outro and install copy to the actual artifact.
- The public landing page changes from `https://honua.io/qgis-plugin.html`.
