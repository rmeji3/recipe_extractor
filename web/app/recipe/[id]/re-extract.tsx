"use client";

import { useState, useTransition } from "react";
import { reExtract } from "@/app/actions";

/** Re-runs the cascade. Slow by nature — it refetches and re-reads the video. */
export function ReExtractButton({
  savedPostId,
  recipeId,
}: {
  savedPostId: string;
  recipeId: string;
}) {
  const [pending, startTransition] = useTransition();
  const [done, setDone] = useState(false);

  return (
    <button
      className="secondary"
      disabled={pending}
      onClick={() =>
        startTransition(async () => {
          await reExtract(savedPostId, recipeId);
          setDone(true);
        })
      }
    >
      {pending ? "Re-extracting…" : done ? "Done" : "Try again"}
    </button>
  );
}
