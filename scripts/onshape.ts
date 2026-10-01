// Minimal Onshape REST client. Loads credentials from .env (Bun does this automatically).
// Auth: HMAC-SHA256 request signing, per https://onshape-public.github.io/docs/auth/apikeys/
// API base path: /api/v17 (current per https://cad.onshape.com/api/openapi, servers[0]).
import { createHmac, randomBytes } from "node:crypto";

function env(name: string): string {
  const v = process.env[name]?.trim();
  if (!v) throw new Error(`Missing ${name} in .env`);
  return v;
}

export const cfg = {
  base: (process.env.ONSHAPE_BASE_URL?.trim() || "https://cad.onshape.com").replace(/\/$/, ""),
  apiPath: "/api/v17",
  did: env("ONSHAPE_DID"),
  wid: env("ONSHAPE_WID"),
  fsEid: env("ONSHAPE_FS_EID"),
  psEid: process.env.ONSHAPE_PS_EID?.trim() || "",
};

const accessKey = env("ONSHAPE_ACCESS_KEY");
const secretKey = env("ONSHAPE_SECRET_KEY");

function sign(method: string, url: URL, contentType: string, date: string, nonce: string): string {
  const query = url.search.startsWith("?") ? url.search.slice(1) : url.search;
  const str = [method, nonce, date, contentType, url.pathname, query, ""].join("\n").toLowerCase();
  const sig = createHmac("sha256", secretKey).update(str, "utf8").digest("base64");
  return `On ${accessKey}:HmacSHA256:${sig}`;
}

export class ApiError extends Error {
  constructor(public status: number, public body: string, msg: string) { super(msg); }
}

export async function api<T = any>(method: string, path: string, body?: unknown, query?: Record<string, string | number | boolean>): Promise<T> {
  const url = new URL(cfg.base + cfg.apiPath + path);
  for (const [k, v] of Object.entries(query ?? {})) url.searchParams.set(k, String(v));
  const contentType = "application/json";
  const date = new Date().toUTCString();
  const nonce = randomBytes(18).toString("hex").slice(0, 25);
  const res = await fetch(url, {
    method,
    redirect: "manual",
    headers: {
      "Content-Type": contentType,
      Accept: "application/json",
      Date: date,
      "On-Nonce": nonce,
      Authorization: sign(method, url, contentType, date, nonce),
    },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const text = await res.text();
  if (!res.ok) {
    // Never echo request headers (they contain the access key).
    throw new ApiError(res.status, text, `${method} ${url.pathname} -> HTTP ${res.status}: ${text.slice(0, 500)}`);
  }
  return (text ? JSON.parse(text) : undefined) as T;
}

// Path helpers
export const fsPath = () => `/featurestudios/d/${cfg.did}/w/${cfg.wid}/e/${cfg.fsEid}`;
export const psPath = (eid = cfg.psEid) => `/partstudios/d/${cfg.did}/w/${cfg.wid}/e/${eid}`;
