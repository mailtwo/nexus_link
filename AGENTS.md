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

### Code Documentation Policy
- Add XML docstrings for all `public`/`protected` classes and members.
- For `private` members, write docstrings only when the logic is non-obvious or risky.
- Default to one-line summary docstrings unless extra detail is truly necessary.
- Use `<inheritdoc/>` when implementing or overriding already-documented members.
- Prefer grouped docs for related fields over repetitive per-variable docstrings.

### C# Source Layout Policy
- Put all C# game-logic code under `src/`.
- Use subfolders inside `src/` (for example `src/runtime`, `src/vfs`) instead of creating C# logic folders at project root.
