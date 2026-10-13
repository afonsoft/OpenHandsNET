# .NET Conventions

When working in this repository follow these conventions:

- `async`/`await` everywhere — suffix `Async`, accept `CancellationToken`.
- Immutable `record` types for DTOs/events; `sealed` classes by default.
- Nullable reference types on; fix all warnings (warnings are errors).
- JSON payloads use snake_case naming.
- Prefer minimal APIs and constructor injection.
- Run `dotnet build` and `dotnet test` before considering work done.
