#!/bin/bash
# Install the vela skill into ~/.codex/skills/ for Codex.
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SKILLS_ROOT="$HOME/.codex/skills"
PACK_LOG="$(mktemp)"
trap 'rm -f "$PACK_LOG"' EXIT

echo "=== vela skill installer (Codex) ==="

command -v dotnet >/dev/null 2>&1 || {
  echo "vela needs the .NET SDK 10.0 or newer: https://dotnet.microsoft.com/download"
  exit 1
}

echo "Building and installing the vela tool..."
if ! dotnet pack "$SCRIPT_DIR/src/Vela/Vela.csproj" -c Release -o "$SCRIPT_DIR/nupkg" >"$PACK_LOG" 2>&1; then
  echo "dotnet pack failed. Output:" >&2
  cat "$PACK_LOG" >&2
  exit 1
fi
dotnet tool update --global --add-source "$SCRIPT_DIR/nupkg" vela

for src in "$SCRIPT_DIR"/skills/*/; do
  src="${src%/}"
  name="$(basename "$src")"
  target="$SKILLS_ROOT/$name"
  mkdir -p "$target"
  for sub in scripts references tests; do
    [ -d "$src/$sub" ] && ln -sfn "$src/$sub" "$target/$sub"
  done
  sed "s|\${CLAUDE_SKILL_DIR}|$target|g" "$src/SKILL.md" > "$target/SKILL.md"
  echo "Installed '$name' -> $target"
done

case ":$PATH:" in
  *":$HOME/.dotnet/tools:"*)
    ;;
  *)
    echo ""
    echo "Warning: $HOME/.dotnet/tools is not on your PATH, so the 'vela' command will not run yet."
    echo "Add this to your shell's startup file and open a new shell:"
    echo ""
    echo "    export PATH=\"\$HOME/.dotnet/tools:\$PATH\""
    echo ""
    ;;
esac

echo "Done. Run 'vela index' in a solution directory to get started."
