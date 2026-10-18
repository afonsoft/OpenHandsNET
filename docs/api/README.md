# API — contrato público (SPEC-51)

A superfície HTTP pública do OpenHandsNET é documentada por um documento
OpenAPI **gerado do código vivo** — não existe spec escrita à mão para
sincronizar.

| Recurso | Path | Notas |
|---|---|---|
| Documento OpenAPI | `GET /openapi/v1.json` | sempre público; é o contrato |
| Explorer (Scalar) | `GET /scalar/v1` | opt-in — ver flag abaixo |

## Explorer

O explorer Scalar só sobe quando `Api:OpenApiExplorer=true`. Default:
habilitado em `Development`, **desligado em produção**.

```bash
OPENHANDSNET_Api__OpenApiExplorer=true   # habilita /scalar/v1
```

Sem a flag (produção), `/scalar/v1` não é rota — cai no SPA fallback.

## Drift guard (golden snapshot)

`tests/OpenHandsNET.IntegrationTests/Golden/openapi-v1.json` é o
contrato congelado: o teste `Spec51OpenApiContractTests` normaliza o
documento vivo (ordenação recursiva de chaves, sem `servers`) e compara
byte a byte. Endpoint novo/removido/alterado → **fail com diff**.

Refresh consciente (revisão humana obrigatória — o snapshot **é** o
contrato):

```bash
OPENHANDSNET_UPDATE_GOLDEN=1 dotnet test \
    tests/OpenHandsNET.IntegrationTests \
    --filter "FullyQualifiedName~Spec51"
# commite Golden/openapi-v1.json junto com a mudança de endpoint
```

## Gerando um client

Qualquer gerador compatível com OpenAPI 3.x funciona:

```bash
# openapi-generator
openapi-generator-cli generate \
    -i http://localhost:8080/openapi/v1.json \
    -g csharp -o clients/csharp

# Microsoft Kiota
kiota generate -d http://localhost:8080/openapi/v1.json \
    -l csharp -o clients/ohn
```

## Escopo do documento

- Inclui: `/api/v1/*`, `/api/acp/*`, `/oauth/*`, `/v1/chat/completions`.
- Exclui: hubs SignalR, health checks, readiness detalhado
  (`ExcludeFromDescription`) e endpoints marcados internos.
- Auth documentada: cookie session ou `Authorization: Bearer sk-*`.
- Toda operação tem `summary` (padrão `METHOD /path` via transformer,
  ou `.WithSummary` explícito no endpoint).

## Referências

- `docs/api/openai-gateway.md` — gateway OpenAI-compat (detalhes).
- `docs/reference/openhands-upstream/` — baseline upstream de formato.
