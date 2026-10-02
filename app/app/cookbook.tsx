import { router, useFocusEffect } from "expo-router";
import { useCallback, useState } from "react";
import {
  ActivityIndicator, FlatList, Pressable, StyleSheet, Text, TextInput, View,
} from "react-native";
import { api, type Paginated, type RecipeSummary } from "../lib/api";
import { t } from "../lib/theme";

export default function Cookbook() {
  const [query, setQuery] = useState("");
  const [items, setItems] = useState<RecipeSummary[]>([]);
  const [total, setTotal] = useState(0);
  const [loading, setLoading] = useState(true);

  const load = useCallback(async (q: string) => {
    setLoading(true);
    try {
      const params = new URLSearchParams({ pageSize: "50" });
      if (q.trim()) params.set("q", q.trim());
      const page = await api.get<Paginated<RecipeSummary>>(`/api/recipes?${params}`);
      setItems(page.items);
      setTotal(page.totalCount);
    } finally {
      setLoading(false);
    }
  }, []);

  // Reload on focus: a recipe added on the previous screen should be here.
  useFocusEffect(useCallback(() => { load(query); }, [load, query]));

  return (
    <View style={styles.wrap}>
      <TextInput
        style={styles.input}
        value={query}
        onChangeText={setQuery}
        onSubmitEditing={() => load(query)}
        placeholder="Search title, ingredient, creator…"
        placeholderTextColor={t.muted}
        returnKeyType="search"
        autoCapitalize="none"
      />

      {loading && items.length === 0 ? (
        <ActivityIndicator style={{ marginTop: 32 }} />
      ) : (
        <FlatList
          data={items}
          keyExtractor={(r) => r.id}
          ListHeaderComponent={<Text style={styles.count}>{total} recipes</Text>}
          ListEmptyComponent={<Text style={styles.muted}>Nothing here yet.</Text>}
          contentContainerStyle={{ gap: 8, paddingBottom: 32 }}
          renderItem={({ item }) => (
            <Pressable style={styles.row} onPress={() => router.push(`/recipe/${item.id}`)}>
              <Text style={styles.title}>{item.title || "Untitled"}</Text>
              <Text style={styles.muted}>
                {item.ingredientCount} ingredients · {item.stepCount} steps
                {item.creatorHandle ? ` · @${item.creatorHandle}` : ""}
              </Text>
              {/* Variants are tabs on one dish, not separate rows. */}
              {item.variants.length > 0 && (
                <Text style={styles.variants}>
                  also: {item.variants.map((v) => v.label).join(" · ")}
                </Text>
              )}
            </Pressable>
          )}
        />
      )}
    </View>
  );
}

const styles = StyleSheet.create({
  wrap: { flex: 1, padding: t.pad, gap: 12 },
  input: {
    borderWidth: 1, borderColor: t.line, borderRadius: t.radius,
    padding: 12, fontSize: 16, backgroundColor: t.panel, color: t.ink,
  },
  count: { color: t.muted, fontSize: 13, marginBottom: 4 },
  row: {
    backgroundColor: t.panel, borderWidth: 1, borderColor: t.line,
    borderRadius: t.radius, padding: 14, gap: 3,
  },
  title: { fontSize: 16, fontWeight: "600", color: t.ink },
  muted: { color: t.muted, fontSize: 13 },
  variants: { color: t.accent, fontSize: 13 },
});
