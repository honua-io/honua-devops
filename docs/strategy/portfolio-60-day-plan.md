# Honua portfolio — 60-day strategic plan

> ## ⚠️ SUPERSEDED — historical snapshot, do not plan from this document
>
> This plan's 60-day window opened 2026-05-21 and **closed ~2026-07-20**. Its
> status was last reconciled 2026-05-23; every repo row, issue count, and
> "current" statement below reflects the 2026-05-20 scan and is stale.
>
> **The current program is the 2026.1 terminal AI delivery arc**, tracked in
> honua-io/honua-release#120 (install -> services + GP -> maps/dashboards ->
> governed publish), with the corrective successor cut labelled
> `release/2026.1.1`.
>
> **The current gates in this repo** (honua-devops) are #147, #148, #150, and
> #152 — not the honua-devops row in §2/Appendix A below, which is wrong.
>
> Everything below is retained unedited as a record of what was believed in
> May 2026. It is not maintained.

**Status:** SUPERSEDED (window closed ~2026-07-20) — original status line:
execution tracker, backlog filed, status reconciled 2026-05-23
**Date:** 2026-05-21
**Scope:** all 15 active Honua repos (excluding worktrees)
**Inputs:** live capability inventory + open-issue scan completed 2026-05-20
**Filed epics:** see [§4 Phase 1](#phase-1--weeks-1-2-unblock--label-hygiene) and [Appendix C](#appendix-c--filed-backlog-and-promotions) for live issue links
**Execution update:** 2026-05-23 — the QGIS plugin source repo and static landing/status page are live. honua-server #965 is closed after P0 promotion; #1096/#892/#352 label promotions are recorded in Appendix C. GitHub Release ZIP, QGIS Plugin Repository approval, screenshots, and demo video remain release-owner follow-ups.

---

## Table of contents

1. [Executive summary](#1-executive-summary)
2. [Where the portfolio actually stands](#2-where-the-portfolio-actually-stands)
3. [Strategic priorities vs. backlog alignment](#3-strategic-priorities-vs-backlog-alignment)
4. [The 60-day push](#4-the-60-day-push)
5. [Five strategic gaps requiring new epics](#5-five-strategic-gaps-requiring-new-epics)
6. [Three things to downgrade or punt](#6-three-things-to-downgrade-or-punt)
7. [The compliance / FedRAMP mismatch](#7-the-compliance--fedramp-mismatch)
8. [What's already shipped that the marketing surface doesn't claim](#8-whats-already-shipped-that-the-marketing-surface-doesnt-claim)
9. [Founder decisions](#9-founder-decisions)
10. [Open questions](#10-open-questions)
11. [Appendix A — Top issues by repo](#appendix-a--top-issues-by-repo-2026-05-20-scan-baseline)
12. [Appendix B — Strategic-gap epic shapes](#appendix-b--strategic-gap-epic-shapes)
13. [Appendix C — Filed backlog and promotions](#appendix-c--filed-backlog-and-promotions)

---

## 1. Executive summary

The platform is materially more built than the public narrative claims; the backlog and the marketing surface lag behind the shipped capability. The next 60 days should be spent **packaging, sequencing, and proving** what's already on the floor — not chasing more net-new platform surface.

**Three findings drive every recommendation in this document:**

1. **honua-server is production-shaped already.** 1,015 .NET test files, 40 CI workflows including OGC CITE conformance, a complete geoprocessing workflow engine, an MCP server with ground→clarify→plan→execute tools, an NL→FilterPlan compiler with both deterministic and LLM providers, a 3D scene pipeline, a declarative `Spec` language, and a first-class Esri migration-evidence pipeline. The category claim ("AI-native open-source-friendly spatial platform") is supportable today.

2. **The 2026-05-20 backlog scan over-indexed on enterprise plumbing and under-indexed on the differentiated narrative.** 33 open issues on honua-server, of which 15 were P3/P4/GA enterprise wishlist items (SAML, SCIM, SIEM, RBAC, multi-tenancy, plugin SDK, federated queries) — defensible deferral, but it crowded out the items that would actually create market pull. At scan time, zero open issues mentioned the open-weights GIS LLM, a QGIS plugin, NIM integration, GovCloud, or specific SLG customer segments. FedRAMP / SOC 2 sat at P4/GA, which was incompatible with the SLG GTM thesis.

3. **Zero customer-reported issues across 102 open tickets.** Every issue's author is `mikemcdougall`. Every strategic priority is currently a hypothesis. The single largest unblock for *future* prioritization is signing the first paid pilot (sales #47) — until that happens we are guessing.

**The original 60-day plan:** unblock the cloud-staging chain (week 1-2), ship the Esri migration proof package (week 3-6), land the marketplace path (week 7-9), and convert the first pilot while publishing open AI assets (week 10-12). Appendix C records the backlog filing and label-promotion actions that have already executed.

**The biggest single ask in the scan baseline:** treat **honua-server #965 (TLS cert misconfig)** as P0/stop-the-line. It was blocking SDK staging smoke, terraform live validation, and the proof chain that supports both Inception and the marketplace launch; Appendix C records its P0 promotion and 2026-05-22 closure.

---

## 2. Where the portfolio actually stands

**Historical scan baseline (2026-05-20):** 15 active repos, 102 open issues, zero stale (>6 months), zero customer-reported, zero P0 labels in use at scan time.

### Activity heat map

| Tier | Repos | Posture |
|---|---|---|
| **Crown jewel (production-shaped)** | honua-server | The platform. 1,015 .NET tests, 40 CI workflows, broad protocol matrix, first-class migration evidence. |
| **Released alpha** | honua-sdk-js, honua-sdk-dotnet, honua-sdk-python | Three language SDKs, all Apache-2.0, all on staging release trains |
| **Active product surfaces** | honua-portal, honua-mobile, honua-devops | UX, mobile, AI operator — each independent product |
| **Stable infrastructure** | honua-helm, honua-iac | Deployment substrate |
| **Skeleton / commercial packaging** | honua-marketplace | Bootstrap stage; blocks the marketplace path |
| **Internal tooling** | honua-agentflow, honua-support | Dev orchestration + support API |
| **Content** | honua-sales, honua-site | GTM docs + marketing surface |
| **Deprecated** | honua-mobile-sdk | 3 commits, superseded by honua-mobile |

### What's surprisingly strong

These are capabilities the platform *has* that the recent strategic conversation didn't fully account for:

- **Geoprocessing is a real workflow engine.** `BuiltInProcessCatalog` + `GeoprocessingJobService` + 13 concrete executors (Buffer, Intersect, Union, Dissolve, Clip, Centroid, ConvexHull, Project, Simplify, Snap, Length, Area) + gRPC `HonuaProcessService` + REST `GPServerEndpoints` + 31 test files. Retention/admission/destructive-classification/migration-evidence-classification all wired.
- **Grounding + MCP layer already exists.** `IGroundingService` + `DeterministicGroundingEngine` + MCP tools (`GroundCandidatesTool`, `ClarifyIntentTool`, `PlanAnalysisTool`, `DryRunPlanTool`, `ValidatePlanTool`, `ExecutePlanTool`, `CancelJobTool`). This is the NL→intent→plan→execute pipeline the recent conversation was about to recommend we build. It's already shipped.
- **`Spec` is a declarative service-definition language** with grammar, canonicalization, operators, validation, SSE-streaming apply. 47 files. "Terraform for GIS." Not in the marketing surface.
- **Migration-as-first-class-feature.** Server already has `MigrationScannerEndpoints`, `MigrationRunAdminEndpoints`, `MigrationPerformanceEvidenceEndpoints`, `ArcGisMigrationEvidenceEndpoints`, `ProcessMigrationEvidenceClassifier`, `CrossServerConsumeProbeEndpoints`. Recent commits reference "ArcGIS migration fidelity slices" and "OGC evidence packs + pyqgis lane fix".
- **3D scene pipeline.** `Scene` namespace with `TilesetDocumentWriter`, `EcefCoordinateTransform`, `GeometryTileBuilder`. Working 3D tileset emission for Cesium-compatible viewers.
- **Multi-DB FeatureStore.** `FeatureDataProviderRegistry` routes queries across PostGIS, DuckDB, SQL Server, and MySQL. "Bring your own spatial DB" is supportable today.
- **Real edition gating.** `Licensing/FeatureCatalog.cs` + `ILicenseEntitlementService` already model community/pro/enterprise edition tiers in production code.
- **AiBuilder server surface exists, fixture-backed today.** `Honua.Server/Features/AiBuilder/` with `FixturePlanAnalysisService` — the model-free GTM proof. Slot for real LLM-backed planning when we're ready.

### What's gappy in the 2026-05-20 scan

- **Zero customer telemetry.** Pre-pilot stage; sales #14 (VoC) was promoted to MVP on 2026-05-18.
- **Cloud-staging proof chain was broken at scan time** by server #965 (TLS cert). That blocker is now closed; downstream validation remains tracked from Appendix C.
- **honua-marketplace is structure-only** — five P1 MVP issues all filed 2026-05-05, none shipped.
- **Compliance was P4 at scan time**; Appendix C records the P1 promotion and the honua-sales #48 execution umbrella.
- **Esri-parity table-stakes are P3/P4/GA** — see §3.
- **Open-weights LLM, QGIS plugin, NIM integration, GovCloud were missing from the backlog at scan time** — see §5 and Appendix C for the filed execution tracks.

---

## 3. Strategic priorities vs. backlog alignment

This scorecard answers: *for each strategic priority we've articulated, does the reconciled backlog actually execute on it?*

| Strategic priority | In backlog as | State | Notes |
|---|---|---|---|
| **AWS + Azure marketplace launch** | marketplace #1-5, sales #33/#34/#42, terraform #1/#2, helm #5, devops #41 | **STRONG** | Dominant theme. ~11 well-coordinated issues. marketplace #4 (license activation) blocks #5 (first deploy proof). |
| **Esri migration tooling (paid)** | sdk-python #59 (P1, ArcPy scanner), server #1096 (P1), site #9 (P1 proof hub) | **STRONG but uncoordinated** | Three load-bearing pieces in three repos with no shared release plan. #1096 now has the P1 label and remains open for execution. |
| **Esri open assessment funnel** | [honua-sdk-python#62](https://github.com/honua-io/honua-sdk-python/issues/62), [`honua-esri-assess`](https://github.com/honua-io/honua-esri-assess) | **REPO LIVE** | Dedicated repo exists and the sdk-python parent tracker is closed with redirect. |
| **AI Studio / ArcGIS Pro alternative** | server #892 (promoted P1/closed), portal #5 (app builder, P3/GA), portal #1/#6 (catalog/open-data, P3) | **PARTIALLY PROMOTED** | Server contract work was promoted and closed; the portal-facing Pro-alternative surface remains P3. |
| **Open-weights GIS LLM (Honua-GIS-32B)** | [honua-sdk-python#64](https://github.com/honua-io/honua-sdk-python/issues/64), [`honua-gis-llm`](https://github.com/honua-io/honua-gis-llm) | **REPO LIVE** | Dedicated model/eval repo exists; production fine-tune, NIM container, baseline evals, and announcement work continue in child tickets. |
| **QGIS plugin** | [honua-sdk-python#65](https://github.com/honua-io/honua-sdk-python/issues/65), [`honua-qgis-plugin`](https://github.com/honua-io/honua-qgis-plugin), [§8.7 landing/status page](#7-honua-gis-assistant-qgis-plugin) | **LIVE PREVIEW** | Source repo and static page are live; GitHub Release ZIP, QGIS marketplace approval, screenshots, and demo video remain pending. |
| **NVIDIA Inception / NIM / NeMo** | [honua-devops#46](https://github.com/honua-io/honua-devops/issues/46), `docs/deployments/nvidia-nim.md` | **SHIPPED FOUNDATION** | NIM/OpenAI-compatible provider lane is documented; hosted/self-hosted evidence and Honua-GIS NIM follow-ups continue under the model repo. |
| **State/local government GTM** | sales #37 (CivStart) | **THIN** | One issue. No DOT / utility / city / state-agency-named issues anywhere. |
| **GovCloud / on-prem / air-gapped** | — (helm #5 is enabler) | **GAP** | Strategy claims this; backlog doesn't. |
| **FedRAMP / SOC 2 / CMMC** | server #352 (promoted P1/closed), sales #48 | **COMMITTED; EXECUTION UMBRELLA FILED** | SLG market thesis requires this long-lead program; honua-sales #48 now coordinates the active procurement-readiness lane. See §7. |
| **Esri feature-parity (3D, versioned editing, plugin SDK, multi-tenancy, federation, Kafka/NATS, RLS, SCIM/SAML/SIEM)** | server #341, #346, #347, #348, #349, #350, #354, #355, #357, #371, #502, #504, #507, #508, #509, #510, #530 | **PARKED** | 15 issues at P3/P4. Defensible *as a phase-gate*; not defensible if we're claiming feature parity today. |
| **Production-stability for the staging proof chain** | server #965 (TLS, closed), terraform #24 (VPC quota), devops #41, sdk-python #53, terraform #22/#23 (DR drills) | **TLS UNBLOCKED; VALIDATION CONTINUES** | #965 was promoted to P0 and closed 2026-05-22; VPC quota, release-candidate validation, SDK staging config, and DR-drill follow-ups remain active. |
| **First paid pilot + voice of customer** | sales #14, sales #47 | **EARLY** | Trigger milestone exists; signal does not. |

### Reading the scorecard

**Strong areas:** marketplace launch, migration-tool components (uncoordinated), platform stability (TLS unblock resolved; validation follow-ups continue).

**Strategic-gap tracks from the scan:** open-weights model, QGIS plugin, NIM, GovCloud, compliance. Appendix C records the filed issues and later execution updates for the gaps that have moved.

**Under-prioritized for strategy:** AI Studio (P2/P3 instead of P1), migration coordination (no shared epic), customer-pain signal (no pilot yet).

**Over-investment risk if we don't course-correct:** the 15-issue enterprise wishlist on honua-server can quietly eat mental energy at standups. Recommend grouping under one umbrella epic labeled "post-first-pilot" with explicit do-not-start criteria.

---

## 4. The 60-day push

A coordinated four-phase sequence. Each phase has one strategic outcome, not a feature list.

### Phase 1 — Weeks 1-2: Unblock + label hygiene

**Outcome:** the cloud-staging proof chain runs green end-to-end, and the backlog reflects the strategy.

**Action status:** items 1-5 below are reconciled in Appendix C; item 6 remains a recommendation until an umbrella issue is filed.

1. **Resolved 2026-05-22: promote honua-server #965 to P0/stop-the-line and fix it.** The cert misconfig blocked `api.honua.io` and `staging-api.honua.io` from presenting valid TLS, which blocked SDK staging smoke, terraform live validation, and the cloud-staging proof that the marketplace listings need. Appendix C records the P0 promotion; the issue is now closed.
2. **Resolved 2026-05-21: add a priority label to honua-server #1096** (ArcGIS Pro licensed evidence workflow) — promoted to P1 with a 30-day deadline. This is the foundation of the migration narrative.
3. **Resolved 2026-05-21: file the five strategic-gap epics** (see §5 for shapes):
   - Honua-GIS-32B open-weights model
   - QGIS plugin
   - `honua-esri-assess` open assessment tool
   - NVIDIA NIM integration in honua-devops
   - `honua-gp` compatibility shim
4. **Resolved 2026-05-21: promote honua-server #892** (AI app builder contract) from P2 to P1.
5. **Resolved 2026-05-21: file the FedRAMP / SOC 2 readiness epic** — see §7 and Appendix C; active execution is coordinated from honua-sales #48.
6. **Pending recommendation: group enterprise GA wishlist** under one umbrella issue "Enterprise GA — post-first-pilot" with explicit do-not-start criteria; remove individual P4 issues from standing review.

**Justification:** the cost of these moves is paper-thin (labels, epic creation, one bug fix). The benefit is the entire 60-day plan becoming legible in GitHub — every priority becomes a clickable epic with sub-issues, instead of a strategy memo nobody opens.

**Risk if skipped:** the strategic-gap items keep being held in conversation only. The "we'll get to it" tax compounds.

### Phase 2 — Weeks 3-6: Ship the Esri migration proof package

**Outcome:** there is a real artifact a prospective customer can read and a real demo a salesperson can show that proves migration is possible.

**Actions:**

1. **Coordinate sdk-python #59 + honua-server #1096 + honua-site #9 as a single release.** Branding: "Honua Esri Migration Proof v1." These three are the migration story.
2. **Run the licensed ArcGIS Pro evidence workflow against the seeded test data** (#1096) and publish results.
3. **Ship the `arcpy` migration scanner** (#59) with a basic CLI that produces a `MigrationReadinessReport.json` and a markdown report.
4. **Build honua-site #9 competitive proof hub** as the public-facing surface: benchmark table, feature compatibility matrix, sample migration report, reference architecture.
5. **Concurrent:** start the `honua-esri-assess` open-source repo (the lead-gen funnel) with an `EsriFootprint.json` schema and the AGOL Portal API scanner.

**Justification:** the migration story is the only thing that unblocks the SLG sales motion. Customers will not switch from Esri without a credible migration path. The market sentiment research confirmed every value prop maps to a specific Esri pain point, and "migrating our legacy data would cause catastrophic downtime" is the single biggest objection in procurement reviews. The migration proof package is the answer.

**Why coordinate them:** today each repo's piece will ship on its own cadence. A coordinated v1 release lets us write one blog post, give one demo, and produce one sales artifact rather than fragments.

**Risk if skipped:** the Inception application reads as "we have a vision"; with the proof package it reads as "we have a working tool with measured results."

### Phase 3 — Weeks 7-9: Land the marketplace path

**Outcome:** Honua is listed and purchasable on AWS Marketplace and Azure Marketplace; one customer-operated deployment runs end-to-end.

**Actions:**

1. **honua-marketplace #1-5 in sequence.** AWS listing (#1), Azure listing (#2), private-offer assets (#3), license/entitlement activation path (#4), first marketplace deploy proof (#5).
2. **honua-helm #5** completed to operator-handoff state — chart contract, upgrade safety, smoke validation.
3. **honua-iac #1 + #2** marketplace IaC published.
4. **honua-iac #22 + #23** (backup/restore + RTO/RPO drills) ship as procurement-readiness artifacts.

**Justification:** marketplace listings are the only credible distribution channel that bypasses the SLG procurement-vehicle problem. AWS Marketplace can be purchased via existing GSA contracts; Azure Marketplace via existing Microsoft state agreements. Without these, every sale is a 12-month contract-vehicle fight. With them, the contract-vehicle fight is resolved before we start the conversation.

**Risk if skipped:** every direct-sale customer hits a procurement wall. We become a 24-month-cycle company instead of a 6-month-cycle company.

### Phase 4 — Weeks 10-12: First pilot + open AI assets

**Outcome:** one paying pilot, and the open-source AI presence that makes Honua the obvious "open spatial AI" choice.

**Actions:**

1. **Convert one design partner** via the migration proof + marketplace deployment.
2. **Publish Honua-GIS-32B alpha** to Hugging Face — fine-tuned Qwen 2.5 Coder, eval benchmark, Ollama publish, NIM container.
3. **QGIS plugin v0.1.0 source preview live 2026-05-23.** Current scope covers local LLM via Ollama with Honua-GIS model preference, bounded PyQGIS query bridge, and the static landing/status page. Release ZIP and QGIS marketplace path remain release-owner follow-ups.
4. **Open the `honua-esri-assess` repo** (Apache 2.0) — assessment schema, AGOL Portal scanner, `.gdb` reader (reuses honua-server `FileGdbReader`), report generator.

**Justification:** by week 10 the platform proof, the marketplace path, and the migration tool exist. What's missing is **distribution to the audience that's predisposed to switch**: developers and analysts who already use open-source tooling, GIS practitioners outside the US-DOT segment, academic users, sovereignty-conscious international buyers. The open AI assets reach them at zero CAC.

**Why this is the right time for the open assets, not earlier:** before we have the migration proof and a paying customer, "open AI for GIS" is a noise signal. After, it's a credibility signal that compounds. We become the company you can both pay (migration product) and use for free (open model + plugin + assessment).

**Risk if skipped:** we have a paid product but no funnel. The migration story reaches the customers who already heard of Honua; the open assets reach the ones who haven't.

### Sequencing logic

```
Week 1-2:   UNBLOCK    — cert fix, label hygiene, file gap epics
Week 3-6:   PROVE      — migration proof package v1 ships
Week 7-9:   DISTRIBUTE — AWS + Azure marketplace listings live
Week 10-12: COMPOUND   — first pilot signed + open AI assets published
```

Each phase's deliverable becomes the next phase's input. Phase 1 unblocks Phase 2's CI chain. Phase 2's proof package is what Phase 3's marketplace listings link to. Phase 3's listings are where Phase 4's first pilot transacts. Phase 4's pilot generates the first audit traces that feed the second Honua-GIS-32B fine-tune iteration.

---

## 5. Five strategic gaps requiring new epics

These are the items we explicitly committed to in strategy conversations that had **zero representation in any GitHub repo at the 2026-05-20 scan**. Appendix C records the filed epics and later execution status as those gaps move.

| # | Gap | Suggested repo | Suggested epic shape |
|---|---|---|---|
| 1 | **Honua-GIS-32B open-weights LLM** | new repo `honua-gis-llm` (or under honua-sdk-python) | sub-issues: training corpus design, GIS-Workflow-Eval-2026 benchmark, LoRA training pipeline, Hugging Face model card, Ollama upstream PR, NIM container build, eval vs GPT-4o/Claude/vanilla Qwen, distribution channels documentation |
| 2 | **QGIS plugin** | `honua-qgis-plugin` | sub-issues: plugin manifest, bounded PyQGIS bridge, local Ollama detection + Honua-GIS model preference, optional remote NIM/OpenAI fallback, audit JSONL local logging, QGIS plugin marketplace submission, distribution outside marketplace |
| 3 | **`honua-esri-assess` (open assessment)** | new repo `honua-esri-assess` (Apache 2.0) | sub-issues: `EsriFootprint.json` schema, AGOL Portal Sharing API scanner, ArcGIS Server REST scanner (anonymous + token), `.gdb` reader (reuses honua-server `FileGdbReader`), report generator, license entitlement enumeration (legitimate read-only), CLI + GitHub Action distribution |
| 4 | **NVIDIA NIM integration** | issue in honua-devops | wire `ProviderKind.LocalLlama` (~30 LoC), add `HONUA_DEVOPS_NIM_*` env vars, smoke test against `build.nvidia.com` hosted NIMs, write `docs/deployments/nvidia-nim.md`, validate against a NIM-hosted Llama-3.3 or Nemotron |
| 5 | **`honua-gp` compatibility shim** | new sub-package in honua-sdk-python (or new repo) | sub-issues: top-20 `arcpy.management.*` shim, top-15 `arcpy.analysis.*` shim, top-10 `arcpy.da.*` cursor shim, dispatch to Honua REST/gRPC via existing sdk-python client, audit JSONL capture, eval against representative DOT script |

**Justification for filing all five at once:** these are interdependent. The open model (#1) needs the QGIS plugin (#2) for distribution. The plugin needs `honua-gp` (#5) for the code-migration narrative. NIM integration (#4) is what makes the Inception story technically real instead of aspirational. The open assessment (#3) is the lead-gen funnel for the closed migration product. Filing them together means each one is visibly part of a coherent strategy rather than appearing isolated.

**Expected effort:** Honua-GIS-32B is the largest (8-10 weeks for the v0 release). QGIS plugin is ~2-4 weeks. `honua-esri-assess` v0 is ~2-3 weeks. NIM integration is ~3 days. `honua-gp` v0 (top 50 functions) is ~3-4 weeks.

**Epic 2 status (2026-05-23):** `honua-qgis-plugin` is live as a source preview, with the canonical landing/status page recorded in [§8.7](#7-honua-gis-assistant-qgis-plugin). Release ZIP, QGIS marketplace listing, screenshots, and demo video remain release-owner follow-ups.

**Founder defaults for Epic 2 (recorded 2026-05-21):** dedicated GPL-2+ repo, self-contained v0 over HTTP boundaries, anonymous/local-first by default, fallback to qwen2.5-coder until Honua-GIS beta is available, founder/operator rollup owner.

**Expected impact on Inception application:** the application narrative goes from "we plan to use NVIDIA tech" (generic startup pitch) to "we ship a NIM-deployable, NeMo-Customizer-fine-tuned, open-weights GIS-specific LLM with measured eval gains vs GPT-4o, distributed via Hugging Face, Ollama, and a QGIS plugin" (Inception Premier candidate).

---

## 6. Three things to downgrade or punt

Founder bandwidth is finite; explicit deferral is cheaper than implicit drift.

| Item | Current state | Recommendation | Justification |
|---|---|---|---|
| **honua-mobile-sdk** repo | 3 commits ever (last March 9), 0 open issues, superseded by honua-mobile | **Archive.** Replace README with redirect to honua-mobile; fold any IoT-specific scope (the differentiator vs honua-mobile) into a feature flag in honua-mobile if it matters. | Stops appearing in standing reviews. Removes ambiguity about which mobile repo to file issues against. |
| **honua-server enterprise GA wishlist** — #341 federated queries, #346 multi-tenancy, #347 plugin SDK, #357 Kafka/NATS event bus, #355 app-level rate limiting, #341/#346/#354 | All P4/GA, no execution lane | **Group under one umbrella "Enterprise GA — post-first-pilot" epic with explicit "do not start before customer X signs" criteria.** Remove individual issues from standing review. | Keeps the deferred surface visible without consuming mental energy at standups. Provides a single trigger criterion ("we have a paying customer requesting X") for promotion. |
| **honua-iac managed-container evaluations** — #11 EKS Fargate, #12 AKS Virtual Nodes, #13 AWS App Runner | P3 spikes | **Punt to "evaluate after first paying customer".** | Current Kubernetes-via-Helm deployment path is sufficient for the marketplace launch. These are runway optimizations, not product blockers. Premature optimization before we know what customers actually deploy. |

**Justification for explicit deferral over silent neglect:** without an explicit punt, these items show up in every backlog grooming pass and force micro-decisions that aren't load-bearing. Naming them deferred-with-criteria removes them from the cognitive surface area while preserving optionality.

---

## 7. The compliance / FedRAMP mismatch

**Problem at scan time:** honua-server #352 ("Compliance framework: SOC 2 / FedRAMP evidence collection, data residency, key rotation") was labeled **P4/GA**. Our stated GTM thesis targets state and local government, eventual GovCloud, eventual federal-touching SLG customers. These buyers ask "do you have SOC 2?" on the first call, and the procurement officer's questionnaire requires either SOC 2 attestation or FedRAMP-equivalent controls before evaluating.

**Execution status 2026-05-23:** #352 was promoted to P1 and closed; honua-sales #48 is the active procurement-readiness umbrella for the SOC 2 program.

**Why P4/GA was incompatible:**

- SOC 2 Type 1 readiness assessment: 3-6 months minimum
- SOC 2 Type 2 audit: 6-12 months after Type 1 (requires evidence over time)
- FedRAMP Moderate: 12-18 months minimum
- Sitting at P4 would have meant it did not start in 2026. We would have been having "we'll have it soon" conversations 12+ months from now.

**Execution lane:** the P1 umbrella has two repo touch-points:

- **honua-sales** — procurement-readiness umbrella issue covering: target attestation timeline (SOC 2 Type 1 by Q4 2026, Type 2 by Q3 2027), auditor selection, scoping decisions, customer-facing security questionnaire (already exists at `honua-sales/docs/user/ENTERPRISE_SECURITY_QUESTIONNAIRE_STARTER.md`)
- **honua-server** — technical-controls implementation: audit logging at #350 currently P4 needs promotion, RBAC at #349 already P2 ok, SSO/OIDC at #348 already P2 ok, RLS at #502 needs promotion, audit event model at #504 needs promotion, immutable audit trail with SIEM export at #350 needs promotion, key rotation, data residency controls

**Specific sub-issue list for the umbrella:**

1. Select SOC 2 auditor (3 quotes by 2026-07-31)
2. Define SOC 2 Trust Services Criteria scope
3. Promote honua-server #350 (audit logging) to P1
4. Promote honua-server #502 (RLS) to P2
5. Promote honua-server #504 (audit event model) to P2
6. Implement data residency configuration in honua-server
7. Implement encrypted credential vault (referenced in #354)
8. Build the FedRAMP control matrix doc (which controls we meet, which we don't, gap analysis)
9. Customer-facing security trust-center page on honua-site
10. Penetration test schedule (initial test by 2026-Q4)

**Tradeoff if we don't:** every SLG sales conversation hits the compliance question and we say "in progress" without a date. Customers default to renewing Esri. We lose the SLG thesis to a clock we didn't start.

**Tradeoff if we do:** ~$30-80K in audit cost (SOC 2 Type 1 audit typically $20-40K; pre-audit consultancy +$10-30K); ~10-20% of engineering time on controls implementation over 12 months. Real cost, real engineering drag. But the alternative is the SLG thesis is unrealizable in 2026.

---

## 8. What's already shipped that the marketing surface doesn't claim

The capability inventory revealed several real, working systems that aren't visible in any public-facing artifact (site, README, sales docs). These are either marketing leverage or product-strategy hidden gems.

### 1. The Grounding + MCP layer

`Features/Grounding/` + `Features/Protocols/Mcp/` is a working NL→intent→plan→execute pipeline with deterministic clarification handling. This is *the* infrastructure for AI Studio, honua-devops, QGIS plugin, and any future MCP-using client to share one consistent agent surface.

**Marketing leverage:** "Honua ships the first GIS-native MCP server" is a specific, defensible, technical-buyer claim. Maps cleanly to the Anthropic MCP standard. Nobody else in spatial has this.

**Recommendation:** add an `mcp.md` to docs/, link from site #9 proof hub, and mention in the Inception application explicitly.

### 2. The Spec language

`Features/Spec/` with `Canonical`, `Grammar`, `Operators`, `Validation`, SSE-streaming apply. This is a declarative service-definition language — "Terraform for GIS." Customers can version their Honua services as YAML/JSON manifests, plan changes, apply with diff preview.

**Marketing leverage:** procurement loves "infrastructure-as-code for your GIS." Esri has no equivalent. The "AI-native + reproducible" thesis is supportable with this artifact today.

**Recommendation:** promote in docs, demo in the migration proof package (show converting an Esri ServiceDefinition.sd into a Honua Spec manifest).

### 3. Migration-evidence pipeline

Server already exposes `MigrationScannerEndpoints`, `MigrationRunAdminEndpoints`, `MigrationPerformanceEvidenceEndpoints`, `ArcGisMigrationEvidenceEndpoints`, `CrossServerConsumeProbeEndpoints`. Recent commits reference "ArcGIS migration fidelity slices" and "OGC evidence packs + pyqgis lane fix."

**Marketing leverage:** the migration product isn't speculative — the server has migration-specific endpoints already shipped. The narrative "Honua was built migration-first" is supportable.

**Recommendation:** this is the spine of site #9 (proof hub). Pull a migration-evidence example from the running test data and publish.

### 4. Multi-database FeatureStore

`FeatureDataProviderRegistry` routes queries across PostGIS, DuckDB, SQL Server, and MySQL. "Bring your own spatial database" is a real feature.

**Marketing leverage:** customers with existing SQL Server geodatabases (a lot of utilities) can move to Honua without migrating the DB — Honua just queries their existing SQL Server while gradually moving services off Esri.

**Recommendation:** call this out specifically in the migration proof package as a "low-friction Tier 0" — read-only migration where the data doesn't move.

### 5. 3D scene pipeline

`Features/Scene/` produces Cesium-compatible 3D tilesets. Working ECEF transform.

**Marketing leverage:** state digital-twin RFPs always ask about 3D. We have it. Esri's I3S is the dominant format; we're shipping the OGC-standard 3D Tiles which is the Esri-alternative.

**Recommendation:** demo this in any state-DT-RFP response. Note that I3S read support is honua-server #530 (P4) — should be promoted if any state-DT pitch is active.

### 6. AiBuilder server surface (fixture-backed)

`Features/AiBuilder/` exists but uses `FixturePlanAnalysisService` — a fixture-backed proof. Slot for real LLM-backed planning when we're ready.

**Marketing leverage:** when we wire Honua-GIS-32B + NeMo Retriever, this becomes the AI Studio backend. The shape is already there; we're upgrading the brain.

**Recommendation:** explicit issue (server #892 already exists) — promote to P1, target Phase 4 (week 10-12) for first real LLM integration.

### 7. Honua GIS Assistant QGIS plugin

[`honua-qgis-plugin`](https://github.com/honua-io/honua-qgis-plugin) is a
dedicated GPL-2+ QGIS plugin repo, and the public landing/status page is live
at [honua.io/qgis-plugin.html](https://honua.io/qgis-plugin.html). The current
`0.1.0` early-preview scope is local-first: QGIS 3.34+, local Ollama model
refresh, Honua-GIS model preference, `qwen2.5-coder` fallback, disable-able
local audit JSONL, bounded vector-layer query bridge, and default-off remote
endpoint settings.

**Marketing leverage:** this gives the open spatial AI story a concrete analyst
surface. It reaches QGIS users directly without claiming marketplace approval
or a downloadable release artifact before those release-owner steps happen.

**Recommendation:** keep the landing page and plugin docs as the public source
of truth. Publish the GitHub Release ZIP, QGIS marketplace listing, screenshots,
and demo video only when those artifacts are release-matched.

---

## 9. Founder decisions

Founder calls recorded 2026-05-21. First pilot pricing remains open.

| # | Decision | Founder call |
|---|---|---|
| 1 | **Where does `honua-esri-assess` live?** New repo (clean OSS distribution), not a sub-package in honua-sdk-python. | New repo `honua-io/honua-esri-assess` |
| 2 | **Where does the Honua-GIS-32B work live?** New repo `honua-gis-llm`, not under honua-sdk-python. | New repo `honua-io/honua-gis-llm` |
| 3 | **Honua-GIS-32B base model.** Qwen 2.5 Coder 32B, not Llama 3.3 70B. | Qwen 2.5 Coder 32B |
| 4 | **Esri Partner Network posture: resolved.** Do not join EPN. Keep the migration play independent of Esri partnership, co-marketing, partner licensing, or event constraints. | Do not join. |
| 5 | **Compliance investment commitment.** Commit to SOC 2 readiness and Type 1 audit path; keep FedRAMP as control-matrix / readiness planning until separately funded. | SOC 2 committed |
| 6 | **Should we archive honua-mobile-sdk?** Archive and redirect to honua-mobile. | Archive |
| 7 | **First pilot pricing.** sales #47 trigger says "first paid pilot signed" but pricing isn't defined. Recommend $25K-75K for the migration assessment tier (matches the engagement effort), then $250K-2M for execution. Founder to set ACV bands. | Open |
| 8 | **QGIS plugin model/runtime posture.** Use Qwen-based Honua-GIS model through local Ollama first. No telemetry by default. | Qwen + Ollama |

---

## 10. Open questions

Things this plan doesn't have an answer for yet, in order of urgency.

1. **What's the actual budget?** Compliance ($30-80K), Hugging Face fine-tuning compute ($5-15K total over the year), marketplace submission fees, design partner concessions. The plan implies ~$50-150K cash spend across the 60 days. Need founder reconciliation against runway.

2. **Who else is working on this?** This plan assumes founder-only execution. If there's a small team, work distribution changes (e.g., honua-server #965 cert fix can happen in parallel with sdk-python #59 scanner work). If solo, sequencing matters more.

3. **Is there a target Inception decision date?** If Inception responds in N weeks, that's when the open-weights work + QGIS plugin should be at maximum visibility. Currently plan has these in Phase 4 (week 10-12); if Inception decision is week 8, accelerate.

4. **Design partner pipeline.** Which prospects exist today? The Phase 4 outcome ("convert one design partner") assumes there's a prospect to convert. Sales #14 (VoC) just opened; the prospect list may not be there yet.

5. **honua-server-admin repo** — the issue scan agent flagged that several migration-tooling issues live in `honua-server-admin` (e.g., #79, #84, #94, #25 declarative-spec / "Terraform-for-GIS" epic). This was NOT in the 15 repos in scope. Should we add it to the standing inventory? Probably yes.

6. **What about `honua-server-ai-work`?** Another shadow repo flagged in the file listing. Worth verifying what's there before assuming the AI work is consolidated.

7. **Are there any external contributors we should be coordinating with?** Open-source projects benefit from community; closed migration products don't. The plan splits these correctly but needs explicit governance docs.

8. **GTC submission timing.** If we want a 2027 GTC speaking slot, the submission deadline is typically Q4 2026. The open-weights work needs to be measurable by then (eval published, downloads counted, customer story). Phase 4 timing supports this.

---

## Appendix A — Top issues by repo (2026-05-20 scan baseline)

(Compact list for reference; full data in inventory scan output. Current promotion and closure state is reconciled in Appendix C; scan-time "should" and "needs" wording below is historical unless Appendix C leaves a follow-up open.)

### honua-server (33 open) — heaviest backlog

Active P1s:
- **#965** TLS cert bug on api/staging hosts — scan-time P0 candidate; promoted to P0 and closed 2026-05-22
- **#1035** Metadata v2 canonical resource graph

Migration-relevant:
- **#1096** ArcGIS Pro licensed evidence workflow — priority/P1 now applied; remains open
- **#892** AI app builder platform contract — promoted to P1 and closed 2026-05-22

Esri-parity (currently parked):
- #530 (3D scenes/I3S — P4), #371 (versioned editing — P3), #374 (data enrichment API — P3), #971/#972/#973 (collaborative editing — P2)

Compliance:
- **#352** Compliance framework — promoted to P1 and closed 2026-05-23; active procurement execution moved to honua-sales #48

Enterprise GA wishlist (to umbrella-defer):
- #341, #346, #347, #348, #349, #350, #354, #355, #357, #502, #504, #507, #508, #509, #510

### honua-sdk-python (2 open) — the migration wedge

- **#59** ArcPy/Python GP migration scanner — P1, ready-to-start
- **#53** Live staging target config — P1

### honua-site (6 open) — marketing surface

- **#9** Competitive proof hub — P1, the public face of the migration story
- #8 Developer adoption epic — P1
- #7 Operating cadence — P1
- #3 Lead capture instrumentation — P1
- #2 Docs hub with API quickstarts — P1
- #5 CRM reconciliation SLA — P2

### honua-marketplace (5 open, all P1 MVP)

All five filed 2026-05-05. Sequence #4 → #5 → #1/#2/#3.

### honua-iac (13 open)

- **#30** Deploy seeded cloud demo services — P1, formerly blocked on server #965; downstream validation remains
- **#24** VPC quota exhaustion fix — P1
- #1, #2 AWS + Azure Marketplace deployments — P1
- #22, #23 Backup/restore + RTO/RPO drills — P2
- #5 Operating cadence — P1

### honua-helm (3 open)

- **#5** Epic: K8s release platform — P1, blocks customer-operated deployment
- #4 Operating cadence — P1
- #3 Automated smoke tests — P2

### honua-sales (19 open) — GTM motion

P1s: #47 (first paid pilot trigger), #42 (end-to-end buyer path), #34/#33 (Azure/AWS Marketplace path), #18 (showcase repo), #14 (customer discovery — newly MVP), #13 (inbound demand engine), #12 (AI-assisted outbound), #8 (operating cadence), #3 (email subscribe), #1 (content marketing strategy)

### honua-mobile (5 open, all P2)

Epic-sized work: #1 (Mobile SDK epic), #82 (Mobile DevOps), #92 (Offline field operations demo), #10 (Embeddable map component), #85 (Store prereqs)

### honua-portal (8 open) — under-prioritized

P2: #71 (Open data hub annotations + handoff)
P3: #1 (Web map portal), #4 (real-time collab editing), #5 (Dashboard + app builder), #6 (Open data hub)
P4: #43, #44, #45 (implementation placeholders)

**Note:** zero P1 issues on the customer-facing UX repo. This is the AI Studio surface — should have P1 work.

### honua-devops (1 open)

- **#41** Release-candidate validation for cross-repo compatibility — P1

### honua-support (5 open)

P1: #1 Epic (support platform), #3 (telemetry evidence), #5 (operator triage workspace)
P2: #4 (customer support portal)
Unlabeled: #9 (LLM triage)

### honua-sdk-js (0 open)

Burned down within the last 2 weeks. Healthy.

### honua-sdk-dotnet (0 open)

Same.

### honua-mobile-sdk (0 open)

Recommend archive.

### honua-agentflow (2 open, unlabeled)

Internal tooling; not strategic.

---

## Appendix B — Strategic-gap epic shapes

Reference: copy-paste-ready epic titles + sub-issues for the five new epics in §5.

### Epic 1: Honua-GIS-32B — open-weights GIS-specific LLM

**Repo:** new `honua-io/honua-gis-llm`
**License:** Apache-2.0 for tooling; model weights under selected base model's license (Qwen Apache-2.0 if Qwen 2.5 Coder is chosen)
**Owner:** founder (model + corpus) + community contributors (after v0)

**Sub-issues:**
1. Eval harness: `gis-workflow-eval` — 50 prompts with expected outputs covering query, geoprocessing, styling, raster ops; published as GitHub repo for reproducibility
2. Baseline eval: vanilla Qwen 2.5 Coder, vanilla GPT-4o, vanilla Claude — establish baselines
3. Training corpus curation: 500-2000 GIS workflow examples from FaultCatalog + honua-devops audit traces + synthesized arcpy ↔ honua_gp pairs + filtered public sources
4. LoRA fine-tune pipeline: MLX-LM on Mac (development) + Axolotl on H100 (production); document recipe
5. Hugging Face model card publication with GGUF + safetensors variants
6. Ollama model library upstream PR
7. NIM container build + Docker Hub publish
8. Eval published vs GPT-4o/Claude — narrative blog post
9. Integration into honua-devops (`ProviderKind.LocalLlama`)
10. Integration into the QGIS plugin (Epic 2)

### Epic 2: QGIS plugin — Honua-GIS Assistant

**Repo:** `honua-io/honua-qgis-plugin`
**License:** GPL-2+ (required for QGIS plugin marketplace)
**Landing page:** [§8.7 landing/status page](#7-honua-gis-assistant-qgis-plugin)
**Current status:** `0.1.0` source preview; source and packaging scripts are live. No GitHub release tag, GitHub Release ZIP, or QGIS marketplace approval exists yet; production screenshots and demo video remain pending.

**Sub-issues:**
1. Source-preview implemented: plugin skeleton + `metadata.txt` + QGIS plugin manifest
2. Source-preview implemented: chat panel UI in QGIS dockwidget
3. Source-preview implemented: bounded PyQGIS query bridge with Honua-GIS model preference
4. Source-preview implemented: local Ollama detection + `qwen2.5-coder` fallback until Honua-GIS beta is published
5. Source-preview implemented as default-off settings: optional remote NIM endpoint + OpenAI-compatible fallback
6. Source-preview implemented: audit JSONL local logging (no telemetry by default)
7. Release-owner pending: QGIS plugin marketplace submission
8. Source-preview implemented: documentation + example workflows, including the static landing/status page in §8.7
9. Release-owner pending: distribution outside QGIS marketplace through GitHub Release ZIP once published
10. Release-owner pending: demo video for marketing, using only release-matched screenshots and sample data

### Epic 3: `honua-esri-assess` — open Esri assessment tool

**Repo:** new `honua-io/honua-esri-assess`
**License:** Apache-2.0

**Sub-issues:**
1. `EsriFootprint.json` schema definition + reference doc
2. CLI scaffold (Python or .NET — recommend .NET to share `FileGdbReader` with honua-server)
3. ArcGIS Online Portal Sharing API scanner (read-only token auth)
4. ArcGIS Server REST scanner (anonymous + token)
5. `.gdb` reader using honua-server's `FileGdbReader` as a library dependency
6. License entitlement enumeration (legitimate read-only via documented admin endpoints)
7. Migration readiness report generator (Markdown + JSON)
8. GitHub Action distribution for CI/CD integration
9. Hand-off contract to closed migration product: `EsriFootprint.json` schema versioning
10. Docs + onboarding tutorial

### Epic 4: NVIDIA NIM integration in honua-devops

**Repo:** honua-devops
**License:** existing (proprietary)

**Sub-issues:**
1. Wire `ProviderKind.LocalLlama` in `Providers/AgentProviderFactory.cs` (~30 LoC change)
2. Add `HONUA_DEVOPS_NIM_ENDPOINT`, `HONUA_DEVOPS_NIM_MODEL`, `HONUA_DEVOPS_NIM_API_KEY` env vars to `BackendConfiguration`
3. Smoke test against `build.nvidia.com` hosted NIM (free tier for Developer Program members)
4. Write `docs/deployments/nvidia-nim.md` deployment guide
5. Validate `find_recent_operations` audit-aware tool against NIM-hosted Llama-3.3 + Nemotron
6. Eval comparison: NIM-hosted model vs codex/claude on the same prompt suite
7. Update README to claim NIM compatibility
8. Update Inception application narrative with concrete NIM integration claim

### Epic 5: `honua-gp` — arcpy compatibility shim

**Repo:** new sub-package in honua-sdk-python (or new repo `honua-gp`)
**License:** proprietary (closed-source — this is the moat)

**Sub-issues:**
1. Top-20 `arcpy.management.*` shim: `MakeFeatureLayer`, `SelectLayerByAttribute`, `SelectLayerByLocation`, `CalculateField`, `AddField`, `DeleteField`, `Append`, `Merge`, `Dissolve`, `Copy`, `Delete`, `Rename`, `CreateFeatureclass`, `CreateTable`, `Project`, `Sort`, `MakeTableView`, `GetCount`, `ListFields`, `Describe`
2. Top-15 `arcpy.analysis.*` shim: `Buffer`, `Clip`, `Intersect`, `Union`, `Erase`, `NearestNeighbor`, `Near`, `SpatialJoin`, `TabulateIntersection`, `MultipleRingBuffer`, `PointDistance`, `SummarizeWithin`, `SymmetricalDifference`, `Update`, `Identity`
3. Top-10 `arcpy.da.*` cursor shim: `SearchCursor`, `UpdateCursor`, `InsertCursor` with context-manager semantics
4. Dispatch layer: each shim calls the existing honua-sdk-python client which talks to Honua REST/gRPC
5. Audit JSONL capture: every call logged for fine-tuning corpus
6. Eval suite: 50 representative DOT/utility arcpy scripts — measure compatibility %
7. Compatibility matrix doc: which arcpy functions are supported, which raise `NotImplementedError`, which have semantic differences
8. Distribution: PyPI package `honua-gp` (private index initially)
9. Customer-facing migration report integration: show which scripts will work unchanged vs need manual review
10. Continuous expansion: add new functions as customer engagements reveal them

---

## End of plan

**Founder review checklist (status reconciled 2026-05-23):**

- [x] Confirm Phase 1 backlog actions in §4 — #965/#1096/#892/#352 promotions are recorded in Appendix C
- [x] Confirm the five strategic gaps in §5 should be filed as epics this week
- [ ] Decide first pilot pricing in §9
- [x] Decide compliance investment commitment in §7
- [ ] Decide if honua-server-admin + honua-server-ai-work should be added to standing inventory
- [ ] Comment on, or push back on, the sequencing in §4 — any phases need re-ordering for runway / Inception / pilot pipeline reasons
- [ ] Identify any strategic priorities this plan missed

---

## Appendix C — Filed backlog and promotions

Executed 2026-05-21 against the live GitHub portfolio. All issues are ready for honua-agentflow to pick up via existing repo profiles. Specifica-source repos have unpublished contract handoff paths in `agent-delivery-spec/contracts/<repo>/<n>.md`; those paths are not public GitHub URLs until the Specifica workspace has a published remote.

### Label promotions on existing issues (4)

| Repo / Issue | Title | Change |
|---|---|---|
| [honua-server#965](https://github.com/honua-io/honua-server/issues/965) | Cloud API hosts present GitHub Pages TLS certificate | priority/P1 → **priority/P0**; closed 2026-05-22. The TLS blocker is no longer active. |
| [honua-server#1096](https://github.com/honua-io/honua-server/issues/1096) | ops: run licensed ArcGIS Pro evidence workflow and link from migration docs | Added **priority/P1**, **ready-to-start**, **effort/M**, **phase/MVP**, **area/migration**. Was previously unlabeled for priority. |
| [honua-server#892](https://github.com/honua-io/honua-server/issues/892) | Platform contract: AI app builder and spatial query demo enablement | priority/P2 → **priority/P1** + added **blocks-others**, **ready-to-start**; closed 2026-05-22. |
| [honua-server#352](https://github.com/honua-io/honua-server/issues/352) | Compliance framework: SOC 2 / FedRAMP evidence collection | priority/P4 → **priority/P1** + added **blocks-others**; closed 2026-05-23. (Note: phase remains GA; SOC 2 Type 2 is a 2027 milestone. Active execution coordinated from honua-sales#48.) |

### New strategic-gap epics filed (6)

| Repo / Issue | Title | Why | Phase |
|---|---|---|---|
| [honua-sdk-python#62](https://github.com/honua-io/honua-sdk-python/issues/62) | Epic: honua-esri-assess — open-source Esri footprint assessment tool | The open lead-gen funnel for the closed migration product (§5 Epic 3) | priority/P1 phase/MVP effort/XL blocks-others |
| [honua-sdk-python#63](https://github.com/honua-io/honua-sdk-python/issues/63) | Epic: honua-gp — closed-source arcpy compatibility shim (top-50 functions) | The "your scripts keep working" deal-closer; complements #59 scanner (§5 Epic 5) | priority/P1 phase/MVP effort/XL blocks-others |
| [honua-sdk-python#64](https://github.com/honua-io/honua-sdk-python/issues/64) | Epic: Honua-GIS-32B — open-weights GIS-specific LLM | Inception Premier narrative + brand anchor for "open AI for spatial" (§5 Epic 1) | priority/P1 phase/MVP effort/XL |
| [honua-sdk-python#65](https://github.com/honua-io/honua-sdk-python/issues/65) | Epic: honua-qgis-plugin — Honua-GIS assistant for QGIS | Distribution channel for Honua-GIS; source repo and landing page now live (§5 Epic 2) | priority/P2 phase/Beta effort/XL |
| [honua-devops#46](https://github.com/honua-io/honua-devops/issues/46) | Epic: NVIDIA NIM integration as a first-class provider in honua-devops | Make the NVIDIA stack claim technically real, not aspirational (§5 Epic 4) | priority/P1 phase/MVP effort/M ready-to-start |
| [honua-sales#48](https://github.com/honua-io/honua-sales/issues/48) | Epic: Procurement readiness — SOC 2 program kickoff for SLG market | Close the compliance / SLG GTM mismatch identified in §7 | priority/P1 phase/MVP effort/XL blocks-others |

### Specifica contract handoffs recorded (2)

For repos with `requirements_source: specifica`, agentflow grooming requires a contract document outside the GitHub issue body.

- `agent-delivery-spec/contracts/honua-devops/46.md` — unpublished Specifica handoff path for the NIM integration epic
- `agent-delivery-spec/contracts/honua-sales/48.md` — unpublished Specifica handoff path for the SOC 2 procurement-readiness umbrella

The other four new epics live in honua-sdk-python which uses `markdown-default` grooming — the GitHub issue body itself is the contract.

### Founder decisions status reconciliation

These match the §9 decision points. Resolved entries no longer gate agentflow; open entries remain founder follow-ups:

1. **honua-esri-assess repo location** (decision #1) — resolved: dedicated `honua-io/honua-esri-assess` repo.
2. **honua-gis-llm repo location** (decision #2) — resolved: dedicated `honua-io/honua-gis-llm` repo.
3. **Honua-GIS-32B base model choice** (decision #3) — resolved: Qwen 2.5 Coder 32B.
4. **honua-qgis-plugin repo creation** — resolved 2026-05-23. Dedicated `honua-io/honua-qgis-plugin` repo exists with GPL-2+ license, and the static landing/status page is live in §8.7. Remaining release-owner follow-ups are the GitHub Release ZIP, QGIS marketplace approval, screenshots, and demo video.
5. **Esri Partner Network confirmation** (decision #4) — resolved: do not join. No backlog action; document the call in honua-sales/docs/strategy/.
6. **Compliance investment commitment** (decision #5) — resolved: SOC 2 committed; honua-sales#48 remains the execution umbrella.
7. **honua-mobile-sdk archive** (decision #6) — resolved as a founder call. No backlog action filed yet.
8. **First pilot pricing** (decision #7) — recommended bands: $25-75K assessment, $250K-2M execution. Founder to confirm before sales conversations start using these numbers.

### Agentflow prerequisites verified

- All target repos (honua-server, honua-sdk-python, honua-devops, honua-sales) have profiles in `honua-agentflow/profiles/`.
- All required labels exist on all target repos: priority/P0-P4, ready-to-start, blocks-others, effort/XS-XL, phase/MVP/Beta/GA, area/*, edition/*.
- Specifica contract handoff paths recorded for the two issues in specifica-source repos; no public contract URLs are claimed in this tracker.
- Markdown-default repos (honua-sdk-python) have issue bodies with the required `## Why`, `## Scope`, `## Acceptance Criteria` sections.

agentflow can now pick these up via standard `inbox` / `next` flow.

### Linkage from plan sections to live issues

- §1 Executive summary "biggest single ask" → [honua-server#965](https://github.com/honua-io/honua-server/issues/965) (P0, closed)
- §3 scorecard "AI Studio under-prioritized" → [honua-server#892](https://github.com/honua-io/honua-server/issues/892) (now P1)
- §3 scorecard "Esri migration tooling" → [honua-server#1096](https://github.com/honua-io/honua-server/issues/1096) (now P1)
- §4 Phase 2 "ship the migration proof package" → coordinates honua-sdk-python#59 + #62 + honua-server#1096 + honua-site#9
- §5 Epic 1 Honua-GIS-32B → [honua-sdk-python#64](https://github.com/honua-io/honua-sdk-python/issues/64)
- §5 Epic 2 QGIS plugin → [honua-sdk-python#65](https://github.com/honua-io/honua-sdk-python/issues/65), [honua-qgis-plugin](https://github.com/honua-io/honua-qgis-plugin), and [§8.7 landing/status page](#7-honua-gis-assistant-qgis-plugin)
- §5 Epic 3 honua-esri-assess → [honua-sdk-python#62](https://github.com/honua-io/honua-sdk-python/issues/62)
- §5 Epic 4 NIM integration → [honua-devops#46](https://github.com/honua-io/honua-devops/issues/46)
- §5 Epic 5 honua-gp compat shim → [honua-sdk-python#63](https://github.com/honua-io/honua-sdk-python/issues/63)
- §7 compliance mismatch → [honua-server#352](https://github.com/honua-io/honua-server/issues/352) (now P1) + [honua-sales#48](https://github.com/honua-io/honua-sales/issues/48) (umbrella)
