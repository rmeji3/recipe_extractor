"use client";

import { useActionState, useEffect, useState } from "react";
import Link from "next/link";
import { extractFromUrl, pollRecipe, type ActionState } from "./actions";
import { SETTLED, type Recipe } from "@/lib/api";

export default function AddPage() {
  const [state, action, pending] = useActionState<ActionState, FormData>(
    extractFromUrl,
    {},
  );

  // A cold extraction is queued, not awaited: the server fetches, transcribes, and reads
  // the video, which takes up to a minute. The row comes back Processing and settles here.
  const [live, setLive] = useState<Recipe | null>(null);
  const recipe = live ?? state.recipe ?? null;

  useEffect(() => {
    setLive(null);

    if (!state.recipe || SETTLED.includes(state.recipe.status)) {
      return;
    }

    let cancelled = false;
    const id = state.recipe.id;

    const timer = setInterval(async () => {
      const next = await pollRecipe(id);
      if (cancelled || !next) return;
      setLive(next);
      if (SETTLED.includes(next.status)) clearInterval(timer);
    }, 2000);

    return () => {
      cancelled = true;
      clearInterval(timer);
    };
  }, [state.recipe]);

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

      {recipe && (
        <div className="card" style={{ marginTop: "1rem" }}>
          <div className="row wrap">
            <strong className="grow">
              {recipe.status === "Processing"
                ? "Working on it…"
                : recipe.title || "Untitled"}
            </strong>
            <StatusTag status={recipe.status} />
          </div>
          <p className="small muted" style={{ margin: "0.4rem 0 0.8rem" }}>
            {recipe.status === "Processing" ? (
              <>Fetching the video, listening to it, and reading what is on screen. This
              takes up to a minute the first time anyone shares a video.</>
            ) : (
              <>
                {recipe.ingredients.length} ingredients · {recipe.steps.length} steps
                {recipe.creatorHandle ? ` · @${recipe.creatorHandle}` : ""}
              </>
            )}
          </p>
          {recipe.status !== "Processing" && (
            <Link href={`/recipe/${recipe.id}`}>Open it →</Link>
          )}
        </div>
      )}
    </>
  );
}

function StatusTag({ status }: { status: string }) {
  const tone =
    status === "Extracted"
      ? "good"
      : status === "Failed"
        ? "bad"
        : status === "Processing"
          ? "flat"
          : "warn";
  return <span className={`tag ${tone}`}>{status}</span>;
}
