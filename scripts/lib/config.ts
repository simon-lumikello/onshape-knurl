// Central configuration for the dev tooling.
//
// Machine-specific values (API keys, document and element ids) come from .env, which Bun loads
// automatically from the working directory. Everything else a developer might want to change
// (file locations, names in the test document, retry policy) lives here.
import { resolve } from "node:path";

/** Project root: the folder that contains knurl.fs. */
export const ROOT = resolve(import.meta.dir, "../..");

/** Project files, relative to ROOT. */
export const paths = {
  /** The feature source pushed to the "Knurl" Feature Studio. */
  feature: "knurl.fs",
  /** Seed feature that builds the test bodies, pushed to the "Test geometry" Feature Studio. */
  seedGeometry: "test/geometry.fs",
  /** Regression cases: Knurl features the dev loop creates or updates in the test Part Studio. */
  cases: "test/cases.json",
  /** Local, gitignored state: last pushed hashes and backups of overwritten remote edits. */
  stateDir: ".devstate",
};

/** Names used in the test document. */
export const seed = {
  studioName: "Test geometry",
  featureType: "knurlTestGeometry",
  featureName: "Knurl test geometry",
};

/** REST API behaviour. */
export const apiPolicy = {
  /** API version path (current version per https://cad.onshape.com/api/openapi). */
  basePath: "/api/v17",
  /** Retries after HTTP 409 (document changed while we were writing). */
  conflictRetries: 3,
  /** Retries after a short HTTP 429 (burst rate limit). */
  rateLimitRetries: 6,
  /** A Retry-After longer than this means the account quota is used up: stop instead of waiting. */
  maxRateLimitWaitSeconds: 120,
};

/** Feature id used when `dev.ts debug` runs a case inside the eval endpoint. */
export const debugFeatureId = "knurlDebug";

function requireEnv(name: string): string {
  const value = process.env[name]?.trim();
  if (!value) throw new Error(`Missing ${name} in .env (see .env.example)`);
  return value;
}

/** Onshape account and test document, from .env. */
export const onshape = {
  baseUrl: (process.env.ONSHAPE_BASE_URL?.trim() || "https://cad.onshape.com").replace(/\/$/, ""),
  accessKey: requireEnv("ONSHAPE_ACCESS_KEY"),
  secretKey: requireEnv("ONSHAPE_SECRET_KEY"),
  documentId: requireEnv("ONSHAPE_DID"),
  workspaceId: requireEnv("ONSHAPE_WID"),
  featureStudioId: requireEnv("ONSHAPE_FS_EID"),
  partStudioId: requireEnv("ONSHAPE_PS_EID"),
};
