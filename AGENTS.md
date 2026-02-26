## The Golden Rule
When unsure about implementation details, ALWAYS ask the developer.

## Development Guidelines

### Context7 Compliance
Always use Context7 MCP when I need library/API documentation, code generation, setup or configuration steps without me having to explicitly ask.
This project follows context7 principles:
- Do what has been asked; nothing more, nothing less
- NEVER create files unless they're absolutely necessary for achieving your goal
- ALWAYS prefer editing an existing file to creating a new one
- NEVER proactively create documentation files (*.md) or README files unless explicitly requested by the user

### Plans-First Implementation Policy
- For this project, implement features based on the markdown files in the `plans/` folder by default.
- If the developer does not specify details, use the relevant `plans/` documents as the source of truth.
- If a request conflicts with the `plans/` documents, ask the developer for confirmation before proceeding.
- Files in `plans/` are UTF-8 encoded; when reading them in tools/shell, explicitly use UTF-8 encoding.

### Spec Drift Guard
- If implementation is expected to introduce additions or changes that differ from the current documentation, ask the user for approval before implementing.
- If the user decides to proceed, explicitly document the differences in the relevant SSOT document and then implement.
- Do not implement documentation-changing behavior without user confirmation.

## Documentation routing (SSOT)
- Before implementing, changing specs, or updating docs, **read `DOCS_INDEX.md` first**.
- Treat `DOCS_INDEX.md` as the **single source of truth for where each kind of information belongs**.
- When you need to add or change a spec, **edit only the SSOT document for that topic**.
- In non-SSOT documents, do not duplicate the spec; **only add a short reference** (e.g., “See DOCS_INDEX.md → <DocID>”).
- If you find inconsistencies, report them as:  
  `DocA <section> contradicts DocB <section>: <1–2 line summary>`, in Korean.

### Decisions Log
- `DECISIONS.md` records the history of design decisions made in `plans/` documents.
- Only record **planning/design decisions** — not code changes (use git commits for those).
- Always read before implementing to avoid unknowingly reversing prior decisions.
- Each entry must include **what was decided** and **why**.1. list text here

### TODO Policy
- `TODO.md` tracks features specified in `plans/` documents
  that were explicitly deferred by the developer during implementation.
- Add to `TODO.md` only when the developer explicitly says to skip or defer
  a planned feature (e.g., "implement this later", "skip for now").
- Do NOT add entire plan documents to `TODO.md` when a new plan is introduced.
- Remove entries from `TODO.md` once the corresponding feature is implemented.

### Code Documentation Policy
- Add XML docstrings for all `public`/`protected` classes and members.
- For `private` members, write docstrings only when the logic is non-obvious or risky.
- Default to one-line summary docstrings unless extra detail is truly necessary.
- Use `<inheritdoc/>` when implementing or overriding already-documented members.
- Prefer grouped docs for related fields over repetitive per-variable docstrings.
- Exception: for Player API intrinsics under `src/runtime/miniscript`, apply extended XML docstrings to `Inject*Module` and `Register*Intrinsic` methods.
- The extended XML docstring format is defined only in `plans/03_game_api_modules.md` (SSOT). Do not duplicate that format in this file; See `DOCS_INDEX.md` → 03.

### C# Source Layout Policy
- Put all C# game-logic code under `src/`.
- Use subfolders inside `src/` (for example `src/runtime`, `src/vfs`) instead of creating C# logic folders at project root.

### MiniScript Vendor Boundary Policy
- Treat `src/MiniScript-cs/` as vendored upstream code and do not modify files in this folder unless the developer explicitly requests it.
- Implement project-specific MiniScript intrinsic bindings/integration outside `src/MiniScript-cs/` (for example under `src/runtime/miniscript`).
- If intrinsic/API behavior needs changes, prefer wrapper/adapter/registration code in project runtime first; only patch vendored sources as a last resort with explicit developer approval.