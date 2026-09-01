/**
 * Server-side calls into the ASP.NET API.
 *
 * Everything goes through here rather than from the browser, so the dev-auth header stays
 * server-side and there is no CORS to configure. There is no login yet — the API stubs
 * authentication from a header under Development, and this passes a fixed user.
 */
const BASE = process.env.RECIPE_API ?? "http://localhost:5141";
const USER = process.env.RECIPE_USER ?? "dev-user";

export class ApiError extends Error {
  constructor(readonly status: number, message: string) {
    super(message);
  }
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  let response: Response;

  try {
    response = await fetch(`${BASE}${path}`, {
      ...init,
      headers: {
        "X-Dev-User": USER,
        "Content-Type": "application/json",
        ...init?.headers,
      },
      // Extraction can take a minute; never serve a stale answer for it either.
      cache: "no-store",
    });
  } catch {
    throw new ApiError(503, `Cannot reach the API at ${BASE}. Is it running?`);
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
};

// ---------------------------------------------------------------- API shapes

export type ExtractionStatus =
  | "Pending" | "Extracted" | "NeedsVision" | "Failed" | "NotARecipe" | "Processing";

/** Statuses that will not change without another extraction run. */
export const SETTLED: ExtractionStatus[] = [
  "Extracted", "NeedsVision", "Failed", "NotARecipe",
];

export type ClassificationStatus =
  | "Pending" | "Food" | "Uncertain" | "NotFood" | "Unclassifiable";

export interface Ingredient {
  /** Section of the dish — "Sauce", "Marinade". Null when the recipe is one list. */
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
  savedPostId: string;
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
  transcriptLanguage: string | null;
  isEdited: boolean;
  creatorHandle: string | null;
  sourceUrl: string | null;
  extractedAt: string | null;
  updatedAt: string;
}

export interface RecipeSummary {
  id: string;
  savedPostId: string;
  status: ExtractionStatus;
  title: string;
  ingredientCount: number;
  stepCount: number;
  foodConfidence: number;
  creatorHandle: string | null;
  sourceUrl: string | null;
  isEdited: boolean;
  updatedAt: string;
}

export interface SavedPost {
  id: string;
  platform: "Instagram" | "TikTok";
  platformItemId: string;
  url: string;
  kind: string;
  caption: string | null;
  creatorHandle: string | null;
  creatorName: string | null;
  hashtags: string[];
  savedAt: string | null;
  createdAt: string;
  metadataStatus: string;
  thumbnailUrl: string | null;
  classificationStatus: ClassificationStatus;
  foodConfidence: number;
  classifiedBy: string | null;
}

export interface Paginated<T> {
  items: T[];
  pageNumber: number;
  pageSize: number;
  totalCount: number;
  totalPages: number;
}

export interface ImportSummary {
  id: string;
  platform: string;
  submittedCount: number;
  importedCount: number;
  duplicateCount: number;
  createdAt: string;
  skippedCount: number | null;
}
