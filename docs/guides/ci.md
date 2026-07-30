# Running vela in CI, and keeping an index fresh

**A how-to guide.** How to build an index in a pipeline, how to make a job fail when the
index is incomplete, and how to keep a local index current without thinking about it.

## What CI can use vela for

vela reports; it does not change code. So in a pipeline it is useful for exactly two
things:

1. **Asserting coverage that would otherwise regress silently.** `vela index --stats`
   prints counts. If your Razor view count drops to zero, nothing errors: the index still
   builds and every query still answers, and half your presentation layer is quietly
   missing. A count is the only thing that shows it.
2. **Answering a question a later step acts on.** "Does anything still reference this
   symbol" is a checkable fact.

If you want a gate that fails on a broken index, the exit code is already the gate.

## Exit codes are the contract

| Code | Meaning | What CI should do |
|---|---|---|
| `0` | Answered, no problem reported. | Continue. |
| `1` | Could not answer at all. | Fail. Something is misconfigured. |
| `3` | Answered, and the index behind it is known to be incomplete, stale or unverifiable. | Fail, unless you have decided otherwise on purpose. |

The banner above the results always says which reason applies, so a failed job's log names
the cause.

## The minimal job

```yaml
name: index
on: [push, pull_request]

jobs:
  index:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'
      - run: dotnet build
      - run: |
          git clone --depth 1 https://github.com/dbhq-uk/vela-skill.git /tmp/vela-skill
          dotnet pack /tmp/vela-skill/src/Vela/Vela.csproj -c Release -o /tmp/vela-nupkg
          dotnet tool install --global --add-source /tmp/vela-nupkg vela
          echo "$HOME/.dotnet/tools" >> "$GITHUB_PATH"
      - run: vela index --stats
```

vela is not published to NuGet.org, so CI builds it from source, which is what `install.sh`
does locally.

`vela index` exits 3 if any project failed to load, any project compiled with errors, any
document could not be placed, or any configured job has not been imported. The step fails
on a non-zero exit, so the gate is already there.

Two notes on that job:

- The solution has to restore, or MSBuild cannot load the projects, which is why
  `dotnet build` runs first.
- The index goes to `$XDG_CACHE_HOME/vela` or `~/.cache/vela`, which on a fresh runner is
  empty. Nothing is written into the checkout.

## Asserting coverage explicitly

`--stats` prints the numbers; grep asserts on them. This is a shell-level version of the
test vela runs on itself:

```bash
vela index --stats > stats.txt
cat stats.txt

on_disk=$(find . -name '*.cshtml' -o -name '*.razor' \
          | grep -v '/obj/' | grep -v '/bin/' | wc -l)
indexed=$(awk '/^  razor views/ {print $4}' stats.txt)
occurrences=$(awk '/^  in razor views/ {print $5}' stats.txt)

test "$indexed" = "$on_disk" \
  || { echo "Razor coverage regressed: $indexed indexed, $on_disk on disk"; exit 1; }
test "$occurrences" -gt 0 \
  || { echo "Razor views indexed but empty: the #line mapping has collapsed"; exit 1; }
```

**Both checks are needed.** Seven empty Razor documents satisfy the first count and mean the
position mapping has broken. vela's own CI asserts both by count, in
`EndToEndTests.IndexWithStats_ReportsTheCoverageThatMustNotRegress`.

## Caching the index between runs

Do not. The index records the time it was built and compares that against the modification
times of source files under the repository root. A fresh checkout rewrites every file, so a
restored index is stale by construction and every query will exit 3 telling you so.

Cache the NuGet packages instead. That is where the time goes:

```yaml
      - uses: actions/cache@v4
        with:
          path: ~/.nuget/packages
          key: ${{ runner.os }}-nuget-${{ hashFiles('**/*.csproj') }}
```

Indexing itself costs about what a build costs. On the 375,608-line solution vela is
developed against, a full index took 2 minutes 12 seconds at 1.5 GB peak, measured on
29 July 2026. On a `dotnet new webapp` scaffold it is about 8 seconds.

**`vela index --incremental` will not help a pipeline**, and the reason is worth stating so
nobody adds the flag hoping. A fresh runner has no index, so the first thing the flag does
is print `there is no index at ... yet, so there is nothing to compare this tree against`
and build the whole thing. Where it pays is a working copy that persists between runs: a
developer's machine, or a self-hosted runner that keeps its cache directory. See
[keeping a local index fresh](#keeping-a-local-index-fresh).

## A polyglot pipeline

Order matters: index, then import, in that order, because `vela index` rebuilds the database
from nothing.

```yaml
      - run: dotnet build
      - run: vela index                       # exits 3: the typescript job is not imported
        continue-on-error: true
      - run: npm ci && npx scip-typescript index --output index.scip
        working-directory: web
      - run: vela import web/index.scip       # exits 0: the gap is closed
```

The `continue-on-error` on the index step is deliberate and is the only place it belongs: at
that point in the pipeline the index genuinely is incomplete, and vela is right to say so.
Once the import has run, no step should be allowed to fail quietly.

If you prefer a single gate at the end, run a cheap query afterwards and let its exit code
decide. Every verb folds each imported source's live problems into the same health record,
so any query answers with exit 3 while a job is outstanding:

```yaml
      - run: vela find Anything > /dev/null    # exits 3 if anything is still outstanding
```

See [the multi-language guide](multi-language.md) for what those steps do and why the
pending job degrades the index until it is imported.

## Keeping a local index fresh

The index is a snapshot. Every query compares its build time against the files it watches,
and says so if one is newer, so a stale index cannot be mistaken for a current one. The
question is only how often you want to pay for a rebuild.

**Re-index after a change, not before every query.** A query costs well under a second; an
index costs about what a build costs.

The cheapest habit is to hang it off whatever you already run:

```bash
# after a build
dotnet build && vela index
```

Or as a git hook, if you switch branches often:

```bash
# .git/hooks/post-checkout, post-merge
#!/bin/sh
command -v vela >/dev/null 2>&1 && vela index >/dev/null 2>&1 &
```

**Do not read a quiet answer as proof the tree is unchanged.** The freshness check watches
`.cs`, `.vb`, `.cshtml`, `.razor`, `.csproj`, `.vbproj`, `.sln`, `.slnx`, `.props` and
`.targets`, and never descends into `bin`, `obj`, `.git`, `.vs`, `.idea` or `node_modules`.
That is deliberately narrower than what is indexed: walking everything cost more than the
queries did, and watching build output would leave every query permanently degraded, which
is a warning nobody reads. It also means a checked-in generated artefact with another
extension, or a `Directory.Build.props` inside `obj`, changes nothing the check can see.

It is timestamps only. Nothing is read and nothing is hashed.

### Paying less for it, sometimes

`vela index --incremental` rebuilds only the projects whose inputs changed and everything
downstream of them. It is **off by default** and it should stay that way until you have a
reason: a full rebuild cannot be stale, because it reads everything, whereas an incremental
one is a claim that what it skipped has not changed.

What it is worth depends entirely on where you edited. Measured on the ten-project,
375,608-line solution vela is developed against, on 30 July 2026:

| What changed | Wall clock | What it rebuilt |
|---|---|---|
| nothing (full index for comparison) | 158.1s | all ten |
| nothing | **11.9s** | 0 of 10 |
| one line in a leaf project | **22.2s** | 1 of 10 |
| one line in the project everything depends on | **153.9s** | fell back to a full rebuild: 10 of 10 |

**Incremental helps most when you edit a leaf.** A change low in the dependency graph
rebuilds nearly everything and saves nothing, because every reference to the changed
project's types in every other project sits at a line number the change can move. On this
solution `ScentVerdict.Data` is upstream of all nine others, so one line in it invalidates
the whole index. That is the closure being right, not a defect.

Trying and failing is cheap: the fallback is decided before anything is harvested and reuses
the workspace load the rebuild needed anyway, so about 0.6s on a 155s rebuild. And it is
never silent. Every fallback prints its reason followed by `A full rebuild cannot be stale,
because it reads everything. This is the safe outcome and not a failure.`

So as a habit it is reasonable, and it degrades to what you already had:

```bash
dotnet build && vela index --incremental
```

Two things to know before you rely on it:

- **The first run on a tree whose build output is stale may fall back anyway**, reporting
  `its own inputs changed` for a project you did not touch. MSBuild regenerates
  `obj/**/*.AssemblyInfo.cs` and the compiler is handed it, so it is an input. It converges;
  the run after it does not do this. It errs towards rebuilding, which is the safe direction.
- **Assembly references are compared by path, not by content.** A package upgrade changes
  the path and is caught. A referenced assembly rebuilt in place at the same version is not.
  If something outside the solution was rebuilt, index without the flag.

Whenever you are unsure, drop the flag. A full index is always the safe answer, which is why
it is the default.

## Upgrading vela

The index carries a schema version. If you upgrade and the shape has changed, every verb
refuses to answer and tells you to re-index rather than querying a database it cannot read:

```
The index at ... was built against index schema version 8, and this vela reads schema
version 9. It cannot be queried, and answering from it anyway would risk a wrong answer
rather than no answer.
The index is a cache, so it is rebuilt rather than migrated.
Run: vela index --solution ...
```

Exit code 1. In a pipeline this never comes up, because the runner has no index to begin
with. On a developer's machine, re-index; there is nothing else to do.

## Next

- [Exit codes and every banner reason](../reference.md#exit-codes).
- [Answering real questions](querying.md).
