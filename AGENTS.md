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
- For all newly written code, add XML docstrings for each class, member function, and member variable.
- Keep docstrings concise and practical (purpose, key behavior, and important constraints).

### C# Source Layout Policy
- Put all C# game-logic code under `src/`.
- Use subfolders inside `src/` (for example `src/runtime`, `src/vfs`) instead of creating C# logic folders at project root.
