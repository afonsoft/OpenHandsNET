---
paths: ["**/*.cs", "**/*.razor", "**/*.csproj"]
---

# .NET Rules — OpenHandsNET

- Target: `net10.0`, C# 14, nullable enabled, file-scoped namespaces.
- `dotnet format --verify-no-changes` é gate de CI — rodar antes de commitar.
- DI: construtor primário; `ILogger<T>`; registrar em
  `Infrastructure/DependencyInjection.cs` / `Api/Program.cs`.
- EF Core: duas migrations por mudança (`Migrations/` SQLite +
  `Migrations/Postgres/`); gerar via
  `dotnet ef --startup-project src/OpenHandsNET.Api`.
- Testes: xUnit + Shouldly; `tests/OpenHandsNET.UnitTests` (fast),
  `IntegrationTests` (Testcontainers/Docker), `Client.Tests` (bUnit),
  `SandboxAgent.Tests`. Convenção `Spec{NN}*Tests.cs` por SPEC.
- Blazor WASM: cliente em `src/OpenHandsNET.Client`; chamadas via
  `OpenHandsNetApiClient` (nunca `HttpClient` cru em components).
- Comentários XML em pt-BR; identificadores em inglês.
