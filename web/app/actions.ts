"use server";

import { revalidatePath } from "next/cache";
import { redirect } from "next/navigation";
import { api, ApiError, type Recipe } from "@/lib/api";

export interface ActionState {
  error?: string;
  recipe?: Recipe;
}

/** The core product flow: one link in, one recipe out. */
export async function extractFromUrl(
  _previous: ActionState,
  form: FormData,
): Promise<ActionState> {
  const url = String(form.get("url") ?? "").trim();

  if (!url) {
    return { error: "Paste a TikTok or Instagram link." };
  }

  try {
    const recipe = await api.post<Recipe>("/api/recipes/from-url", { url });
    revalidatePath("/cookbook");
    return { recipe };
  } catch (error) {
    return { error: error instanceof ApiError ? error.message : String(error) };
  }
}

/** Re-runs the cascade for a post whose first attempt produced nothing usable. */
export async function reExtract(savedPostId: string, recipeId: string) {
  try {
    await api.post(`/api/recipes/extract/${savedPostId}`);
  } catch (error) {
    if (!(error instanceof ApiError)) throw error;
  }
  revalidatePath(`/recipe/${recipeId}`);
}

export async function saveRecipe(recipeId: string, form: FormData) {
  const ingredients = String(form.get("ingredients") ?? "")
    .split("\n")
    .map((line) => line.trim())
    .filter(Boolean)
    .map(parseIngredient);

  const steps = String(form.get("steps") ?? "")
    .split("\n")
    .map((line) => line.trim())
    .filter(Boolean)
    .map((text) => ({ text }));

  await api.put(`/api/recipes/${recipeId}`, {
    title: String(form.get("title") ?? "").trim(),
    servings: numberOrNull(form.get("servings")),
    prepMinutes: numberOrNull(form.get("prepMinutes")),
    cookMinutes: numberOrNull(form.get("cookMinutes")),
    ingredients,
    steps,
    equipment: String(form.get("equipment") ?? "")
      .split(",")
      .map((e) => e.trim())
      .filter(Boolean),
  });

  revalidatePath(`/recipe/${recipeId}`);
  redirect(`/recipe/${recipeId}`);
}

function numberOrNull(value: FormDataEntryValue | null): number | null {
  const text = String(value ?? "").trim();
  return text === "" ? null : Number(text);
}

/**
 * Reads "2 tbsp soy sauce" back into its parts.
 *
 * Editing is a plain textarea rather than a row-per-ingredient form: this page exists to
 * check extraction quality quickly, and typing a list is far faster than tabbing through
 * three inputs per line. Anything it cannot parse becomes the item, with no amount — which
 * is the honest outcome, since a guessed quantity is worse than a missing one.
 */
function parseIngredient(line: string) {
  const match = line.match(
    /^\s*(\d+(?:[.,]\d+)?(?:\s*\/\s*\d+)?)\s*([a-zA-Z]{1,12}\b)?\s*(.*)$/,
  );

  if (!match || !match[3]) {
    return { item: line, quantity: null, unit: null, prepNote: null, sourceTs: null };
  }

  return {
    quantity: Number(match[1].replace(",", ".")) || null,
    unit: match[2] ?? null,
    item: match[3].trim(),
    prepNote: null,
    sourceTs: null,
  };
}
