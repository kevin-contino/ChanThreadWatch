#!/usr/bin/env bash
# Reads changed paths (one per line) on stdin and prints the CI gate flags as key=value lines:
#   code=true   when any path is not documentation (*.md at any depth, LICENSE.txt, .claude/)
#   script=true when any path can change the offline page script test
#   extension=true when any path can change the browser extension job (its files, and the pairing vectors, pinned
#     Chrome id and version source its tests read)
# Unknown paths count as code. No paths at all is treated as "run everything".
set -euo pipefail

code=false
script=false
extension=false
count=0

while IFS= read -r path || [ -n "$path" ]; do
  path="${path%$'\r'}"
  [ -z "$path" ] && continue
  count=$((count + 1))

  case "$path" in
    *.md | LICENSE.txt | .claude/*) ;;
    *) code=true ;;
  esac

  case "$path" in
    tools/offline-page-script/* | src/ChanThreadWatch.Core/Resources/OfflinePageScript.js | ChanThreadWatch.Tests/Fixtures/* | .github/*)
      script=true
      ;;
  esac

  case "$path" in
    tools/browser-extension/* | ChanThreadWatch.Api.Tests/PairingVectors.json | src/ChanThreadWatch.Api/ApiPairing.cs | src/ChanThreadWatch/Properties/AssemblyInfo.cs | .github/*)
      extension=true
      ;;
  esac
done

if [ "$count" -eq 0 ]; then
  code=true
  script=true
  extension=true
fi

echo "code=$code"
echo "script=$script"
echo "extension=$extension"
