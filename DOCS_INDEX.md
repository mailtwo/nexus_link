# DOCS_INDEX.md — Documentation Routing (2-Tier)

This file is the single entry point for:
- Where canonical facts/rules live (**Tier 1: Domain SSOT**)
- Where to start reading by cross-cutting feature (**Tier 2: Feature Hub**)
- What may be defined vs what may only be linked by reference

If you are implementing, modifying, or refactoring a feature, start here.

---

## Core rules

1) **Concrete rules live only in Tier 1 Domain SSOT docs.**
- field definitions
- enum values
- return shapes
- command syntax
- persistence boundaries
- runtime algorithms
- engine/layout contracts

2) **Tier 2 Feature Hubs do not redefine low-level rules.**
They may contain:
- feature purpose
- player-facing goals
- design boundaries
- reading order
- which Tier 1 docs own which facts
- current open questions / implementation status

3) **If a feature repeatedly touches 3 or more Tier 1 docs, create or update a Tier 2 Feature Hub.**

4) **When a Tier 2 hub needs a new concrete rule, write that rule in the correct Tier 1 doc and link to it from the hub.**
Never define the same rule in both places.

5) **Legacy / deprecated docs are read-only unless explicitly reactivated.**

---

## How to use this index

### If you are changing a concrete fact/rule
Go to **Tier 1 Domain SSOT** and edit the owning doc only.

### If you are designing or implementing a feature that spans multiple systems
Start with the matching **Tier 2 Feature Hub**, then follow its linked Tier 1 docs in order.

### If you are unsure
Ask:
- “Am I changing a rule?” -> Tier 1
- “Am I coordinating multiple rule-owning docs for one feature?” -> Tier 2

---

# TIER 1 — DOMAIN SSOT

These docs own canonical rules.

## A. Product / Experience / Content

### 00 — `00_overview.md`
**Purpose:** Project vision, pillars, scope boundaries, non-goals.  
**Owns:** High-level product identity only.  
**Must NOT own:** implementation contracts, schemas, API details.

### 01 — `01_existing_hacking_games.md`
**Purpose:** Competitive/reference research and positioning.  
**Owns:** differentiation rationale, external references.  
**Must NOT own:** implementation rules.

### 04 — `04_attack_routes_and_missions.md`
**Purpose:** mission templates, attack-route design, gameplay route ideas.  
**Owns:** mission/route design patterns, trace gameplay concept, hint-system design intent.  
**Must NOT own:** API/system-call/runtime schema details.

### 15 — `15_game_flow_design.md`
**Purpose:** player journey, onboarding, progression flow, early-to-late play structure.  
**Owns:** onboarding flow, unlock timing, player journey structure, restart/load flow from experience perspective.  
**Must NOT own:** low-level API/UI/runtime contracts.

---

## B. Player-Facing Interfaces / Interaction Contracts

### 03 — `03_game_api_modules.md`
**Purpose:** MiniScript intrinsic API contract.  
**Owns:** module/function surface, ResultMap shapes, error codes, API-side cost/trace conventions.  
**Must NOT own:** terminal command UX, shell layout, runtime schema internals.

### 07 — `07_ui_terminal_prototype_godot.md`
**Purpose:** terminal UX and system-call / command contract.  
**Owns:** command syntax, command behavior, terminal parsing, terminal-side UX tied to commands.  
**Must NOT own:** official-program contracts, persistence policy, runtime schema.

### 13 — `13_nexus_shell_workspace_contract.md`
**Purpose:** Shell workspace / layout / pane-system contract.  
**Owns:** shell layout rules, pane lifecycle, toast/activity popup/docked pane behavior, workspace-level UI persistence requirements.  
**Note:** current file can be re-scoped into this role even if the filename stays temporarily unchanged.  
**Must NOT own:** save data format, command syntax, official-program behavior.

### 14 — `14_official_programs.md`
**Purpose:** contracts for shipped programs (`ExecutableHardcode` / `ExecutableScript`).  
**Owns:** official program behavior, program-level gating, error semantics, execution contracts for shipped tools/programs.  
**Must NOT own:** intrinsic API shapes, shell layout, persistence format.

---

## C. Runtime / Engine / Data Contracts

### 02 — `02_miniscript_interpreter_and_constraints.md`
**Purpose:** MiniScript embedding/integration constraints.  
**Owns:** interpreter integration model, execution constraints, project-level scripting limitations.  
**Must NOT own:** public API list, command syntax, content-authoring rules.

### 08 — `08_vfs_overlay_design_v0.md`
**Purpose:** VFS data/overlay contract.  
**Owns:** path normalization, VFS permissions, file kinds, overlay semantics.  
**Must NOT own:** terminal command UX, persistence policy, shell UI rules.

### 09 — `09_server_node_runtime_schema_v0.md`
**Purpose:** runtime server/node storage schema.  
**Owns:** server runtime fields, indexes, caches, persisted runtime-facing data structures.  
**Must NOT own:** scheduler rules, command syntax, feature-flow intent.

### 11 — `11_event_handler_spec_v0_1.md`
**Purpose:** runtime event/scheduler semantics.  
**Owns:** dispatch, execution ordering, handler behavior, process/event runtime rules.  
**Must NOT own:** persistence format, mission-flow intent.

### 12 — `12_save_load_persistence_spec_v0_1.md`
**Purpose:** persistence boundaries and save/load policy.  
**Owns:** what is saved, what is transient, versioning, load reconstruction rules.  
**Must NOT own:** UI layout interaction rules except by reference to owning UI contract docs.

---

## D. Scenario / Content Authoring

### 10 — `10_blueprint_schema_v0.md`
**Purpose:** scenario/blueprint authoring schema.  
**Owns:** designer-authored scenario format, blueprint->runtime initialization mapping.  
**Must NOT own:** runtime execution semantics, command syntax, feature-hub coordination.

---

## E. Legacy / Deprecated

### 05 — `05_ui_terminal_prototype.md`
**Status:** LEGACY, READ-ONLY  
**Purpose:** historical prototype notes only.  
**Owns:** nothing.

### 06 — `06_server_nodes_design_v0.md`
**Status:** DEPRECATED, READ-ONLY  
**Purpose:** historical/deprecated node-design notes only.  
**Owns:** nothing.

---

# TIER 2 — FEATURE HUBS

These docs are not the place for low-level rule definitions.
They are the place to start reading and planning.

## 16 — `16_nexus_shell_workspace_hub.md`
**Purpose:** NEXUS Shell as the player’s main workspace.  
**Covers:** terminal-only start -> shell unlock, start menu, taskbar, pane taxonomy, activity popups, settings access, command parity.  
**Read Tier 1 docs in order:** 15 -> 13 -> 07 -> 14 -> 12

## 17 — `17_onboarding_first_license_hub.md`
**Purpose:** from first boot to first license promotion.  
**Covers:** README follow-up, early mission chain, shell unlock timing, tutorial pacing, first “real intrusion” loop.  
**Read Tier 1 docs in order:** 15 -> 07 -> 14 -> 03 -> 04 -> 13 -> 12

## 18 — `18_contracts_and_license_progression_hub.md`
**Purpose:** contract board / mission board / license progression structure.  
**Covers:** why the player takes the next job, what counts toward promotion, how world-feel side contracts differ from critical progression.  
**Read Tier 1 docs in order:** 15 -> 04 -> 14 -> 03 -> 10 -> 11

## 19 — `19_trace_and_risk_feedback_hub.md`
**Purpose:** hot trace / forensic / lock-on as a player feedback loop.  
**Covers:** risk readability, map feedback, route pressure, log-breaking feedback, how trace feels to the player.  
**Read Tier 1 docs in order:** 04 -> 03 -> 11 -> 09 -> 13 -> 12

## 20 — `20_remote_operations_and_route_execution_hub.md`
**Purpose:** remote connection, route execution, ftp/ssh/world-state operations as one coherent feature.  
**Covers:** connect/disconnect, route/session mental model, remote file movement, command vs intrinsic parity, chain-based play feel.  
**Read Tier 1 docs in order:** 03 -> 07 -> 14 -> 08 -> 09 -> 11 -> 04

## 21 — `21_scenario_authoring_pipeline_hub.md`
**Purpose:** content-authoring path from scenario blueprint to running mission behavior.  
**Covers:** how authored content becomes runtime state and event-driven mission logic.  
**Read Tier 1 docs in order:** 10 -> 09 -> 11 -> 08 -> 03 -> 04

## 22 — `22_persistence_and_workspace_restore_hub.md`
**Purpose:** what the player expects to resume after save/load/reboot.  
**Covers:** workspace layout restore, shell state restore, program/process visibility after load, boot vs load experience.  
**Read Tier 1 docs in order:** 12 -> 13 -> 07 -> 14 -> 15 -> 09

## 23 — `23_toolchain_and_program_progression_hub.md`
**Purpose:** tools/programs/automation as progression, not just commands.  
**Covers:** why players script, when official tools unlock, how automation replaces manual repetition, how “tool-building” becomes power.  
**Read Tier 1 docs in order:** 14 -> 03 -> 15 -> 02 -> 07 -> 04

---

## Reference style

When a Tier 2 hub mentions a concrete rule, link the Tier 1 owner and do not restate the rule.

Preferred style:
- `Canonical rule: See Tier 1 -> 07 (07_ui_terminal_prototype_godot.md)`
- `Persistence boundary: See Tier 1 -> 12 (12_save_load_persistence_spec_v0_1.md)`

---

## Editing checklist

1) Is this a concrete rule change? -> edit Tier 1 only  
2) Is this a cross-system feature coordination change? -> update Tier 2 hub first, then edit linked Tier 1 docs  
3) Does the feature now touch 3+ Tier 1 docs repeatedly? -> create/update a Tier 2 hub  
4) If a Tier 2 hub and Tier 1 doc disagree, Tier 1 wins for low-level rules