import Link from "next/link";
import { api, ApiError, type Paginated, type RecipeSummary } from "@/lib/api";

export const dynamic = "force-dynamic";

export default async function CookbookPage({
  searchParams,
}: {
  searchParams: Promise<{ q?: string; status?: string }>;
}) {
  const { q = "", status = "" } = await searchParams;

  const query = new URLSearchParams({ pageSize: "50" });
  if (q) query.set("q", q);
  if (status) query.set("status", status);

  let page: Paginated<RecipeSummary> | null = null;
  let error: string | null = null;

  try {
    page = await api.get<Paginated<RecipeSummary>>(`/api/recipes?${query}`);
  } catch (e) {
    error = e instanceof ApiError ? e.message : String(e);
  }

  return (
    <>
      <h1>Cookbook</h1>
      <p className="lede">
        {page ? `${page.totalCount} recipes` : "Everything extracted so far."}
      </p>

      <form className="card row wrap" action="/cookbook">
        <input
          className="grow"
          type="search"
          name="q"
          placeholder="Search title, ingredients, creator…"
          defaultValue={q}
        />
        <select name="status" defaultValue={status} style={{ padding: "0.6rem" }}>
          <option value="">Any status</option>
          <option value="Extracted">Extracted</option>
          <option value="NeedsVision">Needs vision</option>
          <option value="Failed">Failed</option>
          <option value="NotARecipe">Not a recipe</option>
        </select>
        <button type="submit">Search</button>
      </form>

      {error && <div className="error" style={{ marginTop: "1rem" }}>{error}</div>}

      {page && page.items.length === 0 && (
        <p className="muted" style={{ marginTop: "1.5rem" }}>
          {q ? `Nothing matches “${q}”.` : "No recipes yet — add one from a link."}
        </p>
      )}

      <div className="list">
        {page?.items.map((recipe) => (
          <Link key={recipe.id} className="item" href={`/recipe/${recipe.id}`}>
            <div className="row wrap">
              <strong className="grow">{recipe.title || "Untitled"}</strong>
              {recipe.isEdited && <span className="tag flat">edited</span>}
              <StatusTag status={recipe.status} />
            </div>
            <div className="small muted" style={{ marginTop: "0.3rem" }}>
              {recipe.ingredientCount} ingredients · {recipe.stepCount} steps
              {recipe.creatorHandle ? ` · @${recipe.creatorHandle}` : ""}
            </div>
          </Link>
        ))}
      </div>
    </>
  );
}

function StatusTag({ status }: { status: string }) {
  const tone =
    status === "Extracted" ? "good" : status === "Failed" ? "bad" : "warn";
  return <span className={`tag ${tone}`}>{status}</span>;
}
