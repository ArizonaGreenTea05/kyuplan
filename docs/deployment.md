# Releases and deployment

> **Where this fits:** this is the deployment guide (and the release notes for maintainers). Day-to-day management is in the
> [admin guide](admin-guide.md), end users read the [user guide](user-guide.md), contributors the [developer guide](developer-guide.md).

- [Step-by-step: your first deployment](#step-by-step-your-first-deployment)
- [Releasing](#releasing) · [Where and how to run it](#where-and-how-to-run-it-overview) · [The server image](#the-server-image)
- [Choosing the database](#choosing-the-database) · [HTTPS and reverse proxies](#https-and-reverse-proxies)
- [Azure](#azure) · [Other clouds](#other-clouds-and-platforms) · [Kubernetes](#kubernetes) · [TrueNAS](#truenas)
- [Backup, restore and updates](#backup-restore-and-updates) · [Troubleshooting](#troubleshooting)
- [The WPF client installer](#the-wpf-client-installer) · [Building the pieces by hand](#building-the-pieces-by-hand)

## Step-by-step: your first deployment

This walks through the most common case: **one Linux (or Windows/macOS) machine with Docker, SQLite, HTTPS through Caddy**. Other places
(a NAS, a cloud, Kubernetes, PostgreSQL) use the same image and the same settings; the sections below say what differs.

### 0. Before you start

You need:

- a machine that runs Docker (Docker Engine with the Compose plugin, Docker Desktop, Podman, a NAS app store, ...) and keeps a folder or volume across restarts (**a local disk, not a network share**: SQLite needs it),
- a **host name** (for example `kyuplan.example.org`) pointing at the machine, and ports 80 and 443 open if you want an automatic certificate,
- the **Spond account** of the club: an administrator of the groups to analyse, without two-factor authentication (or use demo data first),
- the image: `ghcr.io/<owner>/kyuplan:<version>`. If the repository is private, run `docker login ghcr.io` with a token that has `read:packages` first (see [GitHub settings](#github-settings)).
- plan the **backup** now: where will a copy of the data go?

### 1. Get the Compose files

```bash
git clone https://github.com/<owner>/<repo>.git kyuplan && cd kyuplan/deploy      # or just copy deploy/docker-compose.yml and .env.example
cp .env.example .env
```

Edit `.env`: set `KYUPLAN_IMAGE` to a **pinned** tag (for example `ghcr.io/<owner>/kyuplan:0.1`), and leave the rest. Nothing else is required: Spond
login and administrator are entered in the browser. (For PostgreSQL use `docker-compose.postgres.yml` and set `POSTGRES_PASSWORD`.)

### 2. Put HTTPS in front

The login is HTTP Basic, so the site **must** be served over HTTPS (except for a try-out on the machine itself). The simplest way is Caddy in the same
Compose project. Create `deploy/Caddyfile`:

```
kyuplan.example.org {
    reverse_proxy kyuplan:8080
}
```

and add to your Compose file (next to the `kyuplan` service; take the published `ports:` away from kyuplan so that only Caddy is reachable, and set
`ASPNETCORE_FORWARDEDHEADERS_ENABLED: "true"` in the `kyuplan` environment):

```yaml
  caddy:
    image: caddy:2
    restart: unless-stopped
    ports: ["80:80", "443:443"]
    volumes:
      - ./Caddyfile:/etc/caddy/Caddyfile:ro
      - caddy-data:/data
volumes:
  caddy-data:
```

On a **home network without HTTPS** you may instead set `Api__AllowInsecureHttp: "true"` and open `http://<host>:8080/`: only if nobody untrusted
can reach that network. Any other proxy works as well (nginx, Traefik, a cloud load balancer, Cloudflare Tunnel, ...); see [HTTPS and reverse proxies](#https-and-reverse-proxies).

### 3. Start it

```bash
docker compose up -d
docker compose ps                  # kyuplan should become "healthy"
docker compose logs -f kyuplan     # Ctrl+C to leave
```

`GET /healthz` answers `200 ok` when the application and its database are alive.

### 4. Do the setup in the browser (right away)

Open `https://kyuplan.example.org/`. A new installation shows the **setup screen**: the Spond login (or *Use demo data*), time zone, exam
keyword, default thresholds, sync, allowed host names and the **first administrator** (12 to 128 character password). The setup needs no login and the first visitor becomes the administrator, so do it right away, or set `Accounts__InitialAdminPassword` and skip it. Moving the settings file (*settings file location*) is only accepted when the setup is opened on the machine the API runs on. Press *Finish the setup*;
the application restarts itself inside the container and then asks for the login of the administrator.

> Whoever opens the page first creates the administrator. Do this before telling anybody else the address.

Alternatively, create the administrator by configuration (`Accounts__InitialAdminPassword`) and enter the rest on the **Settings** page.

### 5. Check that it works

1. Sign in with the administrator. The **Members** page fills within a minute (the first sync runs at start).
2. If you used demo data, a "Demo data" marker is shown; switch it off on the Settings page before real use.
3. Download the desktop app from the button at the bottom left (Windows) and sign in once.

### 6. Hand it over to the people

Continue in the [admin guide](admin-guide.md): create roles and users, prepare the belts and member data, set the branding. Then tell your users
the address and give them the [user guide](user-guide.md).

### 7. Operate it

- **Backup**: schedule a copy of the volume (stop the container briefly for SQLite) or `pg_dump`, plus `settings.json` and `keys/`. See [Backup, restore and updates](#backup-restore-and-updates). Test a restore.
- **Updates**: change the tag in `.env`, `docker compose pull && docker compose up -d`. Back up first.
- **Logs**: `docker compose logs kyuplan`, and files in the data volume (`/data/logs`).
- **Exactly one container.** Never scale the service.

### Quick reference: the settings you most likely set

| Environment variable | Purpose |
| --- | --- |
| `KYUPLAN_IMAGE`, `KYUPLAN_PORT` | Compose variables: image tag and published port |
| `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` | Required behind a reverse proxy that ends HTTPS |
| `Api__AllowInsecureHttp=true` | Trusted network without HTTPS |
| `Api__TrustedProxies__0=10.0.0.5` (more: `__1`, ...) | The reverse proxies whose `X-Forwarded-*` headers are believed (addresses or networks like `10.0.0.0/8`); default: loopback and the private networks. Anybody else who sends them is ignored. Set it to your proxy's address when the container's port is reachable from other machines. |
| `AllowedHosts` | Host names the site answers to (the setup proposes the address in use) |
| `Spond__Email`, `Spond__Password`, `Spond__UseDemoData` | Spond login or demo mode (usually entered in the browser) |
| `Accounts__InitialAdminPassword` | Create the administrator `admin` without the setup screen |
| `Statistics__TimeZone` | The club's time zone (default `Europe/Berlin`) |
| `Database__Provider`, `Database__ConnectionString`, `Accounts__ConnectionString` | PostgreSQL instead of SQLite |
| `Branding__ProductName`, `Branding__OrganizationName`, `Branding__AccentColor`, `Branding__Currency`, `Branding__Logo`, `Branding__DefaultWebTheme`, `Branding__DefaultWindowsTheme` | Branding basis |
| `Themes__Directory` | Folder of the uploaded theme packs (default: `themes` next to `settings.json`, i.e. `/data/themes`) |
| `DefaultData__Enabled=false` | Start new databases without default belts and roles |

The full list is the configuration table of the [README](../README.md#configuration); a value set as an environment variable wins over the Settings page.

---

The whole framework has **one version** and is released by pushing one tag, `v<version>`. The release is a single Docker image with
everything in it: the API, the web client **and the installers of the desktop clients** (today Windows). To set Kyuplan up, deploy that one container and do the rest in
the web client. It runs **anywhere Docker runs** (see [Where and how to run it](#where-and-how-to-run-it-overview)) and keeps its data in **SQLite (default) or PostgreSQL** (see [Choosing the database](#choosing-the-database)).

| Tag | What the workflow produces |
| --- | --- |
| `v0.1` | Docker image `ghcr.io/<owner>/kyuplan` for `linux/amd64` and `linux/arm64` (API + web client + the installers of the desktop clients) and a GitHub release with the installers (`Kyuplan-windows-0.1.exe`) attached |

How the pieces find each other:

- **One installer per operating system** (`windows`, `macos`, `linux`: `ClientPlatform` in `Kyuplan.Contracts`). Its file is named
  `Kyuplan-<platform>-<version>.<extension>` (`Kyuplan-windows-1.2.3.exe`) and lies in the folder `downloads` next to the program
  (`ClientDownload__Directory` sets another one); of several versions of a platform the newest counts. A build from source or a local
  `docker build` has none; the server then offers no download and no update.
- The server **offers each installer for download**: `GET /api/client/<platform>` (needs a login, like the rest of the API). The web
  client shows an eye-catching *Download for <system>* button in its left panel for the system the browser runs on, when the server has
  an installer for it (none on phones).
- `GET /api/info` carries `ClientVersions`, the version of every installer by platform. An installed **desktop client compares the entry
  of its own platform with its own version** when it has signed in and then every hour; when the server's is newer, an *Update to x.y.z*
  button appears in its left panel. A click downloads the installer from the server, runs it silently (the WPF client: `/SILENT
  /Relaunch=1`), ends the program, and the installer starts the new version when it is done. Updating the server therefore updates the clients.
  The WPF client only runs a downloaded installer that it can trust, so a server that has been taken over cannot push code to the clients. Either
  works, the first needs no certificate:
  - **Update key (recommended).** The release signs the installer with a private key (ECDSA P-256) and publishes the signature beside it
    (`Kyuplan-windows-<version>.exe.sig`, served at `GET /api/client/<platform>/signature`); the public key is built into the program
    (`installer/update-key.pub.pem`). The client downloads both and runs the installer only when the signature is valid.
    Set up once: create a key pair, commit the public key as `installer/update-key.pub.pem` and add the private key (the whole PEM text) as the
    repository secret `UPDATE_SIGNING_KEY`:
    ```
    openssl ecparam -name prime256v1 -genkey -noout -out update-key.pem
    openssl ec -in update-key.pem -pubout -out installer/update-key.pub.pem
    ```
    Keep the private key safe: whoever has it can update every client, and a client only trusts the key it was built with, so a lost key
    needs a new release that is installed by hand once.
  - **Authenticode.** An installer that is code-signed with the same certificate as the installed program (valid chain) is run as well. Add the
    repository secrets `WINDOWS_CERT_PFX` (the `.pfx` file, Base64-encoded) and `WINDOWS_CERT_PASSWORD`; the release workflow then signs the
    program and the installer.

  An installer with neither is deleted and not run (the update then has to be done by hand); for development the environment variable
  `KYUPLAN_ALLOW_UNSIGNED_UPDATES=1` lifts that for a program that is not Authenticode-signed.
- **Adding a client for another system** (say a macOS or Linux app): give it an `IClientUpdater` (`Client.Shared`) that names its
  platform, installer extension and version and knows how to run its installer; register `ClientUpdateViewModel` and show its button as
  the WPF client does. In `.github/workflows/release.yml` add a job next to `windows` that builds it and uploads its installer as the
  artifact `installer-<platform>` (named as above), and add the job to `needs` of `image`. Nothing in the API or the web client changes.

## Releasing

```bash
git tag v0.1
git push origin v0.1
```

- The version is `v<major>.<minor>` or `v<major>.<minor>.<patch>`, optionally with a prerelease suffix (`v2.0.0-beta.1`). The `v`
  belongs to the tag only, the version of the application is `0.1` or `1.2.3`. Anything else (`0.1`, `v1`, `v1.2.3+x`) makes the
  workflow fail at once. (Tags of the earlier per-app releases, `api/v...` and `wpf/v...`, no longer start a workflow.)
- The workflow first runs the whole test suite (on Windows: it contains tests of the WPF client's encrypted login file), then
  publishes and packs the WPF client into the installer (every client has such a job), and builds the documentation (see [The documentation](#the-documentation)). Only when they worked, the Docker image (which contains the installer) is built,
  pushed and the release published.
- **The version of the tag is the version of everything**: it is passed to every build as `-p:Version=0.1`, so it becomes the assembly,
  file and informational version of the API, the clients and the installer. The API reports it (`GET /api/info`, shown in both clients);
  in the installer it is the version Windows lists under *Installed apps*, and it is the version the WPF client compares with the server's.
  Builds on a developer machine keep the `<Version>` of `Directory.Build.props`.
- The GitHub release is created by the workflow, with generated notes that start at the previous version tag. A
  prerelease version creates a *pre-release*, and its Docker image gets no `latest`/`1`/`1.2` tag. If the release of the tag exists
  already (the workflow was run again, or the tag was deleted and pushed again, which turns its release into a draft), the
  workflow updates and publishes that release instead of failing with *"tag_name already exists"*: title, notes and the
  installer are replaced. Nothing is deleted.
- The release carries the installers and `Kyuplan-docs-<version>.zip`, the documentation of that version (every guide, the developer guide too).
- Docker image tags: the version without the `v` (`0.1`, `1.2.3`), and for a final version also `latest`, `1.2` (from `v1.2.3`) and
  `1` (from `v1.x`; there is none for 0.x, whose minor versions may break things).
- To take a release back, delete the release and the tag (`git push origin :refs/tags/v0.1`) and the image version
  under the repository's *Packages*.

The workflow is `.github/workflows/release.yml` (it reads the tag with `.github/actions/release-version` and publishes the release
with `.github/actions/publish-release`).

### GitHub settings

Nothing has to be configured: the workflow uses the built-in `GITHUB_TOKEN` and asks for the rights it needs
(`contents: write`, `packages: write`) itself. After the first image was pushed it shows up under the repository's
*Packages* and is **private** if the repository is. A private image can only be pulled after `docker login ghcr.io`, with a
[personal access token](https://github.com/settings/tokens) that has the `read:packages` scope; to avoid that, make the package
public (*Package settings → Danger zone → Change visibility*). The image contains no secrets, only the program.

## Where and how to run it (overview)

One image, many places. **Everything that is not in this table works too, as long as it can run a Linux container and give it a
folder or a PostgreSQL database that survives a restart.** The last column says what you have to do *in addition* to starting the image.

| Option | Good for | Database | Extra steps (details below) |
| --- | --- | --- | --- |
| `docker run` | a try-out, a small server | SQLite in a volume | none |
| **Docker Compose**, [`deploy/docker-compose.yml`](../deploy/docker-compose.yml) | a home server, a VM, a VPS, Portainer, Unraid, Synology Container Manager | SQLite in a volume | none; add a reverse proxy for HTTPS |
| **Docker Compose with PostgreSQL**, [`deploy/docker-compose.postgres.yml`](../deploy/docker-compose.postgres.yml) | the same, when you want a "real" database server, backups with `pg_dump`, or more users | PostgreSQL in a second container of the project, files in a local volume or folder | set `POSTGRES_PASSWORD`; a major PostgreSQL update needs a dump and restore |
| **TrueNAS SCALE**, [`deploy/docker-compose.truenas.yml`](../deploy/docker-compose.truenas.yml) | a NAS you have | SQLite in a dataset, or a PostgreSQL app | dataset owned by UID/GID 568; TrueNAS ignores `.env` files |
| **Azure** Container Apps / Container Instances / App Service for Containers / a VM | no server of your own | **PostgreSQL** (Azure Database for PostgreSQL, flexible server) is the right choice; SQLite only on a VM's own disk | allow the `CITEXT` extension; exactly **one** replica; health probe on `/healthz`; see [Azure](#azure) |
| **AWS** (ECS/Fargate, App Runner, Lightsail, EC2), **Google Cloud** (Cloud Run, GKE, GCE), **Hetzner, DigitalOcean, Fly.io, Railway, ...** | the same | PostgreSQL of the provider (RDS, Cloud SQL, managed PostgreSQL), or SQLite on a **block** disk | stateless platforms (Cloud Run, App Runner) need PostgreSQL *and* all settings as environment variables; see [Other clouds](#other-clouds-and-platforms) |
| **Kubernetes / k3s** | an existing cluster | PostgreSQL (operator or managed), or SQLite on a `ReadWriteOnce` volume | one replica, `strategy: Recreate`; see [Kubernetes](#kubernetes) |
| **Podman**, **Docker on Windows or macOS**, a **Raspberry Pi** | home, lab | SQLite or PostgreSQL | the image also exists for `arm64`; with Podman use `podman compose` or `podman run` with the same variables |
| **Without Docker** (`dotnet run` / `dotnet publish`) | development | SQLite or PostgreSQL | the .NET 10 SDK or runtime; see the README |

Whatever you choose:

- **Run exactly one container (replica) of the application.** It holds the Spond sync worker, the login throttle and the Spond cache in
  its memory; two copies would sync twice and count failed logins separately. A rolling update that runs the new copy next to the old
  one for a moment is fine.
- **HTTPS is not optional on a network you do not own.** The login is HTTP Basic. The server refuses logins that do not arrive over
  HTTPS (except from the machine itself); see [HTTPS and reverse proxies](#https-and-reverse-proxies).
- **Back up the data**, whichever database you use: see [Backup, restore and updates](#backup-restore-and-updates).

## The documentation

The documentation is the Markdown in `docs/` (and the repository's `README.md`), published with [Docusaurus](https://docusaurus.io) (`website/`). It is built **once per audience**,
because every user is only meant to see the guides that apply to them:

| Guide | Address | Who may read it (a permission of the list is enough) |
| --- | --- | --- |
| User guide (with the rules behind statistics, exams and comments) | `/docs/user/` | every signed-in user |
| Administrator guide | `/docs/admin/` | `settings.view`, `users.view`, `roles.view`, `branding.edit` |
| Deployment guide | `/docs/deployment/` | `settings.edit` |
| Developer guide, README reference | none (not served) | only in the release archive |

- The image carries the first three in `/app/docs/<audience>` (the Dockerfile builds them in its own `docs` stage). The API serves them (`GET /docs/<audience>/...`) **only with a login and
  only to users who have one of the permissions above** (403 otherwise, 404 for anything that is not a guide). The files are never reachable without that check.
  The rules are in `Kyuplan.Contracts/Documentation.cs`; the server's folder can be changed with `Documentation:Directory` (default: `docs` next to the program).
- The clients show a **Help** entry (web client: navigation; desktop app: bottom of the navigation) that opens the user guide in the browser, plus *Help for administrators* /
  *Help for installing* for users who may read those. Without documentation on the server (a build from source) the entries are not shown.
- Every release attaches `Kyuplan-docs-<version>.zip`: all four guides as static sites. Unpack it so that its content is served at `/docs/` of a web server (e.g. `npx serve` in the parent
  folder of a `docs` folder); the sites are built for that address. They need no login there, so keep that archive to people who may read everything.
- Locally: `cd website && npm ci && node scripts/build-all.mjs [audience ...]` writes `website/build/<audience>`; `npm start` previews the user guide (`DOCS_AUDIENCE=admin npm start` another one).
  Copy the built folders to `docs/` next to the API's DLL (or set `Documentation:Directory`) to see the help links in a running API.
- `website/scripts/audiences.mjs` says which Markdown file belongs to which guide; `prepare.mjs` rewrites the links (between guides: to the other site; into the repository: to GitHub at the release's tag).
  A new Markdown file is a new entry there.

## The server image

`ghcr.io/<owner>/kyuplan` contains the API, the web client (it is served at the same address), the installers of the desktop clients and the [documentation](#the-documentation).

- **Tags**: `1.2.3` (exactly that release), `1.2` (the newest `1.2.x`), `1` (the newest `1.x`, from 1.0 on), `latest`. For a server,
  pin `1.2` or `1.2.3` and update on purpose; `latest` changes whenever a release is made. `0.x` has no `0` tag, its minor versions may break things.
- **Platforms**: `linux/amd64` and `linux/arm64` under one tag: Docker picks the right one (a PC or server, a Raspberry Pi 4/5, Apple
  silicon, AWS Graviton, Azure Cobalt, ...). Not available: Windows containers, 32-bit ARM.
- **Port**: **8080**, plain HTTP. Change the *published* port (`-p 30100:8080`), not the one in the container.
- **Volume**: `/data` is the only place that has to survive: the SQLite databases (with SQLite), the settings file `settings.json`, the key for the
  passwords in it (`keys/`), the uploaded theme packs (`themes/`, small JSON files) and the logs (`logs/`, one file per day, the 31 newest kept; also on the console, i.e. `docker logs`).
- **User**: not root (UID 1654, name `app`). A Docker-managed volume is writable for it. For a folder you mount yourself, give that UID the rights or run the container as the folder's owner (`--user 568:568`).
- **Health check**: `GET /healthz` answers `200 ok` without a login while the database can be reached (`503` otherwise), also over plain HTTP from other machines, so load balancers and platform probes can use it. The image has a Docker `HEALTHCHECK` that uses it (see `docker ps`).
- **Settings**: everything in `appsettings.json` (see [Configuration](../README.md#configuration)) can be overridden by an environment
  variable: the key with `:` written as `__`, e.g. `Spond:Email` → `Spond__Email`, `Statistics:TimeZone` → `Statistics__TimeZone`, and the list
  `Api:AllowedOrigins` → `Api__AllowedOrigins__0`, `Api__AllowedOrigins__1`, … (ASP.NET Core's own convention). In Compose files,
  Kubernetes, Azure and so on these are the "environment variables" of the container. Secrets (`Spond__Password`, database passwords) belong in the
  platform's secret store (Docker/Compose `env_file`, a Kubernetes `Secret`, Azure Key Vault references, ...).
- **Time**: the image runs in UTC; the club's time zone is the setting `Statistics__TimeZone` (default `Europe/Berlin`).
- **Quick start**:

  ```bash
  docker run -d --name kyuplan -p 8080:8080 -v kyuplan-data:/data --restart unless-stopped ghcr.io/<owner>/kyuplan:1.2
  ```

  Open `http://localhost:8080/` (from another machine see HTTPS below). To try it without a Spond account add `-e Spond__UseDemoData=true`.
- **First start**: the site shows a setup screen (Spond login, the other settings, the first administrator). They are saved in
  `/data/settings.json` and the application restarts itself *inside* the container to apply them (the container keeps running);
  administrators change them later on the Settings page. Environment variables win over that file, so a value you set as one cannot be changed on the page.
  Set `Accounts__InitialAdminPassword` to create the administrator `admin` without the screen. A new database also starts with the standard
  belts and the roles Leading Trainer, Trainer and Passive User (see [Run it](../README.md#run-it)); `DefaultData__Enabled=false` starts it empty.
  They are only added when a database is created, so an existing database is never touched.
- **Allowed host name**: the setup page proposes the address you opened as `AllowedHosts`; requests with another `Host` header are then refused. Change it when the address changes (a new domain!), or the site answers `400`.
- **Branding** (name, accent colour, logos): the image carries the placeholders of `appsettings.json` (the name "My Club", a blue accent, a
  generic crest), so **every club sets its own**: `Branding__ProductName`, `Branding__OrganizationName`, `Branding__AccentColor`, and for the logos a
  folder with the files mounted read-only (`-v /path/to/branding:/branding:ro`) and `Branding__Logo=/branding/logo.svg`,
  `Branding__LogoOnDark=/branding/logo-white.svg` (an SVG, or a PNG/JPEG/GIF/BMP: the same file for the web and the desktop client) and optionally `Branding__Icon`.
  A key that is not set keeps the default of the image, an empty one (`Branding__Logo=`) switches that image off. These settings are only the basis:
  **administrators can change the branding in the web client** (page *Branding*) without any of this. That is saved in the database (so it is backed up with it) and
  applies instead of the settings until they use *Back to the settings of the API*. If a setting of yours seems to have no effect, look there first.
  Open clients follow a change by themselves within half a minute.
- **The Settings page and the database**: with SQLite the page can move the two database files (within the data folder). With PostgreSQL the
  connection strings contain passwords and are **only set through the environment** (or `appsettings`); the two path fields are empty and ignored.
- The log warns that data protection keys are not persisted. Nothing in the API uses them (there are no cookies or tokens), so that is harmless.

## Choosing the database

| | **SQLite** (default) | **PostgreSQL next to the application** (own container, files in a local volume or folder) | **PostgreSQL server of your own / managed** |
| --- | --- | --- | --- |
| Setting | nothing | `Database__Provider=PostgreSql` + two connection strings | the same |
| Extra software | none | the `postgres` image ([`docker-compose.postgres.yml`](../deploy/docker-compose.postgres.yml)) | an existing server, version 13 or newer |
| Where the data is | two files in `/data` (`kyuplan.db`, `accounts.db`) | a Docker volume or folder of the PostgreSQL container | on that server |
| Backup | stop the container and copy `/data`, see below | `pg_dump` | what the server offers (snapshots, point-in-time restore) |
| Good for | one club, one server, up to some dozens of users | the same, with a database you can look into and dump | cloud platforms without a persistent disk, a database that is backed up for you, high availability |
| Watch out for | **local disks only** (no NFS, SMB, Azure Files, EFS: SQLite's locking does not work reliably there); never two instances on the same files | a major version update of PostgreSQL needs a dump and restore | the `citext` extension, TLS, firewall rules; the connection string holds a password |

Both databases (the club's data in `kyuplan.db`, the user accounts in `accounts.db`) exist with every choice. With PostgreSQL they
**may be one database** (use the same connection string twice; the tables do not clash and each has its own migration history table) or two, if
you want to back up or protect the logins separately.

### SQLite

Nothing to do. The defaults of the image are `Database__ConnectionString="Data Source=/data/kyuplan.db"` and
`Accounts__ConnectionString="Data Source=/data/accounts.db"`. The files are in WAL mode: next to each `*.db` there are `-wal` and `-shm` files while it runs;
copy all of them, or stop the container first. Never put the volume on a network share, and never let two containers use the same files.

### PostgreSQL

Settings (environment variables of the container; the same names work in `appsettings.json` as `Database:Provider` and so on):

| Variable | Value |
| --- | --- |
| `Database__Provider` | `PostgreSql` (the default is `Sqlite`; it applies to both databases) |
| `Database__ConnectionString` | an [Npgsql connection string](https://www.npgsql.org/doc/connection-string-parameters.html), e.g. `Host=db;Database=kyuplan;Username=kyuplan;Password=...` |
| `Accounts__ConnectionString` | the same string again (one database), or one for a second database |

What you have to prepare (**extra steps**):

1. **A PostgreSQL 13 or newer** server, and an **empty database** with an **owner** (`CREATE DATABASE kyuplan OWNER kyuplan;`). The application creates and updates the tables itself when it starts, so the user needs the right to create tables in the database's schema (the owner has it; on PostgreSQL 15 and newer an ordinary user does not have `CREATE` on the schema `public`).
2. **The `citext` extension.** Names that are unique regardless of case (users, roles, belts, tags, exams) use it. It belongs to the standard contributed modules (the official `postgres` images, RDS, Cloud SQL, Azure, Supabase, Neon, ... have it) and, since PostgreSQL 13, is **trusted**: the database owner can create it, and the application does so in its first migration. If your provider keeps a list of allowed extensions (**Azure Database for PostgreSQL**: the server parameter `azure.extensions` → add `CITEXT`), allow it first, otherwise the first start stops with *extension "citext" is not allowed*.
3. **Network and TLS**: the server must be reachable from the container; managed servers usually insist on TLS: add `SSL Mode=Require` (or `VerifyFull` with `Root Certificate=...`) to the connection string.
4. **Passwords in the connection string**: put a password that contains `;` or `=` in double quotes: `Password="a;b=c"`. Letters and digits are the easy way. Use the platform's secrets, not a file in a repository.
5. **Connection poolers** (PgBouncer, the pooler of a managed service) generally work. The application keeps a pool of its own (up to 100 connections by default); if your plan limits the connections, lower it with `Maximum Pool Size=10` in the connection string.
6. **Time zone and encoding** need nothing: timestamps are stored as UTC (`timestamptz`), text as UTF-8 whatever the server default is.

What is different from SQLite:

- **There is no automatic move of data between the two systems.** Switching the provider of an instance means starting with an empty database. The schema, the default belts and roles, the first administrator and the settings are created on the first start as usual. To keep data, enter it again (members come back from Spond by themselves; the belts of the members, exams, comments and users do not).
- The database system is chosen **once per instance for both databases**.
- The PostgreSQL schema has its own migrations (`Persistence/PostgresMigrations`, `Accounts/PostgresMigrations`) that the application applies when it starts, as with SQLite. The tables have the same names. The migration history tables are `__EFMigrationsHistory` (club data) and `__EFMigrationsHistory_Accounts`.
- On the very first start the log shows a few `Failed executing DbCommand ... FROM "__EFMigrationsHistory"` lines at error level. That is Entity Framework asking whether the (not yet existing) history table is there; it happens once per database and is not a problem.
- Kerberos/GSS encryption is off by default (the image has no Kerberos library); add `Gss Encryption Mode=Prefer` to the connection string if you really use it.

### Examples

PostgreSQL in the same Compose project (data in the volume `kyuplan-db`; this is [`deploy/docker-compose.postgres.yml`](../deploy/docker-compose.postgres.yml)):

```bash
cd deploy
cp .env.example .env            # set POSTGRES_PASSWORD
docker compose -f docker-compose.postgres.yml up -d
```

To keep the PostgreSQL files in a folder of your choice instead of the volume, replace `kyuplan-db:/var/lib/postgresql/data` by `/srv/kyuplan/postgres:/var/lib/postgresql/data`
(a real disk, not a Windows or macOS shared folder, and owned by the user of the `postgres` image).

A server that exists already, with `docker run`:

```bash
docker run -d --name kyuplan -p 8080:8080 -v kyuplan-data:/data \
  -e Database__Provider=PostgreSql \
  -e "Database__ConnectionString=Host=db.example.org;Database=kyuplan;Username=kyuplan;Password=...;SSL Mode=Require" \
  -e "Accounts__ConnectionString=Host=db.example.org;Database=kyuplan;Username=kyuplan;Password=...;SSL Mode=Require" \
  ghcr.io/<owner>/kyuplan:1.2
```

(`/data` then only holds the settings file, its key and the logs. A platform without any persistent disk is possible, see below.)

## HTTPS and reverse proxies

The container speaks plain HTTP on 8080. The login is HTTP Basic, so it has to be wrapped in HTTPS everywhere except on the machine itself:

- Put a **reverse proxy that ends HTTPS** in front and set `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`, so that the application sees the original scheme and visitor address (that is
  also what makes failed logins count per visitor and not per proxy). Only proxies in the loopback and private networks are believed (`Api__TrustedProxies__0=<address or network>` narrows or widens that): a visitor who reaches the container directly cannot pose as HTTPS or as another address. A request that carries `X-Forwarded-For` is never treated as "this machine", so a proxy on the same host does not switch the HTTPS check off. Any proxy works: **Caddy** (automatic certificates), **nginx**, **Traefik**, **HAProxy**, the load balancer or ingress of a cloud platform,
  **Cloudflare Tunnel**, **Tailscale Serve**, the proxies built into TrueNAS or Synology. Forward the `Host` header unchanged (or adjust `AllowedHosts`).
- Caddy in one line: `kyuplan.example.org { reverse_proxy kyuplan:8080 }` (in the same Compose project, no published port needed).
- Behind a platform that ends TLS itself and talks plain HTTP to the container (Azure Container Apps, Cloud Run, App Runner, ...) the forwarded headers are what counts: set the variable above.
- **On a network you trust completely** (a home LAN) plain HTTP can be allowed: `Api__AllowInsecureHttp=true`. Without HTTPS or this flag, other machines get `400 HTTPS required`.
- The desktop clients take an `https://` address (or an `http://` one when the flag above is set).

## Azure

The image runs on every Azure container service. The pattern that works: **one container + Azure Database for PostgreSQL (flexible server)**.

1. **PostgreSQL**: create a flexible server (version 13 or newer; the smallest burstable size is enough) and a database `kyuplan`, and in *Server parameters* add `CITEXT` to **`azure.extensions`**. Allow the container to connect (public access with a firewall rule for the app's outbound addresses, or a virtual network). TLS is required: `SSL Mode=Require` in the connection string.
2. **Container Apps** (recommended): the image `ghcr.io/<owner>/kyuplan:1.2` (a private image needs registry credentials, or copy it to an Azure Container Registry with `az acr import`), ingress on target port **8080**, *minimum and maximum replicas 1*, secrets for the two connection strings and `Spond__Password`, environment variables for the rest, including `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` (the managed ingress already provides HTTPS and a certificate). Add a startup and liveness probe `GET /healthz` on port 8080.
3. **Container Instances** and **App Service for Containers (Linux)** work the same way (App Service: `WEBSITES_PORT=8080`, *Always on*, one instance). `/data` is not persistent there unless you mount storage, which is why PostgreSQL is the right choice: the settings then come from environment variables (next point) and nothing important is kept in `/data`.
4. **Settings without a persistent disk**: the setup screen and the Settings page write `/data/settings.json`; on an ephemeral `/data` it is gone after a restart. Set what you need as environment variables instead (`Spond__Email`, `Spond__Password`, `Accounts__InitialAdminPassword`, `AllowedHosts`, `Branding__*`, ...). The branding can still be edited in the web client (it is stored in the database); the other settings then cannot be changed on the page. Or mount an **Azure Files** share on `/data` for the settings file and the logs only: with PostgreSQL that is fine (the databases are not on it). **Never put the SQLite files on Azure Files** (SMB).
5. **A VM with Docker** (an Azure VM running the Compose file) is the simplest way if you want SQLite: the VM's own disk is a block disk, where SQLite is fine.
6. **Secrets**: use Container Apps secrets or Key Vault references for the connection strings and passwords. Signing in to PostgreSQL with a managed identity (Entra ID tokens) is **not supported**; use a database password.

## Other clouds and platforms

- **AWS**: *ECS on Fargate* (one task, port 8080, a target group with the health check path `/healthz`, secrets from Secrets Manager or SSM) with *RDS for PostgreSQL* (`citext` is available; `SSL Mode=Require`); *App Runner* (port 8080, health check `/healthz`; no persistent disk: PostgreSQL and environment variables only); *Lightsail containers* or *EC2* with the Compose file (SQLite on EBS is fine; EFS is a network filesystem, **not for SQLite**).
- **Google Cloud**: *Cloud Run* (container port 8080, **maximum instances 1**, "CPU always allocated" so that the background sync keeps running, PostgreSQL from *Cloud SQL* over its private IP or the Cloud SQL connector, all settings as environment variables or Secret Manager entries; Cloud Run cannot keep SQLite files), *GCE* with the Compose file, or *GKE* with the Kubernetes notes below.
- **Small hosters** (Hetzner, DigitalOcean, Scaleway, OVH, Netcup, ...): a VM with Docker, the Compose file and Caddy for HTTPS. **Fly.io**: one machine with a volume mounted at `/data` (SQLite) or a managed PostgreSQL. **Railway, Render, Coolify, CapRover, Dokku**: the image plus a volume or their managed PostgreSQL, HTTP port 8080, health path `/healthz`.
- **NAS and home server platforms**: TrueNAS SCALE (below), Synology Container Manager, QNAP Container Station, Unraid, Portainer, a Docker VM on Proxmox: the Compose file runs as it is; mount `/data` on a local volume (not on a network share).
- **Platforms without a persistent disk**: PostgreSQL for the databases and every other setting as an environment variable.

## Kubernetes

A `Deployment` with `replicas: 1` and `strategy: Recreate` (not a rolling update with SQLite), container port 8080, `livenessProbe` and `readinessProbe` of type `httpGet` on `/healthz`, `securityContext` with `runAsUser: 1654` and `fsGroup: 1654` for the volume, the settings as `env` (secrets from a `Secret`), and a `Service` plus an `Ingress` with TLS in front; set `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`.
For SQLite add a `PersistentVolumeClaim` (`ReadWriteOnce`, block storage) mounted at `/data`; for PostgreSQL use a managed server or an operator (CloudNativePG, Zalando, Crunchy) and put the connection strings in a `Secret`. Autoscaling must stay at one pod.

## TrueNAS

[`deploy/docker-compose.truenas.yml`](../deploy/docker-compose.truenas.yml) runs the container. On TrueNAS SCALE 24.10 or newer:

1. Create a dataset for the data, e.g. `tank/apps/kyuplan/data`, owned by the `apps` user (UID/GID 568).
2. *Apps → Discover Apps → Custom App → Install via YAML*, paste the file and replace every `CHANGE-ME` / `CHANGE ME`: the pool in the data path and, if you want, the Spond login and the administrator's password. If the image is private, run `docker login ghcr.io` in the TrueNAS shell first (see [GitHub settings](#github-settings)).
3. Open the web client at `http://<truenas>:30100/` (on a home network add `Api__AllowInsecureHttp: "true"`, or put the proxy of your choice in front). Sign in as `admin`, then create a user for everybody (give them a role of the **Roles** page: a new user may do nothing until they have permissions) and change the administrator's password on the **Account** page.
4. **PostgreSQL on TrueNAS**: install a PostgreSQL app (or any PostgreSQL container on the NAS), create the database as described above, and put the three `Database__…` / `Accounts__…` variables from the end of the file into the YAML. The SQLite default needs none of this and is fine for one club.
5. To update: change the image tag to the new version and redeploy (TrueNAS pulls the image again). The installed WPF clients then offer to update themselves. The application brings its databases up to date when it starts; back the dataset up before an update that you do not know.

## Backup, restore and updates

- **What to back up**: SQLite: the whole `/data` (both `*.db` files **with** their `-wal` and `-shm` files, the settings file, and `keys/`, which decrypts the passwords in the settings file; if the backups are shared, keep the key apart from the settings file with `Setup__KeyDirectory`). PostgreSQL: the database(s) (`pg_dump -Fc kyuplan > kyuplan.dump`) and `/data/settings.json` with `/data/keys`.
- **A consistent SQLite copy**: stop the container, copy the volume, start it again:

  ```bash
  docker stop kyuplan
  docker run --rm -v kyuplan-data:/data -v "$PWD":/backup alpine tar czf /backup/kyuplan-data.tgz -C /data .
  docker start kyuplan
  ```

- **Backups from the application**: administrators can also download a backup file, restore one, and keep mirror databases up to date on the *Backup* page (see the [administrator guide](admin-guide.md#backups)). The file covers the two databases, **not** `settings.json`, `keys/` or the uploaded themes folder: copy those with the volume as above. A **new** instance can start from such a file: put it on the volume and set `Backup__InitialDataFile=/data/start.kyubackup` (used only when both databases are created by that start). Options: `Backup__Enabled` (automatic mirror updates, default `true`), `Backup__TargetsFile` (where the mirror definitions live, default next to the settings file), `Backup__MaxUploadMegabytes` (largest file that can be restored, default 256), `Backup__MaxUnpackedMegabytes` (default 1024).
- **Restore**: stop the container, put the files back (or `pg_restore -c -d kyuplan kyuplan.dump`), start it.
- **Update**: pull the new tag and recreate the container (`docker compose pull && docker compose up -d`). The application applies the database migrations itself when it starts; **back up first**, and never start a database with an *older* version than the one that last migrated it. The installed desktop clients update themselves from the server afterwards.
- **PostgreSQL major version** (say 17 → 18) in the bundled container: dump with the old one, start the new version on a new volume, restore. The files of one major version cannot be opened by the next.
- **Moving to another host**: copy the data (as above) and start the same image tag there. A new public address means a new `AllowedHosts` (and the desktop clients ask for the new address once).

## Troubleshooting

| Symptom | Cause and fix |
| --- | --- |
| `400 HTTPS required` | You opened the site over plain HTTP from another machine. Use HTTPS behind a proxy with `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`, or on a trusted network `Api__AllowInsecureHttp=true`. |
| `400 Bad Request - Invalid Hostname` | `AllowedHosts` does not contain the address you use. Set `AllowedHosts` (environment variable, or the Settings page) to it, or to `*`. |
| `unable to open database file`, `readonly database`, `Permission denied` on `/data` | The folder is not writable for the container user (UID 1654, or the one of `--user`). `chown` it, or use a Docker volume. |
| `database is locked`, or damage after a crash | The SQLite files are on a network share, or two containers use them. Move them to a local disk, or use PostgreSQL. |
| `extension "citext" is not allowed`, `permission denied to create extension` | PostgreSQL: allow `CITEXT` on the platform (Azure: `azure.extensions`), or create it once as a superuser: `CREATE EXTENSION citext;` in that database. |
| `permission denied for schema public` | PostgreSQL 15 and newer: `ALTER DATABASE kyuplan OWNER TO kyuplan;` (or `GRANT ALL ON SCHEMA public TO kyuplan;`). |
| `28P01 password authentication failed`, `Connection refused`, `SSL required` | Wrong password, host or port; the server is not up yet (Compose: `depends_on` with the health check); add `SSL Mode=Require`. |
| The container is `unhealthy` | `docker logs kyuplan`. `/healthz` answers `503` when the database cannot be reached. |
| Settings disappeared after a restart | `/data` is not a persistent volume (a stateless platform). Mount one, or set the values as environment variables. |
| The setup screen is not shown | There is a user already (or `Accounts__InitialAdminPassword` created one). Sign in; to start over, use an empty database. |
| `Failed executing DbCommand ... __EFMigrationsHistory` once on a new database | Expected, see PostgreSQL above. |
| Logins or syncs seem to happen twice | More than one replica runs. Scale to one. |

## The WPF client installer

`Kyuplan-windows-<version>.exe` (built by [Inno Setup](https://jrsoftware.org/isinfo.php) from
[`installer/Kyuplan.iss`](../installer/Kyuplan.iss)) installs the client **self-contained**: it brings
its own .NET runtime, so nothing else has to be installed (the price is a bigger download). Users get it from the web client of their
server (*Download for Windows*) or from the GitHub release; once installed, the client updates itself from the server (see above).

- Installs for the current user by default (no administrator rights); the start dialog offers *for all users*. Adds a Start
  menu entry, an optional desktop icon and an uninstaller. English or German, following the language of Windows.
- **The installer does not ask for the address of the API, and it is not in any settings file**: every user enters it themselves.
  The client asks for it in a dialog when it starts for the first time, followed by the login. It is kept per Windows user in
  `%LocalAppData%\Kyuplan\api-address.txt`. To use another server later, the **Change …** button next to the server address in
  the login window (sign out first when already signed in) opens the same dialog; the program then starts again with the new address.
  A remembered login only ever goes to the server it was saved for.
- `appsettings.json` in the installation folder only has `PlannerApi:ApiKey`, for an API that requires one. Installing an update does
  not overwrite it. (Older versions kept the address there; it is no longer read, so the first start after the update asks for it.)
- Silent installation: `Kyuplan-windows-1.2.3.exe /VERYSILENT /SUPPRESSMSGBOXES`; add `/Relaunch=1` to start the program afterwards (the self-update does).
- Without the secrets `WINDOWS_CERT_PFX` and `WINDOWS_CERT_PASSWORD` (see the client updates above) the installer is **not code-signed**: Windows
  SmartScreen warns when it is downloaded ("Windows protected your PC" → *More info* → *Run anyway*). The installed program still updates itself
  when the release carries the update-key signature (`UPDATE_SIGNING_KEY`).
- The user's own data (server address, language, theme, last filter, the remembered login) is in `%LocalAppData%\Kyuplan` and stays when the
  program is uninstalled.

## Building the pieces by hand

```bash
dotnet publish src/Kyuplan.Client.Wpf -c Release -r win-x64 --self-contained -p:Version=1.2.3 -o artifacts/wpf
ISCC.exe /DAppVersion=1.2.3 /DVersionInfo=1.2.3.0 installer\Kyuplan.iss    # artifacts\installer\Kyuplan-windows-1.2.3.exe
docker build -f src/Kyuplan.Api/Dockerfile --build-arg VERSION=1.2.3 --build-arg REPOSITORY_URL=https://github.com/<owner>/<repo> --build-context installer=artifacts/installer -t kyuplan .
# both CPUs (the Dockerfile cross-compiles, no emulation); needs a buildx builder, and --push instead of -t for a registry:
docker buildx build --platform linux/amd64,linux/arm64 -f src/Kyuplan.Api/Dockerfile --build-context installer=artifacts/installer -t kyuplan .
```

Without `--build-context installer=...` the image has no installer (and the server offers no download).
