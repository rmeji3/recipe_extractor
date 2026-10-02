import { Link, router } from "expo-router";
import { useState } from "react";
import {
  ActivityIndicator, Pressable, StyleSheet, Text, TextInput, View,
} from "react-native";
import { addFromUrl, ApiError, signOut, type Recipe } from "../lib/api";
import { t } from "../lib/theme";

/**
 * The core flow: one link in, one recipe out.
 *
 * Paste for now. A share extension is the same call with the URL arriving from the share
 * sheet instead of the clipboard.
 */
export default function Add() {
  const [url, setUrl] = useState("");
  const [working, setWorking] = useState(false);
  const [status, setStatus] = useState<string | null>(null);
  const [recipe, setRecipe] = useState<Recipe | null>(null);
  const [error, setError] = useState<string | null>(null);

  async function onAdd() {
    setError(null);
    setRecipe(null);
    setWorking(true);

    try {
      const result = await addFromUrl(url.trim(), (progress) => {
        // A cache hit settles instantly; a cold video takes up to a minute while the
        // server fetches, listens to, and reads it.
        setStatus(
          progress.status === "Processing"
            ? "Watching the video…"
            : progress.status,
        );
      });

      setRecipe(result);
      setUrl("");
    } catch (e) {
      setError(e instanceof ApiError ? e.message : String(e));
    } finally {
      setWorking(false);
      setStatus(null);
    }
  }

  return (
    <View style={styles.wrap}>
      <Text style={styles.lede}>Paste a TikTok or Instagram link.</Text>

      <TextInput
        style={styles.input}
        value={url}
        onChangeText={setUrl}
        placeholder="https://www.tiktok.com/@…"
        placeholderTextColor={t.muted}
        autoCapitalize="none"
        autoCorrect={false}
        keyboardType="url"
        editable={!working}
      />

      <Pressable
        style={[styles.button, (working || !url.trim()) && styles.buttonOff]}
        onPress={onAdd}
        disabled={working || !url.trim()}
      >
        {working
          ? <ActivityIndicator color="#fff" />
          : <Text style={styles.buttonText}>Get recipe</Text>}
      </Pressable>

      {status && <Text style={styles.muted}>{status}</Text>}
      {error && <Text style={styles.error}>{error}</Text>}

      {recipe && (
        <Pressable style={styles.card} onPress={() => router.push(`/recipe/${recipe.id}`)}>
          <Text style={styles.cardTitle}>{recipe.title || "Untitled"}</Text>
          <Text style={styles.muted}>
            {recipe.status === "Extracted"
              ? `${recipe.ingredients.length} ingredients · ${recipe.steps.length} steps`
              : describe(recipe)}
          </Text>
        </Pressable>
      )}

      <View style={styles.footer}>
        <Link href="/cookbook" style={styles.link}>Cookbook →</Link>
        <Pressable onPress={() => signOut().then(() => router.replace("/sign-in"))}>
          <Text style={styles.muted}>Sign out</Text>
        </Pressable>
      </View>
    </View>
  );
}

function describe(recipe: Recipe): string {
  switch (recipe.status) {
    case "NeedsVision": return "Could not read a recipe from this one.";
    case "Failed": return recipe.failureReason ?? "The video could not be fetched.";
    case "NotARecipe": return "This does not look like a recipe.";
    default: return recipe.status;
  }
}

const styles = StyleSheet.create({
  wrap: { flex: 1, padding: t.pad, gap: 12 },
  lede: { color: t.muted, fontSize: 15 },
  input: {
    borderWidth: 1, borderColor: t.line, borderRadius: t.radius,
    padding: 14, fontSize: 16, backgroundColor: t.panel, color: t.ink,
  },
  button: {
    backgroundColor: t.accent, borderRadius: t.radius,
    padding: 16, alignItems: "center", minHeight: 52, justifyContent: "center",
  },
  buttonOff: { opacity: 0.5 },
  buttonText: { color: "#fff", fontSize: 16, fontWeight: "600" },
  card: {
    backgroundColor: t.panel, borderWidth: 1, borderColor: t.line,
    borderRadius: t.radius, padding: t.pad, gap: 4,
  },
  cardTitle: { fontSize: 17, fontWeight: "600", color: t.ink },
  muted: { color: t.muted, fontSize: 14 },
  error: { color: t.bad, fontSize: 14, lineHeight: 20 },
  footer: { marginTop: "auto", flexDirection: "row", justifyContent: "space-between" },
  link: { color: t.accent, fontSize: 16, fontWeight: "600" },
});
