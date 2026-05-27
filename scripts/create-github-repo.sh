#!/usr/bin/env bash
set -euo pipefail

REPO_NAME="${1:-tym-llm-dotnet}"
VISIBILITY="${2:-private}"
DESCRIPTION="Time Yards Model LLM .NET prototype"

case "$VISIBILITY" in
  public|private|internal) ;;
  *)
    echo "Visibility must be one of: public, private, internal" >&2
    exit 1
    ;;
esac

if ! command -v git >/dev/null 2>&1; then
  echo "git is required but was not found in PATH." >&2
  exit 1
fi

if ! command -v gh >/dev/null 2>&1; then
  echo "GitHub CLI is required. Install it, then run: gh auth login" >&2
  exit 1
fi

if ! gh auth status >/dev/null 2>&1; then
  echo "GitHub CLI is not authenticated. Run: gh auth login" >&2
  exit 1
fi

if [ ! -d .git ]; then
  git init
fi

git add .
if ! git diff --cached --quiet; then
  git commit -m "Initial TYM LLM .NET prototype"
fi

git branch -M main

gh repo create "$REPO_NAME" "--$VISIBILITY" \
  --source=. \
  --remote=origin \
  --push \
  --description "$DESCRIPTION"

echo "Repository created and pushed: $REPO_NAME"
