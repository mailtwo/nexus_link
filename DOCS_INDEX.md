# DOCS_INDEX.md — Documentation Routing (SSOT)

This file is the **single source of truth** for:
- What each document covers
- Where new/updated information must be written (SSOT routing)
- What **must not** be duplicated elsewhere

If you are implementing a feature, modifying specs, or updating docs, **start here**.

---

## Global rules

1) **Write specs only in the SSOT doc for that topic.**  
2) Other docs may mention the topic **only by reference** (link + one-line context).  
3) Legacy/deprecated docs are **read-only** unless explicitly stated.  
4) When you discover a mismatch, report it as:  
   `DocA <section> vs DocB <section> — conflict: <1–2 lines>`.

---

## Quick routing table

| You are changing… | SSOT doc to edit |
|---|---|
| Market/genre research & differentiation | `01_existing_hacking_games.md` |
| MiniScript language embedding / interpreter integration (not API list) | `02_miniscript_interpreter_and_constraints.md` |
| MiniScript **intrinsics** (public API surface, ResultMap shapes, error codes) | `03_game_api_modules.md` |
| Player strategies / missions / routes / gameplay ideas | `04_attack_routes_and_missions.md` |
| Terminal UX + **system calls** + command behavior | `07_ui_terminal_prototype_godot.md` |
| Virtual File System (overlay/permissions/path resolution/data model) | `08_vfs_overlay_design_v0.md` |
| Runtime storage schema for server nodes (what gets stored) | `09_server_node_runtime_schema_v0.md` |
| Blueprint/schema (how designers author scenarios; how blueprints map into runtime init) | `10_blueprint_schema_v0.md` |
| Runtime simulation logic (process scheduler + event handlers) | `11_event_handler_spec_v0_1.md` |
| Save/Load and persistence boundaries | `12_save_load_persistence_spec_v0_1.md` |
| Multi-window engine contract | `13_multi_window_engine_contract_v1.md` |
| Official programs (ExecutableHardcode/ExecutableScript) and their contracts | `14_official_programs.md` |
| 게임 플로우 / 온보딩 / 튜토리얼 / 플레이어 여정 설계 | `15_game_flow_design.md` |

---

## Document map (00–14)

### 00 — `00_overview.md` (ACTIVE)
**Purpose:** Project overview, philosophy, and the “big picture” (what the game is, and what it is not).  
**SSOT for:** High-level vision only (pillars, scope boundaries, non-goals).  
**Must include:** A prominent reference to `DOCS_INDEX.md` as the SSOT router for all documentation.  
**Must NOT include:** Any routing table, per-doc scope descriptions, schemas, APIs, system calls, persistence rules, or program contracts. Those belong in `DOCS_INDEX.md` and the topic SSOT docs.

---

### 01 — `01_existing_hacking_games.md` (ACTIVE)
**Purpose:** Research on existing hacking/coding games and this game’s differentiation.  
**SSOT for:** Competitive analysis, reference mechanics, design positioning.  
**Must NOT include:** Implementation specs, runtime schemas, API definitions.

---

### 02 — `02_miniscript_interpreter_and_constraints.md` (ACTIVE)
**Purpose:** How MiniScript is integrated into the project (embedding approach, constraints, safety/time-slicing constraints at a conceptual level).  
**SSOT for:** Interpreter integration decisions and constraints (project-level).  
**Must NOT include:** Concrete intrinsic/API list (belongs to 03), terminal system calls (07), runtime engine schemas (09/10), or program contracts (14).

---

### 03 — `03_game_api_modules.md` (ACTIVE, SSOT)
**Purpose:** MiniScript intrinsic API reference (player-facing API surface).  
**SSOT for:** Modules/functions, argument/return shapes, error codes, ResultMap conventions, shared limits, trace/cost conventions **for intrinsics**.  
**Must NOT include:** Terminal system call UI/UX details (07), VFS internal implementation (08), runtime schema (09), blueprint authoring rules (10), program-level contracts (14) except by reference.

---

### 04 — `04_attack_routes_and_missions.md` (ACTIVE)
**Purpose:** Gameplay/mission ideas and player routes (design notebook).  
**SSOT for:** Player strategy concepts, mission structure, route templates,
힌트 시스템 및 hint agent 설계 (§7).  
**Must NOT include:** Definitive API/system-call specs (03/07), schemas (08–12), engine contracts (13).

---

### 05 — `05_ui_terminal_prototype.md` (LEGACY, READ-ONLY)
**Purpose:** Legacy UI/terminal prototype notes (Unity-era).  
**SSOT for:** Nothing (historical reference only).  
**Update policy:** Do not update. If something is still relevant, migrate to `07` and leave a short note here if needed.

---

### 06 — `06_server_nodes_design_v0.md` (DEPRECATED, READ-ONLY)
**Purpose:** Deprecated prototype scenario notes / early server node design.  
**SSOT for:** Nothing (deprecated).  
**Update policy:** Do not add new rules/specs. If a concept remains valid, rewrite it in the proper SSOT doc and reference it from here.

---

### 07 — `07_ui_terminal_prototype_godot.md` (ACTIVE, SSOT)
**Purpose:** Godot terminal implementation + **system call** definitions + command UX.  
**SSOT for:** System call list/behavior, terminal parsing rules, command outputs/format, UI interactions tied to system calls.  
**Must NOT include:** Program contracts (ExecutableHardcode/Script) and official tools (14), intrinsic API details (03), VFS internals (08) except by reference.

---

### 08 — `08_vfs_overlay_design_v0.md` (ACTIVE, SSOT)
**Purpose:** Virtual File System implementation design.  
**SSOT for:** VFS overlay model, permissions, tombstones, dir deltas, path normalization rules (VFS-level), file kinds and execution flags **as VFS data model**.  
**Must NOT include:** Terminal system call behaviors (`ls/cd/cat/edit/...`) beyond what is strictly required to define VFS semantics (route to 07), intrinsic wrappers (03), save/load policy (12) except by reference.

---

### 09 — `09_server_node_runtime_schema_v0.md` (ACTIVE, SSOT)
**Purpose:** Server runtime data schema (what each server stores while running).  
**SSOT for:** Runtime storage fields/structures, indexes, caches, persistence-related schema details.  
**Must NOT include:** Runtime scheduling/execution semantics (11), blueprint authoring and mapping rules (10), API semantics (03) except by reference.

---

### 10 — `10_blueprint_schema_v0.md` (ACTIVE, SSOT)
**Purpose:** Scenario/blueprint authoring schema + rules for loading/initializing runtime from blueprint.  
**SSOT for:** Blueprint schema, designer-facing authoring rules, blueprint→runtime initialization mapping.  
**Must NOT include:** Runtime processing engine semantics (11), intrinsic API details (03), VFS internals (08) except by reference.

---

### 11 — `11_event_handler_spec_v0_1.md` (ACTIVE, SSOT)
**Purpose:** Runtime simulation logic: processes + event handlers.  
**SSOT for:** Scheduling model, time slicing, handler dispatch, guard rules, once-only semantics, runtime execution policies.  
**Must NOT include:** Persistence format/boundaries (12) except by reference; schema-only storage fields (09) except by reference.

---

### 12 — `12_save_load_persistence_spec_v0_1.md` (ACTIVE, SSOT)
**Purpose:** Save/Load and persistence boundaries.  
**SSOT for:** What is persisted, snapshot format, versioning, excluded transient state, load reconstruction rules.  
**Must NOT include:** UI/multi-window implementation details (13) beyond what must be persisted (reference-only).

---

### 13 — `13_multi_window_engine_contract_v1.md` (ACTIVE, SSOT)
**Purpose:** Multi-window support contract (engine-facing).  
**SSOT for:** Window lifecycle, mode switching requirements, layout persistence requirements **as an engine contract**.  
**Must NOT include:** Save/load format or persistence policy (12 is SSOT; reference-only).

---

### 14 — `14_official_programs.md` (ACTIVE, SSOT)
**Purpose:** Official programs shipped as `ExecutableHardcode` / `ExecutableScript`, and their contracts.  
**SSOT for:** Program behavior contracts (e.g., inspect), program-level error semantics, gating rules, trace/cost behavior for official tools.  
**Must NOT include:** Intrinsic API definitions (03) except “API wrapper references program contract”.

---

### 15 — `15_game_flow_design.md` (ACTIVE)
**Purpose:** 플레이어 전체 여정 설계 (온보딩, 코딩 유도, 중반 흐름, 최종 미션 유도).  
**SSOT for:** 게임 시작 연출, 재시작/로드 정책, 타겟 플레이어 정의, 플레이어 여정 흐름 설계.  
**Must NOT include:** 개별 미션 재료/공격 루트 (04), 힌트 시스템/hint agent 상세 (04 §7),
인게임 API (03), 공식 프로그램 계약 (14).

---

## Reference style (use this when writing non-SSOT docs)

When a non-SSOT doc needs to mention another domain, use a short pointer:

- **Preferred:** `See DOCS_INDEX.md → <DocID> (<filename>)`  
- **Optional:** Add one line of context, but do **not** restate rules/specs.

Example:
> “SSH inspect hint semantics are defined in `14_official_programs.md` (SSOT). See DOCS_INDEX.md → 14.”

---

## Editing checklist (for Codex)

When you need to change something:
1) Identify the topic → use **Quick routing table**.
2) Edit **only** the SSOT doc.
3) Update other docs with **reference-only** notes if needed.
4) If you touched interfaces between domains (e.g., 10→09 init mapping), ensure both SSOT docs still align, but keep each fact in its SSOT location.
