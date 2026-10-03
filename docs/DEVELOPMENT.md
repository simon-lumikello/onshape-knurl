# Developing Knurl

The feature is a single FeatureScript file, [`knurl.fs`](../knurl.fs). A small Bun/TypeScript tool
pushes it to Onshape through the REST API, regenerates a set of regression cases in a test document and
reports their status, so most iterations need no clicking in Onshape.

## Repository layout

```
knurl.fs                      the feature (the product)
test/geometry.fs              seed feature that builds the test bodies
test/cases.json               regression cases: Knurl features with fixed parameters
scripts/dev.ts                CLI for the dev loop (see "Commands")
scripts/probe-revolved.ts     geometry probe for cylinders and cones
scripts/probe-band.ts         geometry probe for rounded-rectangle bands
scripts/lib/config.ts         all tooling configuration (paths, names, retry policy)
scripts/lib/onshape.ts        REST client: request signing, 409/429 handling
scripts/lib/featurescript.ts  FeatureScript eval and value conversion
scripts/lib/fscheck.ts        compile diagnostics and debug runs through eval
scripts/lib/studio.ts         Feature Studio push and compile check
scripts/lib/partstudio.ts     regression cases, feature upserts, status report
scripts/lib/rings.ts          shared sampling for the probes
docs/ARCHITECTURE.md          how the feature builds its cutters, and why
```

## Setup

You need [Bun](https://bun.sh) and an Onshape account (the free plan works; its documents are public).

1. **Test document.** Create a document with a Part Studio and a Feature Studio named **Knurl**.
2. **API key.** Onshape → user icon → **My account** → **Developer** → **Create new API key**, with only
   the *read* and *write documents* scopes. The secret is shown once.
3. **Configuration.** Copy `.env.example` to `.env` and fill in the keys and ids. An Onshape URL reads
   `https://cad.onshape.com/documents/<did>/w/<wid>/e/<element id>`. `.env` is gitignored, and the
   scripts never print the keys.
4. `bun install`, then `bun run seed` once: it creates the **Test geometry** Feature Studio and adds the
   seed feature as the first feature of the Part Studio.

The FeatureScript std library version (3083.0) is the one Onshape puts into a new Feature Studio's
template; `knurl.fs` and `test/geometry.fs` pin it in their `import` lines.

## Commands

```bash
bun run dev                     # push knurl.fs, check it compiles, add/update all cases, report status
bun run status                  # report the status of the seed and the cases (no push)
bun run check                   # compile diagnostics with line numbers, without pushing
bun run typecheck               # typecheck the TypeScript tooling (offline)
bun run seed                    # push test/geometry.fs and add the seed feature

bun scripts/dev.ts --case band          # only add/update cases whose name contains "band"
bun scripts/dev.ts --cases my.json      # use another case file (scratch experiments; keep them in .devstate/)
bun scripts/dev.ts debug "tube"         # run one case inside eval, with stack traces
bun scripts/dev.ts faces 'qCreatedBy($seed + "cone", EntityType.FACE)'
bun scripts/dev.ts eval 'function(context is Context, queries) { return 1; }'
bun scripts/dev.ts suppress Knurl       # suppress / unsuppress test features by name substring
bun scripts/dev.ts unsuppress "Knurl shaft"
bun scripts/dev.ts help

bun scripts/probe-revolved.ts 0,0,0 0,0,1 10 0.25 10 30          # cylinder: crossings, air, rotation
bun scripts/probe-revolved.ts 60,60,0 0,0,1 8 0.25 10 11 --internal
bun scripts/probe-band.ts -60,0 15 15 6 0.2 6.75 12.75             # band: flats vs fillets
```

`bun run dev` exits 0 when every case regenerates, 2 when a case is in ERROR, and 1 on compile or API
failure.

## How the dev loop works

- **Push.** The Feature Studio is written with the `sourceMicroversion` of a fresh read and
  `rejectMicroversionSkew`, so a concurrent change fails with HTTP 409 and is retried from a new read.
- **Edits made in Onshape are never overwritten silently.** If the Feature Studio changed since the last
  push, the remote copy is saved to `.devstate/` and the push stops. Merge it, or rerun with `--force`.
- **Compile check.** An empty feature spec list means the studio does not compile. The API gives no
  messages for that, so `scripts/lib/fscheck.ts` rewrites the module into one lambda (same line numbers)
  and runs it through the eval endpoint, which reports parse and semantic errors.
- **Debug runs.** `debug <case>` runs the feature for one case inside eval, at that case's rollback
  position, so exceptions from std operations come back with line-numbered stack traces. A real
  feature only shows a bare `REGEN_ERROR` for those.
- **Cases.** Each case in `test/cases.json` is matched by name and added or updated. Every parameter is
  sent, with the spec default where the case is silent: features added through the API do not get
  defaults filled in, and a missing parameter fails the precondition. Query parameters are FeatureScript
  query expressions (`$seed` = the seed feature id), resolved to deterministic ids at the case's position
  in the feature list.
- **Status.** Feature states come from the features endpoint; the error, warning and info texts from
  `getFeatureError/Warning/Info` through eval.

## API quota

The free plan has a daily API allowance. When it is used up, Onshape answers HTTP 429 with a
`Retry-After` of many hours; the tooling stops and prints when it resets. Short rate limits are retried
automatically. Typical costs: `bun run dev` about 5 calls plus 1 per case and 1 per query parameter;
`check`, `debug`, `eval`, `faces` and each probe run 1 to 3 calls. Use the manual workflow while the
quota is exhausted.

## Manual workflow (no API)

1. Open the **Knurl** Feature Studio, select all, paste `knurl.fs`, and commit (Ctrl+S). Errors appear
   at the bottom of the editor.
2. In the Part Studio, edit or add Knurl features and check the results.
3. The next `bun run dev` notices that the Feature Studio was edited in Onshape and stops; rerun with
   `--force` once the local file has the same content.

## Regression baseline

After any change to `knurl.fs`, `bun run dev` must report every case as INFO with these results
(geometry checks in the CHANGELOG for 0.2.0 to 0.4.0 used the probes above):

| Case | Summary | Faces |
|---|---|---|
| Knurl cylinder | 30 grooves at 0 deg, pitch 2.094 mm on R10 mm | 92 |
| Knurl tilted helical | 36 grooves at 30 deg RH, pitch 1.511 mm on R10 mm | 74 |
| Knurl tube bore | 24 grooves at 20 deg LH, pitch 1.968 mm on R8 mm | 99 |
| Knurl shaft diamond | 33 + 33 grooves at 30 deg RH/LH, pitch 0.989 mm on R6 mm | 3469 |
| Knurl collar margin | 40 at 30 deg RH, pitch 1.36 mm; 40 at 45 deg LH, pitch 1.111 mm on R10 mm | 6150 |
| Knurl cone helical | 41 grooves at 30 deg RH, pitch 1.493 mm on R11.25 mm (cone mid radius) | 125 |
| Knurl plate diamond | 35 + 35 grooves at 45 deg RH/LH, pitch 2 mm on a planar face | 3261 |
| Knurl cube top straight | 10 grooves at 0 deg, pitch 2 mm on a planar face | 86 |
| Knurl band diamond | 63 + 63 grooves at 30 deg RH/LH, pitch 1.508 mm around a band of 109.699 mm | 3214 |

## Release

1. Bump the version in the `knurl.fs` header, `package.json` and `CHANGELOG.md`.
2. `bun run typecheck`, `bun run dev`, compare with the baseline.
3. Paste or push `knurl.fs` into the public release document's **Knurl** Feature Studio and create an
   Onshape version named after the release (e.g. `1.0.0`). Users add custom features from versions,
   so they only see a release once it is versioned.
4. Tag the commit (`git tag v1.0.0`).
