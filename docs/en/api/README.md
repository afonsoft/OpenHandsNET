# API — public contract (SPEC-51)

The OpenHandsNET public HTTP surface is documented by an OpenAPI
document **generated from live code** — there is no hand-written spec
to keep in sync.

| Resource | Path | Notes |
|---|---|---|
| OpenAPI document | `GET /openapi/v1.json` | always public; it IS the contract |
| Explorer (Scalar) | `GET /scalar/v1` | opt-in — see flag below |

## Explorer

The Scalar explorer only runs when `Api:OpenApiExplorer=true`. Default:
enabled in `Development`, **off in production**.

```bash
OPENHANDSNET_Api__OpenApiExplorer=true   # enables /scalar/v1
```

Without the flag (production), `/scalar/v1` is not a route — it falls
through to the SPA fallback.

## Drift guard (golden snapshot)

`tests/OpenHandsNET.IntegrationTests/Golden/openapi-v1.json` is the
frozen contract: `Spec51OpenApiContractTests` normalizes the live
document (recursive key ordering, no `servers`) and compares it byte
for byte. Any endpoint added/removed/changed → **fail with diff**.

Conscious refresh (human review required — the snapshot **is** the
contract):

```bash
OPENHANDSNET_UPDATE_GOLDEN=1 dotnet test \
    tests/OpenHandsNET.IntegrationTests \
    --filter "FullyQualifiedName~Spec51"
# commit Golden/openapi-v1.json together with the endpoint change
```

## Generating a client

Any OpenAPI 3.x-compatible generator works:

```bash
# openapi-generator
openapi-generator-cli generate \
    -i http://localhost:8080/openapi/v1.json \
    -g csharp -o clients/csharp

# Microsoft Kiota
kiota generate -d http://localhost:8080/openapi/v1.json \
    -l csharp -o clients/ohn
```

## Document scope

- Includes: `/api/v1/*`, `/api/acp/*`, `/oauth/*`,
  `/v1/chat/completions`.
- Excludes: SignalR hubs, health checks, detailed readiness
  (`ExcludeFromDescription`) and internal-marked endpoints.
- Documented auth: session cookie or `Authorization: Bearer sk-*`.
- Every operation carries a `summary` (default `METHOD /path` via
  transformer, or explicit `.WithSummary` on the endpoint).

## References

- `docs/en/api/openai-gateway.md` — OpenAI-compat gateway (details).
- `docs/reference/openhands-upstream/` — upstream format baseline.
