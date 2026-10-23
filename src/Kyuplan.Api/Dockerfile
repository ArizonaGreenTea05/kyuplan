# syntax=docker/dockerfile:1
# Build from the repository root:
#   docker build -f src/Kyuplan.Api/Dockerfile --build-arg VERSION=1.2.3 -t kyuplan .
# The image contains the API and the web client (Blazor WebAssembly), both served on port 8080, and the installer of the Windows client
# (/app/downloads), which the server offers for download and the installed clients as their update.
# The documentation (user, administrator and deployment guide, built with Docusaurus) lies in /app/docs; the server shows each user the guides they may read.
# The release workflow (.github/workflows/release.yml) builds the installer first and passes it in as the "installer" build context for
# every v<version> tag:
#   docker build -f src/Kyuplan.Api/Dockerfile --build-context installer=artifacts/installer ...
# A build without it (the default below) has no installer, and the server then offers no download.
#
# One image for every Docker host (Linux amd64 and arm64: TrueNAS, Azure, AWS, a Raspberry Pi, ...). It is configured by environment
# variables only: see docs/deployment.md. Without any, it runs on SQLite files in the volume /data; PostgreSQL is a setting
# (Database__Provider=PostgreSql), not another image.
#   docker buildx build --platform linux/amd64,linux/arm64 -f src/Kyuplan.Api/Dockerfile -t kyuplan .

# The documentation: one Docusaurus site per audience (website/, from docs/), served under /docs by the API to the users who may read each.
FROM --platform=$BUILDPLATFORM node:25-alpine AS docs
WORKDIR /repo/website
COPY website/package.json website/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY website ./
COPY docs ../docs
# The tag (or branch) that links from the documentation into the repository point at.
ARG DOCS_REPO_REF=main
RUN DOCS_REPO_REF=${DOCS_REPO_REF} node scripts/build-all.mjs user admin deployment

# Always runs on the machine that builds; for the other CPU it cross-compiles (no emulation), TARGETARCH being the one of the image.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src

# Restore on the project files alone, so that this layer is reused until a package changes.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Kyuplan.Contracts/Kyuplan.Contracts.csproj src/Kyuplan.Contracts/
COPY src/Kyuplan.Client.Shared/Kyuplan.Client.Shared.csproj src/Kyuplan.Client.Shared/
COPY src/Kyuplan.Client.Blazor/Kyuplan.Client.Blazor.csproj src/Kyuplan.Client.Blazor/
COPY src/Kyuplan.Api/Kyuplan.Api.csproj src/Kyuplan.Api/
RUN dotnet restore src/Kyuplan.Api -a "$([ "$TARGETARCH" = arm64 ] && echo arm64 || echo x64)"

COPY src/Kyuplan.Contracts src/Kyuplan.Contracts
COPY src/Kyuplan.Client.Shared src/Kyuplan.Client.Shared
COPY src/Kyuplan.Client.Blazor src/Kyuplan.Client.Blazor
COPY src/Kyuplan.Api src/Kyuplan.Api
# The logos and the icon of appsettings.json's Branding section (the API project links them into its output).
COPY assets/branding assets/branding

# Becomes the assembly's (informational) version, which GET /api/info reports.
ARG VERSION=0.0.0
RUN mkdir /empty
RUN dotnet publish src/Kyuplan.Api -c Release -o /app --no-restore -p:Version=${VERSION} --no-self-contained \
    -a "$([ "$TARGETARCH" = arm64 ] && echo arm64 || echo x64)"


# Empty unless a build context of this name is given (see above).
FROM scratch AS installer


FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
COPY --from=installer / /app/downloads/
COPY --from=docs /repo/website/build /app/docs

# The default is SQLite: both databases, the settings file and the log files live in /data. Mount a volume there, or they are lost
# with the container. Like every other setting these can be overridden with environment variables, e.g. for PostgreSQL
# Database__Provider, Database__ConnectionString and Accounts__ConnectionString (docs/deployment.md).
# The repository the release was built from (GET /api/info, the "powered by" link); the release workflow passes it.
ARG REPOSITORY_URL=
ENV Repository__Url=${REPOSITORY_URL} \
    Database__ConnectionString="Data Source=/data/kyuplan.db" \
    Accounts__ConnectionString="Data Source=/data/accounts.db" \
    Setup__SettingsFile="/data/settings.json" \
    Logging__File__Path="/data/logs/kyuplan-.log" \
    ASPNETCORE_HTTP_PORTS=8080
# An empty folder owned by the unprivileged user (COPY instead of RUN, so that building the arm64 image needs no emulation).
COPY --from=build --chown=$APP_UID /empty /data
VOLUME /data

LABEL org.opencontainers.image.title="kyuplan" \
      org.opencontainers.image.description="Exam planner for karate clubs on Spond data: API, web client and desktop installers in one container"

# GET /healthz needs no login and answers 200 while the database can be reached. (bash is in this image, curl is not.)
HEALTHCHECK --interval=30s --timeout=5s --start-period=60s --retries=3 \
    CMD bash -c 'exec 3<>/dev/tcp/127.0.0.1/8080 && printf "GET /healthz HTTP/1.0\r\nHost: localhost\r\n\r\n" >&3 && head -n1 <&3 | grep -q " 200 "' || exit 1

USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Kyuplan.Api.dll"]
