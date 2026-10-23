<p align="center"><picture><source media="(prefers-color-scheme: dark)" srcset="assets/branding/logo-white.svg"><img src="assets/branding/logo-black.svg" alt="kyuplan" height="72"></picture></p>

<p align="center">
  <b>Plan your karate club's belt exams on the member and event data you already keep.</b><br>
  Open source, self-hosted, one Docker container.
</p>

<p align="center">
<a href="https://github.com/ArizonaGreenTea05/kyuplan/releases"><img alt="Latest release" src="https://img.shields.io/github/v/release/ArizonaGreenTea05/kyuplan?include_prereleases&sort=semver"></a> <a href="https://github.com/ArizonaGreenTea05/kyuplan/pkgs/container/kyuplan"><img alt="Docker image" src="https://img.shields.io/badge/ghcr.io-kyuplan-blue?logo=docker"></a> <a href="https://github.com/ArizonaGreenTea05/kyuplan/issues"><img alt="Open issues" src="https://img.shields.io/github/issues/ArizonaGreenTea05/kyuplan"></a> <a href="https://github.com/ArizonaGreenTea05/kyuplan/pulls"><img alt="Open pull requests" src="https://img.shields.io/github/issues-pr/ArizonaGreenTea05/kyuplan"></a> <a href="https://github.com/ArizonaGreenTea05/kyuplan/commits/main"><img alt="Last commit" src="https://img.shields.io/github/last-commit/ArizonaGreenTea05/kyuplan"></a> <a href="https://github.com/ArizonaGreenTea05/kyuplan/stargazers"><img alt="Stars" src="https://img.shields.io/github/stars/ArizonaGreenTea05/kyuplan?style=flat"></a> <a href="LICENSE"><img alt="MIT licence" src="https://img.shields.io/github/license/ArizonaGreenTea05/kyuplan"></a> <a href="https://ko-fi.com/idkeverygoodnameistaken"><img alt="Sponsor on Ko-fi" src="https://img.shields.io/badge/Ko--fi-sponsor-ff5e5b?logo=ko-fi&logoColor=white"></a>
</p>

<p align="center"><a href="docs/deployment.md">Self-hosting guide</a> · <a href="docs/user-guide.md">User guide</a> · <a href="https://ko-fi.com/idkeverygoodnameistaken">☕ Sponsor on Ko-fi</a></p>

kyuplan reads the groups, members and events of your club from a **data source** and turns them into an **exam planner**. Built in are [Spond](https://spond.com), entering the data by hand and a custom script for any other service; more sources can be added.

- **Suggests who is fit for the next exam** from belt, the day the belt was received, age and training attendance, and lets you save the members you put forward.
- **Shows what happened at past exams**: who attended and who received which belt.
- **Attendance statistics** with filters, comments and tags, exam prices and payments, calendar feeds (ICS), backups.
- **Web and Windows desktop client**, both in **English and German**, with themes and your own club branding.
- **Your data stays with you**: SQLite or PostgreSQL, accounts with roles and permissions, no cloud service besides the data source you choose.

## Self-host it in two minutes

You need a machine with Docker (a server, a NAS, a VM, a Raspberry Pi: the image is built for amd64 and arm64). Try it first with generated demo data, no account needed:

```bash
docker run -d --name kyuplan -p 8080:8080 -v kyuplan-data:/data -e Spond__UseDemoData=true ghcr.io/arizonagreentea05/kyuplan:latest
```

Open <http://localhost:8080/>: on first start a setup screen asks for the data source and its login (or keeps the demo data) and creates the first administrator.

### For real use

```bash
git clone https://github.com/ArizonaGreenTea05/kyuplan.git && cd kyuplan/deploy
cp .env.example .env          # pin KYUPLAN_IMAGE to a version tag, e.g. ghcr.io/arizonagreentea05/kyuplan:0.1
docker compose up -d          # SQLite in the volume "kyuplan-data"; docker-compose.postgres.yml for PostgreSQL
```

- **Put HTTPS in front of it.** The login is HTTP Basic, so serve the site through a reverse proxy such as Caddy; the guide has a ready-made setup.
- **Back up the `kyuplan-data` volume** (or use the built-in backup page). Update with `docker compose pull && docker compose up -d`.

Step-by-step instructions, PostgreSQL, TrueNAS, Azure, Kubernetes, troubleshooting and every setting are in the [deployment guide](docs/deployment.md).

## Documentation

| I want to … | Read |
| --- | --- |
| **use** kyuplan | [User guide](docs/user-guide.md) |
| **install** it (Docker, NAS, cloud) | [Deployment guide](docs/deployment.md) |
| **manage** an instance | [Administrator guide](docs/admin-guide.md) |
| **change the code** | [Developer guide](docs/developer-guide.md) |
| look up a setting, an API endpoint or a table | [Reference](docs/reference.md) |
| understand the rules behind the figures | [Statistics](docs/attendance-statistics.md), [exam planning](docs/exam-planning.md), [comments and tags](docs/comments-and-tags.md) |

A running instance serves the same documentation for its users, and the API reference at `/scalar` in demo mode. The full index is [docs/README.md](docs/README.md).

## Develop

Needs the **.NET 10 SDK** (the WPF client needs Windows). The API starts in demo mode in `Development`:

```bash
dotnet run --project src/Kyuplan.Api --launch-profile http   # http://localhost:5100: web client, API below /api
dotnet test tests/Kyuplan.Tests
```

Layout, conventions and how to contribute are in the [developer guide](docs/developer-guide.md). Bug reports and ideas are welcome as [issues](https://github.com/ArizonaGreenTea05/kyuplan/issues).

## Support the project

kyuplan is free and developed in spare time. If it saves your club some evenings of spreadsheet work, you can
**[buy me a coffee on Ko-fi](https://ko-fi.com/idkeverygoodnameistaken)** ☕. Starring the repository and telling other clubs helps just as much.

## Licence

[MIT](LICENSE) © ArizonaGreenTea05
