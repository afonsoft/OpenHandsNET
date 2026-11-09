---
paths: ["**/*.cs", "**/*.razor", "**/*.csproj"]
---

# .NET Rules — Sandcastle

- Target: `net10.0`, C# 14, nullable enabled, file-scoped namespaces.
- `dotnet format --verify-no-changes` é gate de CI — rodar antes de commitar.
- DI: construtor primário; `ILogger<T>`; registrar em
  `Infrastructure/DependencyInjection.cs` / `Api/Program.cs`.
- EF Core: duas migrations por mudança (`Migrations/` SQLite +
  `Migrations/Postgres/`); gerar via
  `dotnet ef --startup-project src/Sandcastle.Api`.
- Testes: xUnit + Shouldly; `tests/Sandcastle.UnitTests` (fast),
  `IntegrationTests` (Testcontainers/Docker), `Client.Tests` (bUnit),
  `SandboxAgent.Tests`. Convenção `Spec{NN}*Tests.cs` por SPEC.
- Blazor WASM: cliente em `src/Sandcastle.Client`; chamadas via
  `SandcastleApiClient` (nunca `HttpClient` cru em components).
- Comentários XML em pt-BR; identificadores em inglês.
