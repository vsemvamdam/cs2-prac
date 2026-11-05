#!/usr/bin/env bash
# Собирает плагин и папку для Windows-ПК: dist/CS2Prac (скрипты + plugin/CS2Prac) и dist/CS2Prac.zip.
# Нужен .NET 10 SDK. Без интернета можно указать папку с DLL CounterStrikeSharp: CSS_API_DIR=/путь/к/api tools/pack.sh
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root/plugin/CS2Prac"
if [ -n "${CSS_API_DIR:-}" ]; then
  dotnet build -c Release -p:CssApiDir="$CSS_API_DIR"
else
  dotnet build -c Release
fi
out="$root/dist/CS2Prac"
rm -rf "$root/dist" && mkdir -p "$out/plugin/CS2Prac"
cp "$root"/installer/* "$out/"
bin="$root/plugin/CS2Prac/bin/Release/net10.0"
cp "$bin/CS2Prac.dll" "$out/plugin/CS2Prac/"
cp -r "$bin/data" "$root/plugin/CS2Prac/gamedata" "$out/plugin/CS2Prac/"
(cd "$root/dist" && zip -qr CS2Prac.zip CS2Prac)
echo "Готово: $root/dist/CS2Prac.zip"
