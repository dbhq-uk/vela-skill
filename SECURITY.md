# Security

## Reporting a vulnerability

Email <dan@dbhq.uk> rather than opening a public issue. Include what you found,
how to reproduce it, and what an attacker could do with it. You will get a first
response within 48 hours.

## What this skill does

Vela answers questions about a .NET solution by compiling it with Roslyn and
querying the result. Everything happens on your machine.

### Network

**None at runtime.** The analysis is local: Roslyn loads the solution, and
queries are answered from the compilation. No source is uploaded, no index is
sent anywhere, and there is no service behind the skill.

The one network dependency is indirect - building requires a .NET SDK, and
`dotnet` restores NuGet packages for the solution you point it at, exactly as it
would if you ran `dotnet build` yourself. That is your solution's package feed,
not ours.

### On disk

- Installs into `~/.claude/skills/vela` or `~/.codex`, depending on the agent
- Reads the solution you point it at
- **Never modifies the repository.** Vela answers questions; it does not edit
- `dotnet` uses its own `~/.dotnet` tooling directory, as it does for any build

### Credentials

None. The skill reads no credential store and holds no account.

## Why it is deterministic

Results come from the compiler, not from a model or a heuristic. The same
solution at the same commit produces the same answer, which is the property that
makes "what would this change break?" worth trusting.
