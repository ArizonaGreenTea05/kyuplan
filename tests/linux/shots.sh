#!/bin/bash
# Builds the API and the Linux client from /repo (read-only), starts the API in demo mode and the client in a virtual X server, signs in as
# the administrator and saves a PNG of every page of the sidebar to /out. Meant to run inside tests/linux/Dockerfile.
# It is a look at the pages, not a test: it only fails when the build, the API or the client does not come up, or the client logs an exception.
set -euo pipefail

OUT="${OUT:-/out}"
WORK=/tmp/work
PASSWORD="$(head -c 24 /dev/urandom | base64 | tr -dc 'A-Za-z0-9' | head -c 20)-Aa1"   # for this run only, of this throw-away API

mkdir -p "$OUT" "$WORK"
rsync -a --exclude bin --exclude obj --exclude node_modules --exclude .git --exclude artifacts --exclude website /repo/ "$WORK/"

echo "== build"
(cd "$WORK/src/Kyuplan.Api" && dotnet build -c Debug -v q --nologo | tail -1)
(cd "$WORK/src/Kyuplan.Client.Linux" && dotnet build -c Debug -v q --nologo | tail -1)

echo "== API (demo data)"
mkdir -p /tmp/api && cd /tmp/api
ASPNETCORE_URLS=http://127.0.0.1:5100 Accounts__InitialAdminPassword="$PASSWORD" Spond__UseDemoData=true \
  dotnet "$WORK/src/Kyuplan.Api/bin/Debug/net10.0/Kyuplan.Api.dll" > /tmp/api.log 2>&1 &
for _ in $(seq 1 60); do curl -fs http://127.0.0.1:5100/healthz > /dev/null && break; sleep 1; done
curl -fs http://127.0.0.1:5100/healthz > /dev/null || { cat /tmp/api.log; exit 1; }
sleep 8   # the first sync of the demo data

echo "== client"
mkdir -p "$HOME/.config/kyuplan"
echo "http://127.0.0.1:5100/" > "$HOME/.config/kyuplan/api-address.txt"
export DISPLAY=:99 GSK_RENDERER=cairo LIBGL_ALWAYS_SOFTWARE=1 NO_AT_BRIDGE=1 GDK_BACKEND=x11
Xvfb :99 -screen 0 1360x860x24 > /dev/null 2>&1 &
sleep 2
dbus-run-session -- dotnet "$WORK/src/Kyuplan.Client.Linux/bin/Debug/net10.0/kyuplan.dll" > /tmp/client.log 2>&1 &
sleep 10

shot() { import -window root "$OUT/$1.png"; }

shot 00-login
xdotool mousemove 680 433 click 1; sleep 0.5
xdotool type --delay 30 admin; xdotool key Tab
xdotool type --delay 30 "$PASSWORD"; xdotool key Return
sleep 8

# The rows of the sidebar for the administrator of the demo data (the window is 1360 x 860).
declare -A PAGES=(
  [01-dashboard]=80 [02-statistics]=118 [03-calendar]=156 [04-exams]=194 [05-members]=232 [06-belts]=270 [07-prices]=308 [08-tags]=346
  [09-users]=431 [10-roles]=469 [11-settings]=507 [12-branding]=545 [13-calendar-feeds]=583 [14-backup]=621 [15-sync-status]=659
  [16-account]=744 [17-preferences]=782
)
for name in $(printf '%s\n' "${!PAGES[@]}" | sort); do
  xdotool mousemove 130 "${PAGES[$name]}" click 1
  sleep 4
  shot "$name"
done

echo "== done"
if grep -E "Unhandled|System\.[A-Za-z]*Exception|GLib-GObject-CRITICAL|Gtk-CRITICAL" /tmp/client.log; then
  echo "the client logged problems (above)"
  exit 1
fi
