#!/usr/bin/env bash
# Reads changed paths (one per line) on stdin and prints the CI gate flags as key=value lines:
#   code=true   when any path is not documentation (*.md at any depth, LICENSE.txt, .claude/)
#   script=true when any path can change the offline page script test
# Unknown paths count as code. No paths at all is treated as "run everything".
set -euo pipefail

code=false
script=false
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
done

if [ "$count" -eq 0 ]; then
  code=true
  script=true
fi

echo "code=$code"
echo "script=$script"
