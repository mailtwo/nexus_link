# uplink2 (working title)

Terminal-first coding/hacking simulation game prototype built with Godot .NET.

This repository focuses on a safe, fully virtual world model:
- No real OS access
- No real network access
- Gameplay based on strategy, inference, and automation

Prototype scenario content is intentionally omitted from this README.

## Project Goals

- Deliver a Linux-like terminal UX as the main play surface
- Let players modify and automate tools through MiniScript-based programs
- Build a scalable runtime model for many virtual server nodes
- Keep all hacking interactions abstracted and game-safe

## Core Design Pillars

- Terminal-first UX with optional editor overlay (`LineEdit` + `RichTextLabel` + `CodeEdit`)
- Data-driven world generation via blueprint schemas
- Server runtime model centered on `serverList(nodeId)`, `ipIndex`, `processList`
- Virtual filesystem architecture based on `Base + Overlay + Tombstone + BlobStore`
- Extensible syscall processor architecture (module/registry/dispatcher pattern)

## Current Implementation Scope

- Blueprint loading/parsing pipeline (`src/blueprint`)
- World runtime bootstrap + addressing + blueprint application (`src/runtime/world`)
- VFS foundation and overlay merge rules (`src/vfs`)
- System call core contracts/parser/registry/processor (`src/runtime/syscalls`)
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
plans/                # Design and implementation plan documents
scenario_content/     # Runtime scenario resources and content data
```

## Tech Stack

- Godot 4.6 (.NET)
- C# / .NET 8
- YamlDotNet (blueprint parsing)

## Build and Run

### Prerequisites

- Godot 4.6 .NET edition
- .NET SDK 8.x

### Build

```bash
dotnet build Uplink2.sln
```

### Run

```bash
# Option 1: Open project.godot in Godot Editor and run
# Option 2: CLI
godot --path .
```

Main scene: `res://scenes/TerminalScene.tscn`

## Plan Documents (Source of Truth)

- `plans/00_overview.md` - overall direction and document map
- `plans/02_miniscript_interpreter_and_constraints.md` - script runtime model
- `plans/03_game_api_modules.md` - sandbox API surface
- `plans/07_ui_terminal_prototype_godot.md` - terminal/editor UX in Godot
- `plans/08_vfs_overlay_design_v0.md` - VFS overlay architecture
- `plans/09_server_node_runtime_schema_v0.md` - world runtime schema
- `plans/10_blueprint_schema_v0.md` - blueprint data schema and world instantiation rules

## Roadmap (High Level)

- Expand syscall modules beyond VFS (`run`, `connect`, network/process domains)
- Integrate MiniScript runner with resource constraints (CPU/RAM budget model)
- Extend mission/event progression through runtime flags and action pipelines

