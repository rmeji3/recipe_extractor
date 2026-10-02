import Constants from "expo-constants";
import * as SecureStore from "expo-secure-store";

/**
 * The API base.
 *
 * A phone cannot reach the Mac's `localhost`, so in development this has to be the
 * machine's LAN address and the API has to bind `0.0.0.0`. Set it in `app.json` under
 * `extra.apiUrl`.
 */
const BASE: string =
  (Constants.expoConfig?.extra?.apiUrl as string | undefined) ?? "http://localhost:5141";

const ACCESS_KEY = "sousie.access";
const REFRESH_KEY = "sousie.refresh";

// ----------------------------------------------------------------- session

/**
 * Whether there is a session, as a store the UI can subscribe to.
 *
 * The auth gate cannot re-read the keychain to answer this. Reading is asynchronous, so a
 * redirect fired on the same tick as a sign-in sees the old answer and bounces the user
 * straight back to the sign-in screen. Writing tokens updates this synchronously, which
 * is what makes the navigation deterministic.
 *
 * `null` means "not yet known" — at launch, before the keychain has been read once.
 */
let session: boolean | null = null;
const listeners = new Set<() => void>();

function setSession(value: boolean) {
  if (session === value) return;
  session = value;
  listeners.forEach((listener) => listener());
}

export function subscribeSession(listener: () => void) {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

export function sessionSnapshot() {
  return session;
}

/** Reads the keychain once at launch to seed the store. */
export async function loadSession() {
  setSession((await SecureStore.getItemAsync(REFRESH_KEY)) !== null);
}

export class ApiError extends Error {
  constructor(
    readonly status: number,
    message: string,
  ) {
    super(message);
  }
}

/** Thrown when the session is gone for good and the user has to sign in again. */
export class SignedOutError extends ApiError {
  constructor() {
    super(401, "Signed out");
  }
}

// ------------------------------------------------------------------ tokens

/**
 * Tokens live in the keychain, not AsyncStorage.
 *
 * The refresh token is a bearer credential for sixty days. Plain storage is readable on a
 * jailbroken device, and a leaked refresh token is a live session.
 */
async function readTokens() {
  const [access, refresh] = await Promise.all([
    SecureStore.getItemAsync(ACCESS_KEY),
    SecureStore.getItemAsync(REFRESH_KEY),
  ]);
  return { access, refresh };
}

async function writeTokens(access: string, refresh: string) {
  await Promise.all([
    SecureStore.setItemAsync(ACCESS_KEY, access),
    SecureStore.setItemAsync(REFRESH_KEY, refresh),
  ]);
  setSession(true);
}

export async function clearTokens() {
  await Promise.all([
    SecureStore.deleteItemAsync(ACCESS_KEY),
    SecureStore.deleteItemAsync(REFRESH_KEY),
  ]);
  setSession(false);
}

// ----------------------------------------------------------------- refresh

/**
 * In-flight refresh, shared.
 *
 * Access tokens last an hour, so several screens routinely discover expiry at the same
 * moment. Refresh tokens rotate on use, so two concurrent refreshes would race: the second
 * presents a token the first just revoked, and the user is signed out for no reason.
 */
let refreshing: Promise<string> | null = null;

async function refreshAccess(): Promise<string> {
  if (refreshing) return refreshing;

  refreshing = (async () => {
    const { refresh } = await readTokens();

    if (!refresh) throw new SignedOutError();

    const response = await fetch(`${BASE}/api/auth/refresh`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ refreshToken: refresh }),
    });

    if (!response.ok) {
      // The session is finished — expired, revoked, or already rotated away.
      await clearTokens();
      throw new SignedOutError();
    }

    const tokens = (await response.json()) as AuthTokens;
    await writeTokens(tokens.accessToken, tokens.refreshToken);
    return tokens.accessToken;
  })();

  try {
    return await refreshing;
  } finally {
    refreshing = null;
  }
}

// ----------------------------------------------------------------- request

async function request<T>(path: string, init: RequestInit = {}, retry = true): Promise<T> {
  const { access } = await readTokens();

  let response: Response;

  try {
    response = await fetch(`${BASE}${path}`, {
      ...init,
      headers: {
        "Content-Type": "application/json",
        ...(access ? { Authorization: `Bearer ${access}` } : {}),
        ...init.headers,
      },
    });
  } catch {
    throw new ApiError(0, `Cannot reach Sousie at ${BASE}.`);
  }

  // Expired access token: refresh once and replay. Doing this here rather than in every
  // screen is the difference between a session that feels permanent and one that logs
  // people out mid-recipe.
  if (response.status === 401 && retry) {
    await refreshAccess();
    return request<T>(path, init, false);
  }

  if (response.status === 429) {
    const seconds = Number(response.headers.get("Retry-After") ?? "60");
    throw new ApiError(429, `Too many requests. Try again in ${Math.ceil(seconds / 60)} minutes.`);
  }

  if (!response.ok) {
    throw new ApiError(response.status, (await response.text()) || response.statusText);
  }

  return response.status === 204 ? (undefined as T) : ((await response.json()) as T);
}

export const api = {
  get: <T,>(path: string) => request<T>(path),
  post: <T,>(path: string, body?: unknown) =>
    request<T>(path, { method: "POST", body: body ? JSON.stringify(body) : undefined }),
  put: <T,>(path: string, body: unknown) =>
    request<T>(path, { method: "PUT", body: JSON.stringify(body) }),
  delete: <T,>(path: string) => request<T>(path, { method: "DELETE" }),
};

// -------------------------------------------------------------------- auth

export interface AuthTokens {
  accessToken: string;
  expiresIn: number;
  refreshToken: string;
  user: { id: string; email: string | null; displayName: string | null };
}

/** Exchanges Apple's identity token for a Sousie session. */
export async function signInWithApple(identityToken: string, displayName?: string) {
  const response = await fetch(`${BASE}/api/auth/apple`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ identityToken, displayName }),
  });

  if (!response.ok) {
    throw new ApiError(response.status, (await response.text()) || "Sign-in failed.");
  }

  const tokens = (await response.json()) as AuthTokens;
  await writeTokens(tokens.accessToken, tokens.refreshToken);
  return tokens;
}

export async function signOut() {
  const { refresh } = await readTokens();

  if (refresh) {
    // Best effort: the server revokes the refresh token, but the local tokens go either
    // way. A failed call must not leave someone stuck signed in.
    await fetch(`${BASE}/api/auth/signout`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ refreshToken: refresh }),
    }).catch(() => undefined);
  }

  await clearTokens();
}

// ------------------------------------------------------------------ shapes

export type ExtractionStatus =
  | "Pending"
  | "Processing"
  | "Extracted"
  | "NeedsVision"
  | "Failed"
  | "NotARecipe";

/** Statuses that will not change without another extraction run. */
export const SETTLED: ExtractionStatus[] = ["Extracted", "NeedsVision", "Failed", "NotARecipe"];

export interface Ingredient {
  group: string | null;
  quantity: number | null;
  unit: string | null;
  item: string;
  prepNote: string | null;
  confidence: number;
  sourceTs: number | null;
}

export interface Step {
  text: string;
  tsStart: number | null;
  tsEnd: number | null;
}

export interface Recipe {
  id: string;
  savedPostId: string | null;
  status: ExtractionStatus;
  failureReason: string | null;
  title: string;
  servings: number | null;
  prepMinutes: number | null;
  cookMinutes: number | null;
  ingredients: Ingredient[];
  steps: Step[];
  equipment: string[];
  foodConfidence: number;
  isEdited: boolean;
  creatorHandle: string | null;
  sourceUrl: string | null;
  updatedAt: string;
  variantLabel: string | null;
  derivedFromRecipeId: string | null;
}

export interface RecipeVariant {
  id: string;
  label: string;
  ingredientCount: number;
  stepCount: number;
}

export interface RecipeSummary {
  id: string;
  status: ExtractionStatus;
  title: string;
  ingredientCount: number;
  stepCount: number;
  creatorHandle: string | null;
  isEdited: boolean;
  variants: RecipeVariant[];
}

export interface Paginated<T> {
  items: T[];
  totalCount: number;
  totalPages: number;
}

export interface CookTimer {
  seconds: number;
  label: string;
}

export interface CookStep {
  number: number;
  text: string;
  tsStart: number | null;
  timers: CookTimer[];
}

export interface CookMode {
  recipeId: string;
  title: string;
  servings: number | null;
  scaledBy: number;
  prepMinutes: number | null;
  cookMinutes: number | null;
  ingredients: Ingredient[];
  steps: CookStep[];
  equipment: string[];
  sourceUrl: string | null;
}

// ----------------------------------------------------------------- helpers

/**
 * Adds a link and waits for it to settle.
 *
 * A cold extraction is queued, not awaited — the server fetches, transcribes, and reads
 * the video, which takes up to a minute. A cache hit comes back finished immediately.
 */
export async function addFromUrl(
  url: string,
  onProgress?: (recipe: Recipe) => void,
): Promise<Recipe> {
  let recipe = await api.post<Recipe>("/api/recipes/from-url", { url });
  onProgress?.(recipe);

  const deadline = Date.now() + 3 * 60 * 1000;

  while (!SETTLED.includes(recipe.status) && Date.now() < deadline) {
    await new Promise((resolve) => setTimeout(resolve, 2000));
    recipe = await api.get<Recipe>(`/api/recipes/${recipe.id}`);
    onProgress?.(recipe);
  }

  return recipe;
}
