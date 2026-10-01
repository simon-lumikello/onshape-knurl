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
```

`bun run dev` exits 0 when all cases regenerate, 2 when a case has ERROR status, and 1 on
compile or API failure.

How it works:
- **Push**: `POST /featurestudios/.../e/{eid}` with the `sourceMicroversion` from a fresh GET
  and `rejectMicroversionSkew: true`. If the document moved in between, the API answers 409
  and the script re-reads and retries.
- **Edits made in Onshape are not overwritten**: if the Feature Studio changed since our last push,
  the remote copy is saved to `.devstate/` and the push stops. Merge it, or rerun with `--force`.
- **Compile check**: `GET .../featurespecs`. An empty list means the studio does not compile.
  The public API exposes no compile messages, so open the Feature Studio tab to read them.
- **Cases**: each entry in `cases.json` is matched by name and added or updated.
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
