# Indexing other languages

**A how-to guide.** How to get TypeScript, Python, Go or anything else with a SCIP indexer
into the same database as your C#, and how `vela.json` makes the gap visible until you do.

vela indexes C#, Visual Basic, Razor Pages, MVC views and Blazor components. It emits and
reads [SCIP](https://github.com/scip-code/scip), the standard interchange format for this
kind of index, so an index another language's indexer produced can be merged in and queried
by the same verbs.

**vela does not run other indexers.** You run them; vela imports the result. See
[the SCIP ecosystem](../scip-ecosystem.md) for what to install for which language.

## The short version

```bash
vela index                          # your .NET
cd web && scip-typescript index     # their TypeScript
cd .. && vela import web/index.scip # both in one database
vela refs formatPrice               # answers from the TypeScript
vela refs RequestId                 # answers from the C#
```

The rest of this page is why you probably want a `vela.json` as well.

## Worked example

Start from the tutorial's scaffolded solution, and add a little TypeScript to it.

```bash
mkdir -p web/src
cat > web/src/format.ts <<'EOF'
export function formatPrice(pence: number): string {
  return `£${(pence / 100).toFixed(2)}`;
}
EOF
cat > web/src/cart.ts <<'EOF'
import { formatPrice } from "./format";

export function total(prices: number[]): string {
  return formatPrice(prices.reduce((a, b) => a + b, 0));
}
EOF
cat > web/tsconfig.json <<'EOF'
{ "compilerOptions": { "target": "ES2020", "module": "ESNext",
  "moduleResolution": "bundler", "strict": true }, "include": ["src"] }
EOF
```

### 1. Declare it

Put a `vela.json` beside the solution:

```json
{
  "version": 1,
  "solution": "RazorDemo.sln",
  "jobs": [
    { "language": "csharp", "indexer": "vela", "root": "." },
    { "language": "razor", "indexer": "vela", "root": "." },
    { "language": "typescript", "indexer": "scip-typescript", "root": "web" }
  ]
}
```

### 2. Index, and watch vela refuse to be quiet about the gap

```bash
vela index
```

```
Using vela.json at /home/you/velatut/vela.json: 3 job(s): csharp and razor from vela's own
indexer; typescript from scip-typescript at 'web'.
Indexed 23 documents to /home/you/.cache/vela/RazorDemo-6cbef186e8416dc7.db
No job covers javascript 1 file(s), so none of it is in this index. Nothing of yours is
missing that a job asked for; this is what the repository holds beside it.
The exclude list kept this count out of 3 director(ies) and rejected 0 further file(s).
1 configured job(s) are not in this index. vela does not run other indexers, so each one's
.scip has to be produced and imported; until it is, this index is missing that language and
says so on every answer:
  web/index.scip: nothing has been imported from it, and vela.json declares a typescript job
  rooted at 'web' that produces it, so no typescript from there is in this index. Run
  scip-typescript in 'web', then: vela import web/index.scip
!! The index is INCOMPLETE. web/index.scip: ...
```

Exit code 3. Every query against this index carries the banner until that exact file is
imported.

**This is the point of the file.** A config that declares a language and then silently does
not index it would be worse than no config at all, because you would believe the answers.

### 3. Run the other indexer

```bash
cd web && scip-typescript index --output index.scip && cd ..
```

### 4. Import it

```bash
vela import web/index.scip
```

```
Imported 2 document(s) and 17 occurrence(s) from /home/you/velatut/web/index.scip, produced
by scip-typescript, into /home/you/.cache/vela/RazorDemo-6cbef186e8416dc7.db
2 document(s) declare no position encoding, so their character offsets were read as UTF-16
code units, which is what every other row in this index means. ...
```

Exit code 0. That second paragraph is a note, not a warning: `scip.proto` asks indexers to
state what unit their character offsets count in, and every real `scip-typescript` index
leaves the field unset, so vela says which reading it chose rather than assuming silently.

### 5. Query across both

```bash
vela refs formatPrice
```

```
web/src/cart.ts
       1:10   ref  src.format.formatPrice
       4:10   ref  src.format.formatPrice
web/src/format.ts
       1:17   def  src.format.formatPrice

3 result(s)
```

```bash
vela refs RequestId
```

```
RazorDemo/Pages/Error.cshtml
      13:51   ref  RazorDemo.Pages.ErrorModel.RequestId
RazorDemo/Pages/Error.cshtml.cs
      11:20   def  RazorDemo.Pages.ErrorModel.RequestId
      13:56   ref  RazorDemo.Pages.ErrorModel.RequestId
      17:9    ref  RazorDemo.Pages.ErrorModel.RequestId

4 result(s)
```

One database, one set of verbs, two languages, and the exit code is 0 because nothing is
missing any more.

`vela index --stats` splits the pile back apart, so you can see what each half contributed
and which `.scip` the imported half came from:

```
documents            : 25
  generated          : 8   (compiled, not on disk)
  razor views        : 7   (.cshtml and .razor)
occurrences          : 2691
  in razor views     : 22
  definitions        : 189
sources              : 2   (where each document came from)
  roslyn harvest     : 23 document(s), 2670 occurrence(s)
  imported .scip     : 2 document(s), 21 occurrence(s)   /home/you/velatut/web/index.scip
```

### 6. Re-index without losing it

```bash
vela index
```

```
Replayed 1 imported .scip file(s) that the index this run replaced had been built from.
vela index rebuilds from nothing, so without this every imported language would leave the
index without a word being said:
  web/index.scip: 2 document(s) and 17 occurrence(s).
```

Exit code 0.

`vela index` deletes the database and rebuilds it, so without the replay every imported
language would disappear on the next routine re-index, silently, and the person least
likely to notice would be the one who ran it. The replay re-reads the same files through
the same importer.

It refuses to pretend, in two ways. A `.scip` whose content hash has changed since it was
imported is re-imported and **said** to have changed, because an indexer is normally re-run
over changed code between one index and the next. A `.scip` that has gone, or that will not
read, degrades the index and names itself, and that verdict is written under the same key
`vela import` clears, so producing the file and importing it settles it.

## Re-running an indexer after the code changed

Give the TypeScript something more to index, so the counts move:

```bash
cat > web/src/cart.ts <<'EOF'
import { formatPrice } from "./format";

export function subtotal(prices: number[]): number {
  return prices.reduce((a, b) => a + b, 0);
}

export function total(prices: number[]): string {
  return formatPrice(subtotal(prices));
}
EOF
cd web && scip-typescript index --output index.scip && cd ..
vela import web/index.scip --replace
```

Without `--replace` the second import collides on every document path and nothing is
written. With it, the documents that `.scip` names are deleted with their occurrences and
written again, and you are told how many went and how many came:

```
Imported 2 document(s) and 21 occurrence(s) from /home/you/velatut/web/index.scip, produced
by scip-typescript, into /home/you/.cache/vela/RazorDemo-6cbef186e8416dc7.db
Replaced 2 document(s) already in the index: 17 occurrence(s) removed and 21 written in
their place.
The paths this .scip names were rewritten, whoever contributed them. A document from any
other source that it does not name is untouched.
```

If the replacement holds fewer occurrences than what it replaced, vela says so. That is what
re-running the indexer over code that lost a symbol looks like, and it is also what a broken
indexer run looks like; vela cannot tell the two apart from the inside, so it states the
fact and assumes neither.

### Deleting a file

`--replace` also removes what the previous import of the same `.scip` left behind. Delete
`web/src/cart.ts`, re-run `scip-typescript`, and the new file simply does not name that
document. vela records which `.scip` every imported document came from, so it can take the
abandoned row out rather than leave `refs` answering from a file you deleted:

```
Imported 1 document(s) and 12 occurrence(s) from /home/you/velatut/web/index.scip, produced
by scip-typescript, into /home/you/.cache/vela/RazorDemo-6cbef186e8416dc7.db
Removed 1 document(s) with 9 occurrence(s): a previous import of this .scip put them in the
index and this one no longer names them. The index is smaller than it was.
```

Only that `.scip`'s own documents are ever removed this way. vela's C# harvest and any
other imported `.scip` are keyed separately and are not touched. If the new file names no
documents at all, everything that source contributed goes and vela says so in as many
words, because an emptied language must never be a silent one.

## Why the excludes are the feature

The other half of `vela.json` is the exclude list, and on a real repository it is the half
that matters more.

Measured on ScentVerdict, the solution vela is developed against, on 30 July 2026:

- A naive count by extension reports **12,036 Python files**. 11,950 of them are vendored
  `venv` and `site-packages`. Eighty-six are the repository's own.
- One directory, `src/ScentVerdict.Web/wwwroot/app/`, is **gitignored build output** of the
  mobile app: minified bundles, hash-named chunks, a service worker. It holds 66 of the
  208 JavaScript files that survive the default excludes.

Indexing that directory would index the same code twice, once as readable source and once
as bundles nobody can open, and a directory that appears and disappears between builds would
make the index non-deterministic.

So vela ships opinionated default excludes, and the config is how a repository overrides
them. With the defaults plus that one directory, the honest first-party picture of that
repository on that day was:

```
csharp 1975, razor 334, javascript 142, python 86, vue 48, sql 40, typescript 39, java 3
```

`vela index` prints what the exclude list kept out and which languages no job covers, so the
numbers can be checked rather than trusted, and they should be: that repository is live and
every one of these moves. Five of the 86 Python files are the same five counted twice,
because `.agents/skills` there is a symlink to `.claude/skills` and the walk follows it.

### Exclude syntax

Gitignore's rules, because that is the dialect you already know. A config's `exclude` list
is **appended** to the defaults, so state only what is different about your repository, and
`"!**/dist/"` is how you take a default back.

The one rule people trip over is inherited too: **a negation cannot re-include a file whose
parent directory is excluded.** vela follows git here rather than smoothing it away, because
the alternative surprise is silent.

Write a directory as `**/node_modules/` rather than `**/node_modules/**`. Both exclude the
same files, but the first lets the walk skip the subtree unread.

The full list of defaults and the full glob rules are in
[the reference](../reference.md#velajson).

### The excludes never shrink the compilation

What Roslyn compiles is what gets indexed. A generated file under `obj` that the compiler
was handed is in the index whatever `vela.json` says. The excludes govern what vela **says**
about a repository, not what the compiler saw, which is the only reading under which adding
a config cannot silently shrink an existing index.

## A repository with no .NET in it

Legitimate, and supported. Import into nothing:

```bash
vela import path/to/index.scip --solution whatever.sln
```

vela creates the database, writes a health record so later queries can vouch for it, and
answers.

## Things to know about imported indexes

These come up because SCIP leaves some things optional, and vela reports rather than
guesses:

- **Position encoding.** Indexers written in .NET, Java and TypeScript count character
  offsets in UTF-16 code units; Go, Rust, C++ and Python indexers count in something else.
  `scip.proto` asks indexers to state which, and most do not. vela reads unstated offsets as
  UTF-16, says how many documents that applied to, and notes that columns on lines holding
  non-ASCII characters may be off. The line and the file are right either way.
- **Occurrences with no symbol.** SCIP permits it. They are in the index and cannot be found
  by name, and vela counts them.
- **Colliding display names.** A module descriptor loses its file extension and any
  remaining dot becomes an underscore, so a folder `utils/` beside a file `utils.ts`, or a
  module `a.b.ts` beside `a_b.ts`, arrive at one name. Nothing is lost: each occurrence
  still carries the moniker it came with. vela names the collisions in the import report,
  because the ambiguity block groups by exactly that name and would otherwise present two
  symbols as one.
- **Monikers that do not fit the grammar.** Stored under the symbol itself.

None of these raise the exit code. They are properties of the file that was read.

## Next

- [Every other SCIP indexer, with install commands](../scip-ecosystem.md).
- [The `vela.json` reference](../reference.md#velajson).
- [Running vela in CI](ci.md).
