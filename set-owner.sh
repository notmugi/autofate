#!/usr/bin/env bash
# Replaces the GitHub owner placeholder across repo files.
# Usage: ./set-owner.sh YOUR_GITHUB_USERNAME
set -euo pipefail

if [ $# -ne 1 ]; then
  echo "Usage: $0 YOUR_GITHUB_USERNAME"
  exit 1
fi

OWNER="$1"
PLACEHOLDER="YOUR_GITHUB_USERNAME"

# Files that may contain the placeholder.
FILES=(repo.json SETUP.md README.md Autofate/Autofate.json)

for f in "${FILES[@]}"; do
  if [ -f "$f" ] && grep -q "$PLACEHOLDER" "$f"; then
    sed -i "s/$PLACEHOLDER/$OWNER/g" "$f"
    echo "Updated $f"
  fi
done

# Update RepoUrl in the plugin manifest too (it uses a different placeholder).
if [ -f Autofate/Autofate.json ]; then
  sed -i "s#https://github.com/yourname/Autofate#https://github.com/$OWNER/Autofate#g" Autofate/Autofate.json
  echo "Updated Autofate/Autofate.json RepoUrl"
fi

echo "Done. Owner set to '$OWNER'."
