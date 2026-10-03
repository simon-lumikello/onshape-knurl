// Minimal Onshape REST client.
//
// Auth: HMAC-SHA256 request signing, per https://onshape-public.github.io/docs/auth/apikeys/
// Credentials and ids come from .env via config.ts; they are never printed.
import { createHmac, randomBytes } from "node:crypto";
import { apiPolicy, onshape } from "./config.ts";

export class ApiError extends Error {
  /** Seconds from the Retry-After header, when the server sent one. */
  retryAfter?: number;
  constructor(public status: number, public body: string, message: string) {
    super(message);
  }
}

type Query = Record<string, string | number | boolean>;

/**
 * Calls the API and parses the JSON response.
 * Short rate limits (HTTP 429) are waited out and retried; a long Retry-After means the account's
 * API quota is used up, which is reported instead of waited for.
 */
export async function api<T = any>(method: string, path: string, body?: unknown, query?: Query): Promise<T> {
  for (let attempt = 0; ; attempt++) {
    try {
      return await request<T>(method, path, body, query);
    } catch (error) {
      if (!(error instanceof ApiError) || error.status !== 429 || attempt >= apiPolicy.rateLimitRetries) throw error;
      const waitSeconds = error.retryAfter ?? 2 ** (attempt + 1);
      if (waitSeconds > apiPolicy.maxRateLimitWaitSeconds) {
        const resetsAt = new Date(Date.now() + waitSeconds * 1000).toLocaleString();
        throw new ApiError(429, error.body,
          `Onshape API quota exhausted; it resets around ${resetsAt}. Use the manual workflow until then (docs/DEVELOPMENT.md).`);
      }
      console.error(`  (rate limited, waiting ${waitSeconds}s)`);
      await Bun.sleep(waitSeconds * 1000);
    }
  }
}

async function request<T>(method: string, path: string, body?: unknown, query?: Query): Promise<T> {
  const url = new URL(onshape.baseUrl + apiPolicy.basePath + path);
  for (const [key, value] of Object.entries(query ?? {})) url.searchParams.set(key, String(value));
  const contentType = "application/json";
  const date = new Date().toUTCString();
  const nonce = randomBytes(18).toString("hex").slice(0, 25);
  const response = await fetch(url, {
    method,
    redirect: "manual",
    headers: {
      "Content-Type": contentType,
      Accept: "application/json",
      Date: date,
      "On-Nonce": nonce,
      Authorization: signature(method, url, contentType, date, nonce),
    },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const text = await response.text();
  if (!response.ok) {
    // The message carries the response body only, never the request headers (they contain the key).
    const error = new ApiError(response.status, text, `${method} ${url.pathname} -> HTTP ${response.status}: ${text.slice(0, 500)}`);
    const retryAfter = Number(response.headers.get("retry-after"));
    if (retryAfter > 0) error.retryAfter = retryAfter;
    throw error;
  }
  return (text ? JSON.parse(text) : undefined) as T;
}

/** `On <accessKey>:HmacSHA256:<signature>` over method, nonce, date, content type, path and query. */
function signature(method: string, url: URL, contentType: string, date: string, nonce: string): string {
  const query = url.search.startsWith("?") ? url.search.slice(1) : url.search;
  const signed = [method, nonce, date, contentType, url.pathname, query, ""].join("\n").toLowerCase();
  const digest = createHmac("sha256", onshape.secretKey).update(signed, "utf8").digest("base64");
  return `On ${onshape.accessKey}:HmacSHA256:${digest}`;
}

/** Retries `operation` after HTTP 409 ("A concurrent update interfered"), which is transient. */
export async function withConflictRetry<T>(operation: (attempt: number) => Promise<T>, onRetry?: () => void): Promise<T> {
  for (let attempt = 1; ; attempt++) {
    try {
      return await operation(attempt);
    } catch (error) {
      if (!(error instanceof ApiError) || error.status !== 409 || attempt > apiPolicy.conflictRetries) throw error;
      onRetry?.();
      await Bun.sleep(1000 * attempt);
    }
  }
}

// Endpoint paths for the configured document.
const documentPath = () => `d/${onshape.documentId}/w/${onshape.workspaceId}`;
export const elementsPath = () => `/documents/${documentPath()}/elements`;
export const newFeatureStudioPath = () => `/featurestudios/${documentPath()}`;
export const featureStudioPath = (elementId = onshape.featureStudioId) => `/featurestudios/${documentPath()}/e/${elementId}`;
export const partStudioPath = (elementId = onshape.partStudioId) => `/partstudios/${documentPath()}/e/${elementId}`;
