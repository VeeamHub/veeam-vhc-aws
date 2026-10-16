#!/usr/bin/env bash
# Flags literal credentials in config files. smtp_password is included.
# ENC: values are protected ciphertext and are allowed, along with empty,
# ${VAR}, ${{ expr }}, {{ template }}, <placeholder>, null, and ~.
set -euo pipefail

key='^[[:space:]]*(smtp_)?(password|passwd|secret|api[_-]?key|apikey|token|bind_?password)[[:space:]]*:[[:space:]]*[^[:space:]]'
allow=':[[:space:]]*("" *|'"''"' *|\$\{\{.*\}\}|\$\{[^}]*\}|\{\{.*\}\}|<[^>]*>|null|~|"?ENC:[^[:space:]]*)[[:space:]]*,?[[:space:]]*$'

if [ "$#" -gt 0 ]; then
  hits=$(grep -nIE "$key" "$@" | grep -vE "$allow" || true)
else
  hits=$(git grep -nIE "$key" -- '*.yaml' '*.yml' '*.json' '*.toml' '*.config' '*.xml' 2>/dev/null \
    | grep -vE "$allow" || true)
fi

if [ -n "$hits" ]; then
  echo "$hits" | sed -E 's/(:[[:space:]]*).*/\1<redacted>/'
  echo "Literal credential found in a tracked config file. Use an env var, an ENC: value, or an empty placeholder."
  exit 1
fi

echo "clean: no literal credentials in tracked config files"
