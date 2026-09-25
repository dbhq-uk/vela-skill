# Security

## Reporting a vulnerability

Email <dan@dbhq.uk> rather than opening a public issue. Include what you found,
how to reproduce it, and what an attacker could do with it. You will get a first
response within 48 hours.

## What this skill does

vela answers questions about code from an index it builds on your machine. For
.NET it loads the solution with MSBuild and compiles it with Roslyn. Any other
language arrives as a `.scip` file that language's own indexer wrote, which
`vela import` reads. Everything happens locally.

### Indexing runs the solution's build logic

**Index only code you trust.** `vela index` evaluates the solution with MSBuild
and runs its source generators, as `dotnet build` does. MSBuild targets and
source generators are code, supplied by the repository and by its packages, so
indexing a repository runs that code on your machine. Treat `vela index` on an
unfamiliar repository as you would treat `dotnet build` on it.

`vela import` only reads the `.scip` file. It runs nothing from it.

### Network

**None at runtime.** No source is uploaded, no index is sent anywhere, and there
is no service behind the skill.

vela expects a restored solution and does not restore it. Restoring it, with
`dotnet restore` or a build, fetches NuGet packages from your solution's own
package feeds, as it would for any build. That is your feed, not ours.

`install.sh` and `install-codex.sh` build vela from source, so that build
restores vela's own dependencies from nuget.org.

### On disk

- **The skill:** `~/.claude/skills/vela` for Claude Code, or `~/.codex/skills/vela`
  for Codex.
- **The `vela` command:** a .NET global tool in `~/.dotnet/tools`.
- **The index cache:** `~/.dbhq/vela` by default. `VELA_CACHE_HOME`, or failing
  that `XDG_CACHE_HOME`, moves it. An index names every symbol and file path in
  the code it covers, so treat it as you would the code. `vela cache clear`
  removes indexes.
- **Never modifies the repository.** vela reads the solution; it does not edit
  it. The index is kept outside the repository, and vela refuses to index if the
  cache directory resolves to somewhere inside it.
- `dotnet` uses its own `~/.dotnet` and `~/.nuget` directories, as it does for
  any build.

### Credentials

None. The skill reads no credential store and holds no account.

## Why it is deterministic

Results come from the compiler, not from a model or a heuristic. The same
solution at the same commit produces the same answer, which is the property that
makes "what would this change break?" worth trusting.
