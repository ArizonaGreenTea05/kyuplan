# kyuplan

Planner for the karate club's belt exams, built on the club's [Spond](https://spond.com) groups. Its heart is the list of **exams**:
for an exam that is still to come it **suggests who is fit for it** (from their belt, the day they got it, their age and their
attendance) and lets you **save the members you want to put forward**; for an exam that took place it shows **who attended and who
received which belt**. Underneath is the attendance analysis the planner started as.

A backend API reads Spond; a **Blazor WebAssembly** client and a **WPF** client show the same pages using the same view models. Both
clients speak **English and German**.

## Try it

Requires the **.NET 10 SDK** (the WPF client needs Windows). The API starts in demo mode in `Development`, with generated data and no
Spond credentials:

```bash
dotnet run --project src/Kyuplan.Api --launch-profile http   # http://localhost:5100: web client, API below /api, docs at /scalar
dotnet run --project src/Kyuplan.Client.Wpf                  # Windows desktop app
```

A new installation shows a setup screen in the web client (settings and the first administrator); see
[the reference](docs/reference.md#run-it).

## Solution layout

| Project | What it is |
| --- | --- |
| `src/Kyuplan.Contracts` | DTOs shared by the API and the clients (the wire format) and shared rules. |
| `src/Kyuplan.Api` | ASP.NET Core minimal API: Spond access, statistics, SQLite/PostgreSQL persistence, accounts. |
| `src/Kyuplan.Client.Shared` | The typed API client, **all view models** and **every UI text** (`Resources/Strings*.resx`). |
| `src/Kyuplan.Client.Blazor` / `src/Kyuplan.Client.Wpf` | The two UIs: markup only, bound to the shared view models. |
| `tests/Kyuplan.Tests` | xUnit tests for everything above except the markup. |
| `docs/`, `website/` | The documentation and the Docusaurus sites built from it. |

## Documentation

| I want to … | Read |
| --- | --- |
| **use** kyuplan | [User guide](docs/user-guide.md) |
| **install** it (Docker, NAS, cloud) | [Deployment guide](docs/deployment.md) |
| **manage** an instance | [Administrator guide](docs/admin-guide.md) |
| **change the code** | [Developer guide](docs/developer-guide.md) |
| look up a configuration key, an API endpoint or a database table | [Reference](docs/reference.md) |
| understand the rules behind the figures | [Statistics](docs/attendance-statistics.md), [exam planning](docs/exam-planning.md), [comments and tags](docs/comments-and-tags.md) |

The full index is [docs/README.md](docs/README.md). Release engineering (`installer`, `deploy`, `.github`) is described in the
[deployment guide](docs/deployment.md); notes for coding agents are in [AGENTS.md](AGENTS.md).

## Release

Pushing one tag `v<version>` builds the Docker image (API, web client and the desktop installers in one container) and a GitHub release:

```bash
docker run -d -p 8080:8080 -v kyuplan-data:/data ghcr.io/<owner>/kyuplan:latest   # SQLite; open http://localhost:8080/
```
