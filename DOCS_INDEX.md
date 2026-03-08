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

## Document identifiers and metadata

- **Document numbers are the canonical identifiers.**
- Tier 1 documents use **two-digit doc IDs**.
- Tier 2 Feature Hubs use the **100 series**.
- Assume every document filename starts with its doc ID.
- In prose, prefer **doc ID references only** (for example: `See DOCS_INDEX -> 13`, `See DOCS_INDEX -> 100`).

### Required metadata block
Every active `plans/` document must include a short metadata block near the top.

Required fields:
- `Purpose:` one concise sentence describing what the document owns
- `Keywords:` 5–10 high-signal search terms

Optional field:
- `Aliases:` short alternate names only when genuinely useful

Metadata rules:
- `Keywords` exist for **document discovery and searchability**.
- `Keywords` are **not** the canonical reference target in body text.
- When creating a new document, always add its metadata block.
- When a document’s scope changes materially, update its `Purpose` and `Keywords` accordingly.
- When searching for relevant documentation, consult `DOCS_INDEX.md` first, then use `Purpose` / `Keywords` / `Aliases` to identify the most relevant document.
- Prefer stable, canonical terms in `Keywords`. Do not overstuff synonyms or narrative phrases.


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

### 12 — `12_save_load_persistence_spec_v0_5.md`
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

# TIER 2 — FEATURE HUBS

These docs are not the place for low-level rule definitions.
They are the place to start reading and planning.
Tier 2 Feature Hub document numbers use the `100` series to stay visually distinct from Tier 1 SSOT docs.

## 100 — `100_nexus_shell_feature_hub.md`
**Purpose:** NEXUS Shell as the player’s main workspace.  
**Covers:** terminal-only start -> shell unlock, start menu, taskbar, pane taxonomy, activity popups, settings access, command parity.  
**Read Tier 1 docs in order:** 15 -> 13 -> 07 -> 14 -> 16 -> 12

## 101 — `101_onboarding_first_license_hub.md`
**Purpose:** from first boot to first license promotion.  
**Covers:** README follow-up, early mission chain, shell unlock timing, tutorial pacing, first “real intrusion” loop.  
**Read Tier 1 docs in order:** 15 -> 07 -> 14 -> 03 -> 04 -> 13 -> 12

## 102 — `102_contracts_and_license_progression_hub.md`
**Purpose:** contract board / mission board / license progression structure.  
**Covers:** why the player takes the next job, what counts toward promotion, how world-feel side contracts differ from critical progression.  
**Read Tier 1 docs in order:** 15 -> 04 -> 14 -> 03 -> 10 -> 11

## 103 — `103_trace_and_risk_feedback_hub.md`
**Purpose:** hot trace / forensic / lock-on as a player feedback loop.  
**Covers:** risk readability, map feedback, route pressure, log-breaking feedback, how trace feels to the player.  
**Read Tier 1 docs in order:** 04 -> 03 -> 11 -> 09 -> 13 -> 12

## 104 — `104_remote_operations_and_route_execution_hub.md`
**Purpose:** remote connection, route execution, ftp/ssh/world-state operations as one coherent feature.  
**Covers:** connect/disconnect, route/session mental model, remote file movement, command vs intrinsic parity, chain-based play feel.  
**Read Tier 1 docs in order:** 03 -> 07 -> 14 -> 08 -> 09 -> 11 -> 04

## 105 — `105_scenario_authoring_pipeline_hub.md`
**Purpose:** content-authoring path from scenario blueprint to running mission behavior.  
**Covers:** how authored content becomes runtime state and event-driven mission logic.  
**Read Tier 1 docs in order:** 10 -> 09 -> 11 -> 08 -> 03 -> 04

## 106 — `106_persistence_and_workspace_restore_hub.md`
**Purpose:** what the player expects to resume after save/load/reboot.  
**Covers:** workspace layout restore, shell state restore, program/process visibility after load, boot vs load experience.  
**Read Tier 1 docs in order:** 12 -> 16 -> 13 -> 07 -> 14 -> 15 -> 09

## 107 — `107_toolchain_and_program_progression_hub.md`
**Purpose:** tools/programs/automation as progression, not just commands.  
**Covers:** why players script, when official tools unlock, how automation replaces manual repetition, how “tool-building” becomes power.  
**Read Tier 1 docs in order:** 14 -> 03 -> 15 -> 02 -> 07 -> 04

## 108 — `108_developer_tools_hub.md`
**Purpose:** developer tooling and debug-only startpoint override as a feature-planning hub.  
**Covers:** direct-to-shell development entry, debug boot/startpoint override direction, future developer tools umbrella.  
**Read Tier 1 docs in order:** 15 -> 13 -> 16 -> 12

---

## Reference style

Use document IDs as the canonical reference target.

### In body text
- Refer to documents by **doc ID only**.
- Preferred style:
  - `Canonical rule: See DOCS_INDEX -> 07`
  - `Persistence boundary: See DOCS_INDEX -> 12`
  - `Related context: See DOCS_INDEX -> 100`
- Do **not** use document titles or filenames as the primary reference target in body text unless absolutely necessary.

### Reference strength
- **Normative reference**:
  - Use this when pointing to the document that owns the canonical rule.
  - Example: `Canonical rule: See DOCS_INDEX -> 13`
- **Informative reference**:
  - Use this when pointing to related context that is helpful but does not own the rule.
  - Example: `Related context: See DOCS_INDEX -> 100`

### Feature-first reading
- For cross-cutting features like NEXUS Shell, start from the relevant **Tier 2 Feature Hub** and follow its linked Tier 1 docs in order.
- Example: `Feature-first reading: See DOCS_INDEX -> 100`

### Searchability
- Discoverability is supported by:
  - the filename
  - the document title
  - the `Purpose`
  - the `Keywords`
  - the optional `Aliases`
- These metadata fields improve document search and routing, but they are **not** the canonical reference target in prose.

### Authoring rule
- In non-owning docs, do not restate the full rule; add only a short reference.
- Use **normative references** for the owning Tier 1 doc.
- Use **informative references** for related Tier 2 hubs or adjacent context.

## Conflict reporting

- If you find inconsistencies, report them by **doc ID** and section, in Korean.
- Preferred format:
  - `13 <section> vs 16 <section> - 충돌: <1~2줄 요약>`

## Editing checklist

1) Is this a concrete rule / schema / API / command / persistence field / engine contract change?  
   -> Edit the owning **Tier 1** document only.

2) Is this a cross-cutting feature change that spans multiple Tier 1 docs?  
   -> Start from the relevant **Tier 2** hub, then update the linked Tier 1 docs as needed.

3) Does this feature now touch **3 or more Tier 1 docs repeatedly**?  
   -> Create or update a **Tier 2** hub.

4) In non-owning docs, do not duplicate the rule.  
   -> Replace duplication with a short **doc ID reference** only (for example: `Canonical rule: See DOCS_INDEX -> 13`).

5) If document ownership or routing changes, update **DOCS_INDEX.md first**.

6) If a Tier 2 hub and a Tier 1 doc disagree on low-level rules, **Tier 1 wins**.

7) If `DOCS_INDEX.md` and another doc disagree on routing/ownership, **DOCS_INDEX.md wins**.

8) Prefer **normative references** for owner docs and **informative references** for related context.

9) Keep body references **numeric-only**.  
   -> Use doc IDs as canonical reference targets; rely on filename/title/Purpose/Keywords only for discovery, not for prose references.
