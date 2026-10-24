#!/bin/sh
# Starts the Linux client of Kyuplan (installed by the package of deploy/arch).
exec dotnet /usr/lib/kyuplan/kyuplan.dll "$@"
