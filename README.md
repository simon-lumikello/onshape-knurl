# Knurl

Onshape FeatureScript custom feature that cuts knurl patterns into faces.

- `knurl.fs`: the feature (single file; can also be pasted by hand)
- `scripts/testgeom.fs`: seed feature that builds the test bodies
- `scripts/cases.json`: Knurl feature instances the dev loop creates or updates
- `scripts/dev.ts`, `scripts/onshape.ts`: REST API dev loop (Bun, no dependencies)

## Setup

### What exists in Onshape

Document **Knurl dev** (free plan, so it is public; keep it free of sensitive content):

| Element | Type | Purpose |
|---|---|---|
| Part Studio 1 | Part Studio | test model: seed feature first, then the Knurl test cases |
| Knurl | Feature Studio | receives `knurl.fs` |
| Test geometry | Feature Studio | receives `scripts/testgeom.fs` (created by `bun run seed`) |

Seed bodies (mm): cylinder Ø20×40 (axis Z), rounded cube 30³ with R5 fillets, plate 60×40×5,
cone frustum Ø30→Ø15 h30, tilted cylinder Ø20×40 (axis off the world axes).

FeatureScript std library: `3083.0`. This was taken from the default template of a new Feature
Studio and matches the Part Studio's own `geometry.fs` import and `libraryVersion`.

### Reading IDs from an Onshape URL

```
https://cad.onshape.com/documents/<did>/w/<wid>/e/<eid>
```
`did` = document, `wid` = workspace, `eid` = the open tab (element).
The Part Studio's `eid` was looked up with `GET /documents/d/{did}/w/{wid}/elements`.

### API key and .env

1. Onshape → user icon → **My account** → **Developer** → **Create new API key**.
2. Scopes: only **read** (`OAuth2Read`) and **write** (`OAuth2Write`) documents.
3. Copy `.env.example` to `.env` and fill in the keys and IDs. `.env` is gitignored.
   The scripts never print the keys.

Auth uses HMAC request signing (`Authorization: On <key>:HmacSHA256:<sig>`), API base `/api/v17`.

## Dev loop

Run from the project root (Bun reads `.env` from there):

```bash
bun run seed      # once: create/push Test geometry, add the seed feature
bun run dev       # push knurl.fs, check it compiles, upsert cases.json, print status + messages
bun run status    # status only
bun scripts/dev.ts --case cylinder                  # only cases whose name contains "cylinder"
bun scripts/dev.ts faces 'qCreatedBy($seed + "cone", EntityType.FACE)'
bun scripts/dev.ts eval 'function(context is Context, queries) { return 1; }'
bun scripts/fscheck.ts knurl.fs                     # compile errors with line numbers (no push needed)
bun scripts/dev.ts debug "tube"                     # run one case inside eval, with stack traces
bun scripts/dev.ts suppress Knurl                   # suppress / unsuppress features by name substring
bun scripts/dev.ts unsuppress "Knurl shaft"
bun scripts/dev.ts --cases .devstate/exp.json       # scratch case file instead of scripts/cases.json
bun scripts/probe-knurl.ts "0,0,0" "0,0,1" 10 0.5 10 30   # groove count / hand check (see file header)
bun scripts/probe-band.ts "-60,0" 15 15 6 0.2 12      # band: crossings and air on flats vs fillets
```

`bun run dev` exits 0 when all cases regenerate, 2 when a case has ERROR status, and 1 on
compile or API failure.

**API quota:** the free plan has a daily API call allowance. When it is used up, Onshape answers
HTTP 429 with a `Retry-After` of many hours, and the scripts stop with the time it resets. Short 429
waits are retried automatically. Each `bun run dev` costs about 5 calls plus 1 per case and 1 per query
parameter; the probe scripts cost 1 call per sampled ring. Use the manual workflow until the quota resets.

How it works:
- **Push**: `POST /featurestudios/.../e/{eid}` with the `sourceMicroversion` from a fresh GET
  and `rejectMicroversionSkew: true`. If the document moved in between, the API answers 409
  and the script re-reads and retries.
- **Edits made in Onshape are not overwritten**: if the Feature Studio changed since our last push,
  the remote copy is saved to `.devstate/` and the push stops. Merge it, or rerun with `--force`.
- **Compile check**: `GET .../featurespecs`. An empty list means the studio does not compile.
  The public API exposes no compile messages, so `scripts/fscheck.ts` rewrites the module into one
  lambda (same line numbers) and runs it through the eval endpoint, which does report PARSE and
  SEMANTIC errors. `dev.ts` prints these automatically on a compile failure.
- **Debug**: `dev.ts debug <case>` runs the feature for one case inside eval, at the rollback position
  of that case, so exceptions from std operations come back with line-numbered stack traces
  (a real feature only reports a bare `REGEN_ERROR` for those).
- **Cases**: each entry in `cases.json` is matched by name and added or updated. Every spec
  parameter is sent, with spec defaults for the ones a case leaves out: features added through
  the API do not get defaults filled in, and a missing parameter fails the precondition.
  Query parameters are FeatureScript query expressions (`$seed` = the seed feature id),
  resolved to deterministic ids by the eval endpoint, rolled back to just before that feature.
- **Status**: feature states from `GET .../features`. The error, warning and info text comes from
  `getFeatureError/Warning/Info` through the eval endpoint.

## Manual fallback (no API)

1. Open the **Knurl** Feature Studio, select all, paste the contents of `knurl.fs`, and click **Commit**
   (or press Ctrl+S). Errors appear at the bottom of the editor.
2. In the Part Studio, open the custom feature from the toolbar (or **Add custom features** the
   first time), select faces, and set parameters.
3. Report errors and a screenshot back. Next time `bun run dev` runs, it will notice the edit and back it up.
