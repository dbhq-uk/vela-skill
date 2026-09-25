#!/bin/bash
# Install the vela skill into ~/.claude/skills/ and build the vela tool.
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SKILLS_ROOT="$HOME/.claude/skills"
PACK_LOG="$(mktemp)"
trap 'rm -f "$PACK_LOG"' EXIT

echo "=== vela skill installer (Claude Code) ==="

command -v dotnet >/dev/null 2>&1 || {
  echo "vela needs the .NET SDK 10.0 or newer: https://dotnet.microsoft.com/download"
  exit 1
}

# Every build gets a version of its own, so re-running this always replaces the
# vela that is installed. Before, every build was 1.0.0, and `dotnet tool update`
# over an installed 1.0.0 said so and exited 0, leaving the old binary in place.
# The suffix is the commit's time, so a later commit is a higher version. A tree
# with uncommitted changes, or no git at all, uses the time now instead.
STAMP="$(git -C "$SCRIPT_DIR" log -1 --format=%ct 2>/dev/null || true)"
if [ -z "$STAMP" ] || [ -n "$(git -C "$SCRIPT_DIR" status --porcelain 2>/dev/null)" ]; then
  STAMP="$(date +%s)"
fi

echo "Building and installing the vela tool..."
rm -rf "$SCRIPT_DIR/nupkg"
if ! dotnet pack "$SCRIPT_DIR/src/Vela/Vela.csproj" -c Release -o "$SCRIPT_DIR/nupkg" \
    --version-suffix "dev.$STAMP" >"$PACK_LOG" 2>&1; then
  echo "dotnet pack failed. Output:" >&2
  cat "$PACK_LOG" >&2
  exit 1
fi

PACKAGES=("$SCRIPT_DIR"/nupkg/vela.*.nupkg)
if [ "${#PACKAGES[@]}" -ne 1 ] || [ ! -f "${PACKAGES[0]}" ]; then
  echo "dotnet pack did not leave exactly one vela package in $SCRIPT_DIR/nupkg." >&2
  exit 1
fi
VERSION="$(basename "${PACKAGES[0]}" .nupkg)"
VERSION="${VERSION#vela.}"

# The exact version, so this build is the one installed even if a feed has a
# newer one, and --allow-downgrade, so an older checkout's build still replaces
# a newer install rather than being skipped.
dotnet tool update --global --add-source "$SCRIPT_DIR/nupkg" vela \
  --version "$VERSION" --allow-downgrade

mkdir -p "$SKILLS_ROOT"
for src in "$SCRIPT_DIR"/skills/*/; do
  src="${src%/}"
  name="$(basename "$src")"
  echo "Installing skill '$name' -> $SKILLS_ROOT/$name"
  ln -sfn "$src" "$SKILLS_ROOT/$name"
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
