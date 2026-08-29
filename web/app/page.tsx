"use client";

import { useActionState } from "react";
import Link from "next/link";
import { extractFromUrl, type ActionState } from "./actions";

export default function AddPage() {
  const [state, action, pending] = useActionState<ActionState, FormData>(
    extractFromUrl,
    {},
  );

  return (
    <>
      <h1>Add a recipe</h1>
      <p className="lede">
        Paste a TikTok or Instagram link. Short links work too.
      </p>

      <form action={action} className="card">
        <div className="row wrap">
          <input
            className="grow"
            type="text"
            name="url"
            placeholder="https://www.tiktok.com/@creator/video/..."
            defaultValue=""
            required
          />
          <button type="submit" disabled={pending}>
            {pending ? "Working…" : "Get recipe"}
          </button>
        </div>
        <p className="small muted" style={{ margin: "0.7rem 0 0" }}>
          A video nobody has extracted before takes up to a minute — it is fetched,
          transcribed, and read. One already in the shared cache comes back instantly.
        </p>
      </form>

      {state.error && (
        <div className="error" style={{ marginTop: "1rem" }}>
          {state.error}
        </div>
      )}

      {state.recipe && (
        <div className="card" style={{ marginTop: "1rem" }}>
          <div className="row wrap">
            <strong className="grow">{state.recipe.title || "Untitled"}</strong>
            <StatusTag status={state.recipe.status} />
          </div>
          <p className="small muted" style={{ margin: "0.4rem 0 0.8rem" }}>
            {state.recipe.ingredients.length} ingredients · {state.recipe.steps.length} steps
            {state.recipe.creatorHandle ? ` · @${state.recipe.creatorHandle}` : ""}
          </p>
          <Link href={`/recipe/${state.recipe.id}`}>Open it →</Link>
        </div>
      )}
    </>
  );
}

function StatusTag({ status }: { status: string }) {
  const tone =
    status === "Extracted" ? "good" : status === "Failed" ? "bad" : "warn";
  return <span className={`tag ${tone}`}>{status}</span>;
}
