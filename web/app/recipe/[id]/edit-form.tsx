"use client";

import Link from "next/link";
import { useState } from "react";
import { saveRecipe } from "@/app/actions";
import type { Recipe } from "@/lib/api";

/**
 * Plain textareas rather than a row per ingredient.
 *
 * This page exists to check extraction quality against the real video, which means fixing
 * a wrong line and moving on. Typing a list is much faster than tabbing through three
 * inputs per ingredient, and the server re-parses amounts on the way in.
 */
export function EditForm({ recipe }: { recipe: Recipe }) {
  const [saving, setSaving] = useState(false);

  const ingredients = recipe.ingredients
    .map((i) => [i.quantity ?? "", i.unit ?? "", i.item].filter(Boolean).join(" "))
    .join("\n");

  return (
    <form action={saveRecipe.bind(null, recipe.id)} onSubmit={() => setSaving(true)}>
      <div className="row wrap">
        <h1 className="grow">Edit</h1>
        <Link href={`/recipe/${recipe.id}`}>
          <button type="button" className="secondary">Cancel</button>
        </Link>
        <button type="submit" disabled={saving}>{saving ? "Saving…" : "Save"}</button>
      </div>

      <div className="card" style={{ display: "grid", gap: "1rem" }}>
        <label>
          <div className="small muted">Title</div>
          <input type="text" name="title" defaultValue={recipe.title} required />
        </label>

        <div className="row wrap">
          <label className="grow">
            <div className="small muted">Serves</div>
            <input type="number" name="servings" defaultValue={recipe.servings ?? ""} min={1} />
          </label>
          <label className="grow">
            <div className="small muted">Prep minutes</div>
            <input type="number" name="prepMinutes" defaultValue={recipe.prepMinutes ?? ""} min={0} />
          </label>
          <label className="grow">
            <div className="small muted">Cook minutes</div>
            <input type="number" name="cookMinutes" defaultValue={recipe.cookMinutes ?? ""} min={0} />
          </label>
        </div>

        <label>
          <div className="small muted">
            Ingredients — one per line, e.g. “2 tbsp soy sauce”. Leave the amount off if the
            video never said one.
          </div>
          <textarea name="ingredients" rows={12} defaultValue={ingredients} />
        </label>

        <label>
          <div className="small muted">Method — one step per line</div>
          <textarea
            name="steps"
            rows={10}
            defaultValue={recipe.steps.map((s) => s.text).join("\n")}
          />
        </label>

        <label>
          <div className="small muted">Equipment — comma separated</div>
          <input type="text" name="equipment" defaultValue={recipe.equipment.join(", ")} />
        </label>
      </div>

      <p className="small muted" style={{ marginTop: "0.8rem" }}>
        Saving marks the recipe as edited. Timestamps on steps you rewrite are dropped —
        they pointed at the moment the old wording came from.
      </p>
    </form>
  );
}
