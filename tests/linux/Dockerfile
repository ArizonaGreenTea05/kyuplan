# An Arch Linux with everything the Linux client needs to run, and a virtual X server to look at it:
#
#   docker build -t kyuplan-arch tests/linux
#   docker run --rm -v "$PWD":/repo:ro -v "$PWD/linux-screenshots":/out kyuplan-arch /repo/tests/linux/shots.sh
#
# (see docs/developer-guide.md, "Linux screenshots")
FROM archlinux:latest

RUN pacman -Syu --noconfirm --needed \
      dotnet-sdk aspnet-runtime aspnet-targeting-pack \
      gtk4 libadwaita librsvg libsecret gnome-keyring adwaita-icon-theme ttf-dejavu mesa dbus \
      xorg-server-xvfb xdotool imagemagick rsync \
    && pacman -Scc --noconfirm

ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_CLI_HOME=/root HOME=/root
