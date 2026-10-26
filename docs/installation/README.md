# Instalação

Guias de instalação e configuração do OpenHandsNET.
English: [../en/installation/](../en/installation/).

| Guia | Conteúdo |
|---|---|
| [docker.md](docker.md) | Docker / Docker Compose — todos os profiles (OSS, Postgres, runtime-split, observabilidade, enterprise) |
| [host.md](host.md) | Instalação em host (desenvolvimento e produção systemd, sem container para o app) |
| [environment-variables.md](environment-variables.md) | Referência completa de variáveis de ambiente `OPENHANDSNET_*` |

## Qual caminho escolher

| Cenário | Guia |
|---|---|
| Avaliar o produto rápido | [docker.md](docker.md) — `docker compose up -d --build` |
| Desenvolver no repo | [host.md](host.md) — `dotnet run --project src/OpenHandsNET.Api` |
| Self-host single-node | [docker.md](docker.md) + `docker.sock` montado |
| Self-host multi-réplica | [docker.md](docker.md#runtime-split) + Postgres + Redis |
| Servidor sem Docker para o app | [host.md](host.md#producao-systemd) (o Docker ainda é necessário para sandboxes) |

> **Nota sobre Docker**: mesmo na instalação em host, cada conversa cria um
> container sandbox — o daemon Docker é sempre requisito do runtime do agente,
> exceto quando `Sandbox__Provider=remote`/`kubernetes` delega a outro host.

## Screenshots (Docker, app em :3032)

| Tela | Imagem |
|---|---|
| Home (composer de conversa) | ![Home](../images/app-home.png) |
| Configurações (tema, idioma, LLM, secrets) | ![Settings](../images/app-settings.png) |
| Automações (agendamento + webhook) | ![Automations](../images/app-automations.png) |
