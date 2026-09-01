import Link from "next/link";
import { notFound } from "next/navigation";
import { api, ApiError, type Recipe } from "@/lib/api";
import { EditForm } from "./edit-form";
import { ReExtractButton } from "./re-extract";

export const dynamic = "force-dynamic";

export default async function RecipePage({
  params,
  searchParams,
}: {
  params: Promise<{ id: string }>;
  searchParams: Promise<{ edit?: string }>;
}) {
  const { id } = await params;
  const { edit } = await searchParams;

  let recipe: Recipe;

  try {
    recipe = await api.get<Recipe>(`/api/recipes/${id}`);
  } catch (error) {
    if (error instanceof ApiError && error.status === 404) notFound();
    throw error;
  }

  if (edit) {
    return <EditForm recipe={recipe} />;
  }

  const quantified = recipe.ingredients.filter((i) => i.quantity !== null).length;

  return (
    <>
      <div className="row wrap">
        <h1 className="grow">{recipe.title || "Untitled"}</h1>
        <Link href={`/recipe/${recipe.id}?edit=1`}>
          <button className="secondary">Edit</button>
        </Link>
      </div>

      <p className="lede">
        {recipe.creatorHandle && <>@{recipe.creatorHandle} · </>}
        {recipe.sourceUrl && (
          <a href={recipe.sourceUrl} target="_blank" rel="noreferrer">
            watch the original
          </a>
        )}
      </p>

      <div className="card">
        <div className="stats">
          <div><b>{recipe.servings ?? "—"}</b><span className="small muted">serves</span></div>
          <div><b>{recipe.prepMinutes ?? "—"}</b><span className="small muted">prep min</span></div>
          <div><b>{recipe.cookMinutes ?? "—"}</b><span className="small muted">cook min</span></div>
          <div><b>{recipe.ingredients.length}</b><span className="small muted">ingredients</span></div>
          <div>
            <b className={quantified < recipe.ingredients.length / 2 ? "" : ""}>
              {quantified}/{recipe.ingredients.length}
            </b>
            <span className="small muted">with amounts</span>
          </div>
        </div>
      </div>

      {recipe.status !== "Extracted" && (
        <div className="card" style={{ marginTop: "1rem" }}>
          <div className="row wrap">
            <strong className="grow">{explain(recipe)}</strong>
            <ReExtractButton savedPostId={recipe.savedPostId} recipeId={recipe.id} />
          </div>
          {recipe.failureReason && (
            <p className="small muted" style={{ margin: "0.5rem 0 0" }}>
              {recipe.failureReason}
            </p>
          )}
        </div>
      )}

      {quantified < recipe.ingredients.length && recipe.ingredients.length > 0 && (
        <p className="small muted" style={{ marginTop: "1rem" }}>
          Ingredients without an amount were read from narration or from what is on screen —
          neither carries quantities. Only a caption the creator typed does.
        </p>
      )}

      <h2>Ingredients</h2>
      {recipe.ingredients.length === 0 ? (
        <p className="muted">None found.</p>
      ) : (
        groupIngredients(recipe.ingredients).map(([section, items]) => (
          <div key={section ?? "_"}>
            {section && <h3 className="section">{section}</h3>}
            <table className="recipe">
              <tbody>
                {items.map((ingredient, index) => (
                  <tr key={index}>
                    <td className="qty mono">
                      {ingredient.quantity ?? ""} {ingredient.unit ?? ""}
                    </td>
                    <td>
                      {ingredient.item}
                      {ingredient.prepNote && (
                        <span className="muted"> — {ingredient.prepNote}</span>
                      )}
                      {ingredient.confidence < 0.8 && (
                        <span className="tag warn" style={{ marginLeft: "0.5rem" }}>
                          unsure
                        </span>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ))
      )}

      <h2>Method</h2>
      {recipe.steps.length === 0 ? (
        <p className="muted">No steps found.</p>
      ) : (
        <ol className="steps">
          {recipe.steps.map((step, index) => (
            <li key={index}>
              {step.text}
              {step.tsStart !== null && (
                <span className="small muted mono"> · {formatTime(step.tsStart)}</span>
              )}
            </li>
          ))}
        </ol>
      )}

      {recipe.equipment.length > 0 && (
        <>
          <h2>Equipment</h2>
          <p className="muted">{recipe.equipment.join(", ")}</p>
        </>
      )}
    </>
  );
}

/**
 * Groups ingredients by their section, preserving first-appearance order.
 *
 * Deliberately not sorted: the order the creator listed the sections in is the order you
 * cook them, so a marinade that comes first should stay first.
 */
function groupIngredients(
  ingredients: Recipe["ingredients"],
): [string | null, Recipe["ingredients"]][] {
  const groups = new Map<string | null, Recipe["ingredients"]>();

  for (const ingredient of ingredients) {
    const key = ingredient.group?.trim() || null;
    const existing = groups.get(key);
    if (existing) existing.push(ingredient);
    else groups.set(key, [ingredient]);
  }

  return [...groups.entries()];
}

function explain(recipe: Recipe): string {
  switch (recipe.status) {
    case "NeedsVision":
      return "Nothing usable came out of the caption, the narration, or the frames.";
    case "Failed":
      return "The video could not be fetched.";
    case "NotARecipe":
      return "This does not look like a recipe.";
    default:
      return "Not extracted yet.";
  }
}

function formatTime(seconds: number): string {
  const m = Math.floor(seconds / 60);
  const s = Math.floor(seconds % 60);
  return `${m}:${String(s).padStart(2, "0")}`;
}
