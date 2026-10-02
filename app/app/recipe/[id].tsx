import { router, useLocalSearchParams } from "expo-router";
import { useCallback, useState } from "react";
import { useFocusEffect } from "expo-router";
import {
  ActivityIndicator, Linking, Pressable, ScrollView, StyleSheet, Text, View,
} from "react-native";
import { api, type Ingredient, type Recipe } from "../../lib/api";
import { t } from "../../lib/theme";

export default function RecipeScreen() {
  const { id } = useLocalSearchParams<{ id: string }>();
  const [recipe, setRecipe] = useState<Recipe | null>(null);
  const [showing, setShowing] = useState<string | undefined>(undefined);

  const load = useCallback(async (recipeId: string) => {
    setRecipe(await api.get<Recipe>(`/api/recipes/${recipeId}`));
  }, []);

  useFocusEffect(useCallback(() => { load(showing ?? id); }, [load, id, showing]));

  if (!recipe) return <ActivityIndicator style={{ marginTop: 32 }} />;

  const quantified = recipe.ingredients.filter((i) => i.quantity !== null).length;

  return (
    <ScrollView contentContainerStyle={styles.wrap}>
      <Text style={styles.title}>{recipe.title || "Untitled"}</Text>

      {recipe.creatorHandle && <Text style={styles.muted}>@{recipe.creatorHandle}</Text>}

      {recipe.sourceUrl && (
        <Pressable onPress={() => Linking.openURL(recipe.sourceUrl!)}>
          <Text style={styles.link}>Watch the original →</Text>
        </Pressable>
      )}

      <View style={styles.stats}>
        <Stat label="serves" value={recipe.servings} />
        <Stat label="prep" value={recipe.prepMinutes} />
        <Stat label="cook" value={recipe.cookMinutes} />
        <Stat label="with amounts" value={`${quantified}/${recipe.ingredients.length}`} />
      </View>

      <Pressable style={styles.cta} onPress={() => router.push(`/cook/${recipe.id}`)}>
        <Text style={styles.ctaText}>Start cooking</Text>
      </Pressable>

      {quantified < recipe.ingredients.length && (
        <Text style={styles.note}>
          Amounts missing where the video never said one. Narration and on-screen text
          carry the method, not the measurements.
        </Text>
      )}

      <Text style={styles.heading}>Ingredients</Text>
      {groupBy(recipe.ingredients).map(([section, items]) => (
        <View key={section ?? "_"}>
          {section && <Text style={styles.section}>{section}</Text>}
          {items.map((i, n) => (
            <View key={n} style={styles.ingredient}>
              <Text style={styles.qty}>
                {[i.quantity, i.unit].filter(Boolean).join(" ") || "—"}
              </Text>
              <Text style={styles.item}>
                {i.item}
                {i.prepNote ? <Text style={styles.muted}> — {i.prepNote}</Text> : null}
              </Text>
            </View>
          ))}
        </View>
      ))}

      <Text style={styles.heading}>Method</Text>
      {recipe.steps.map((s, n) => (
        <Text key={n} style={styles.step}>{n + 1}. {s.text}</Text>
      ))}
    </ScrollView>
  );
}

function Stat({ label, value }: { label: string; value: string | number | null }) {
  return (
    <View>
      <Text style={styles.statValue}>{value ?? "—"}</Text>
      <Text style={styles.muted}>{label}</Text>
    </View>
  );
}

/** Keeps the recipe's own sections, in source order — that is the order you cook them. */
function groupBy(ingredients: Ingredient[]): [string | null, Ingredient[]][] {
  const groups = new Map<string | null, Ingredient[]>();

  for (const i of ingredients) {
    const key = i.group?.trim() || null;
    const found = groups.get(key);
    if (found) found.push(i);
    else groups.set(key, [i]);
  }

  return [...groups.entries()];
}

const styles = StyleSheet.create({
  wrap: { padding: t.pad, gap: 8, paddingBottom: 48 },
  title: { fontSize: 24, fontWeight: "700", color: t.ink, letterSpacing: -0.5 },
  muted: { color: t.muted, fontSize: 13 },
  link: { color: t.accent, fontSize: 15, marginVertical: 4 },
  stats: {
    flexDirection: "row", gap: 24, backgroundColor: t.panel, borderWidth: 1,
    borderColor: t.line, borderRadius: t.radius, padding: 14, marginVertical: 8,
  },
  statValue: { fontSize: 20, fontWeight: "700", color: t.ink },
  cta: { backgroundColor: t.accent, borderRadius: t.radius, padding: 16, alignItems: "center" },
  ctaText: { color: "#fff", fontSize: 16, fontWeight: "600" },
  note: { color: t.muted, fontSize: 13, lineHeight: 19, marginTop: 8 },
  heading: { fontSize: 17, fontWeight: "700", color: t.ink, marginTop: 20 },
  section: {
    fontSize: 12, fontWeight: "700", color: t.muted,
    textTransform: "uppercase", letterSpacing: 1, marginTop: 12, marginBottom: 4,
  },
  ingredient: { flexDirection: "row", gap: 12, paddingVertical: 5 },
  qty: { width: 90, textAlign: "right", color: t.muted, fontVariant: ["tabular-nums"] },
  item: { flex: 1, color: t.ink, fontSize: 15 },
  step: { color: t.ink, fontSize: 15, lineHeight: 22, marginTop: 8 },
});
