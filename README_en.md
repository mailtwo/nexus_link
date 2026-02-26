# Nexus Link

> **Terminal-based coding & hacking simulation game prototype** (Godot 4.6 .NET / Windows)

## Screenshots

**Terminal prototype (command execution / known hosts / connect)**

![Terminal prototype](docs/screenshots/terminal_known_hosts.png)

**Built-in editor + MiniScript (tool modification & automation)**

![Built-in editor + MiniScript](docs/screenshots/editor_miniscript.png)

---

## One-liner

Players gather clues across a virtual network/servers/filesystem, then **automate and modify tools in MiniScript** to design their own attack routes.

## Game Concept

- **Terminal-first UX**: The game starts from a Linux-style terminal. As the game progresses, the workstation's capabilities gradually expand.
- **Toolchain-driven play**: You're not someone who presses predefined commands — you're someone who **builds the tools**.
- **Reasoning-based progression**: Observe → hypothesize → experiment (modify tools) → interpret results. Difficulty scales through this loop.

> Note: This repository intentionally minimizes spoilers and scenario resources in the README.

## Safe Virtualization Principles

This project enforces the following constraints:

- **No real OS access**: No access to local disk, processes, registry, or any real-world resources.
- **No real network access**: No sockets, HTTP, SSH, or any real-world communication.
- All interactions are handled exclusively through the **virtual world simulator + sandboxed API**.

## Core Design Points (Summary)

### 1) World Runtime Model
- World state is structured around `serverList(nodeId)` / `ipIndex` / `processList`.
- Time is managed via **fixed WorldTick (60Hz)** accumulation — not render FPS — to ensure determinism.

### 2) VFS (Virtual Filesystem)
- Structure: **Base + Overlay + Tombstone + BlobStore**
  - Base is the shared "OS image", Overlay is per-server deltas, Tombstone marks base file deletions.
- Uses an overlay model ("store only changes") to prevent cost explosion at 200+ server nodes.

### 3) Scripting (MiniScript)
- Player-created tools are written and modified in MiniScript.
- Library scripts are declared with the `@name` convention and reused via `import`.
- Long-term: CPU/RAM constraints will be tied to an **upgrade (progression) system**.

### 4) In-game Sandbox API
- MiniScript intrinsic surface is designed around `term/time/fs/net/ssh/ftp`.
- All APIs target **permission checks + cost + trace** conventions.

### 5) Event/Mission Dispatch
- Events like `privilegeAcquire` / `fileAcquire` are dispatched through an event system,
  with scenario handlers (guard/action) enabling data-driven mission progression.

### 6) Multi-window UX
- Goal: multiple windows alive simultaneously, like a movie hacking scene.
- Supports two modes: **NATIVE_OS** and **VIRTUAL_DESKTOP**.
- In NATIVE_OS mode, a **DesktopOverlay** (background window) can be toggled per monitor.
- MVP includes the **SSH Login window (sub-window)** contract.

## Current Implementation Scope (Prototype)

- Blueprint loading/parsing pipeline (`src/blueprint`)
- World runtime bootstrap + addressing + blueprint application (`src/runtime/world`)
- VFS foundation + overlay merge rules (`src/vfs`)
- System call core (parser/registry/dispatcher) (`src/runtime/syscalls`)
- VFS syscall module (phase 1): `pwd`, `ls`, `cd`, `cat`, `mkdir`, `rm`

## Repository Structure

```text
src/
  blueprint/          # Blueprint schemas + YAML reader
  runtime/
    world/            # World runtime state/build/bootstrap/system-call entrypoint
    syscalls/         # System call core + modules
  vfs/                # Base virtual filesystem and path/merge logic
scenes/               # Godot scenes (main: TerminalScene.tscn)
scripts/              # GDScript/UI side scripts
plans/                # Design & specification documents (SSOT)
  DOCS_INDEX.md       # Document routing table — read this first before implementing
  DECISIONS.md        # Design decision log — context for past decisions
docs/
  screenshots/        # README screenshots
scenario_content/     # Runtime scenario resources and content data
```

## Tech Stack

- Godot 4.6 (.NET)
- C# / .NET 8
- YamlDotNet (blueprint parsing)

## Build & Run

### Prerequisites

- Godot 4.6 .NET edition
- .NET SDK 8.x

### Build

```bash
dotnet build Uplink2.sln
```

### Run

```bash
# Option 1) Open project.godot in Godot Editor and run
# Option 2) CLI
godot --path .
```

Main scene: `res://scenes/TerminalScene.tscn`

## Documentation (SSOT: Source of Truth)

All design specs, rules, and data schemas live in the `plans/` folder.  
See **`plans/DOCS_INDEX.md`** for document routing rules.

| Document | Contents |
|---|---|
| `00_overview.md` | Project vision / philosophy / scope |
| `01_existing_hacking_games.md` | Genre reference analysis |
| `02_miniscript_interpreter_and_constraints.md` | MiniScript runtime / constraints |
| `03_game_api_modules.md` | Sandbox API surface + `@name` convention |
| `04_attack_routes_and_missions.md` | Attack routes / mission templates + hint system |
| `07_ui_terminal_prototype_godot.md` | Terminal/editor UX + CodeEdit autocomplete/tooltip |
| `08_vfs_overlay_design_v0.md` | VFS overlay architecture |
| `09_server_node_runtime_schema_v0.md` | World runtime schema |
| `10_blueprint_schema_v0.md` | Blueprint schema / world generation rules |
| `11_event_handler_spec_v0_1.md` | Event system / guard execution contract |
| `12_save_load_persistence_spec_v0_1.md` | Save/load persistence spec |
| `13_multi_window_engine_contract_v1.md` | Multi-window + DesktopOverlay engine contract |
| `14_official_programs.md` | Official program contracts (`scripts`, etc.) |
| `15_game_flow_design.md` | Game flow / onboarding / player journey design |

## Roadmap (High Level)

- Syscall domain expansion: `run`, `connect`, network/process/transfer
- MiniScript Runner integration + CPU/RAM constraint system
- Event/flag-based mission progression (guard/action) expansion
- Multi-window expansion (DesktopOverlay, tracing/topology/transfer queue, etc.)
- `scripts` official program + MiniScript `import` library system
