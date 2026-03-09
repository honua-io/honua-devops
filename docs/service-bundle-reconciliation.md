# ServiceBundle Reconciliation

This document captures the baseline `ServiceBundle` reconciliation plan for `honua-devops#18`.

## Goal

`ServiceBundle` should represent declarative GIS operator intent, not just a manifest payload blob.

The operator therefore needs an explicit mapping from `ServiceBundle` intent to Honua control-plane surfaces.

## Reconciliation Strategy

Baseline strategy:

- use capability discovery to confirm what the target server currently supports
- use manifest export for desired-vs-actual state reads
- use manifest apply only for the metadata subset
- route connections, publishing, access policy, styles, imports, and long-running work through explicit control-plane reconciliation surfaces

This keeps metadata manifest handling from being mistaken for the full platform GitOps engine.

## Covered Surfaces

### Capability discovery

- read current control-plane capability shape
- confirm which reconciliation features exist versus which remain planned

### Desired state export

- export current service state for comparison and drift detection
- use export as the actual-state baseline for reconciliation evidence

### Metadata subset apply

- apply metadata-oriented manifest changes only where the manifest contract is the right fit

### Secure connections

- reconcile connection definitions declaratively
- support create, update, disable, delete, and drift semantics

### Publishing and service settings

- reconcile publish/unpublish state, protocol toggles, and service-level settings

### Access policy

- reconcile service-level access and policy posture declaratively

### Metadata and styles

- keep metadata and style reconciliation explicit rather than burying them in generic publish flows

### Imports and long-running operations

- model imports as long-running, replay-safe control-plane work
- capture operation handles and export status for later drift checks

## Semantics

The baseline reconciliation contract requires:

- idempotent create and update behavior
- explicit disable and delete semantics
- desired-vs-actual export for drift reporting
- replay-safe long-running operation handling

## Typed Drift And Export State

The current planner now emits explicit typed reconciliation state for:

- export mode
- long-running operation mode
- current state summary
- drift scopes for service state, metadata, and capability discovery
- per-surface drift command and diff summary

That means the operator can distinguish:

- what current-state source was used
- which surfaces are backed by existing APIs versus planned surfaces
- which drift command should be run for each reconciliation surface

## Current Implementation

The current `honua-devops` implementation now emits a typed ServiceBundle reconciliation plan as part of deploy planning.

That gives the operator:

- a concrete map of read and write surfaces
- explicit evidence requirements for service-state export and metadata drift checks
- explicit current-state and drift summaries per reconciliation surface
- a bridge between desired-state modeling and future full control-plane execution
