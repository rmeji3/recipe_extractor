import { useLocalSearchParams } from "expo-router";
import { useEffect, useState } from "react";
import {
  ActivityIndicator, Pressable, ScrollView, StyleSheet, Text, View,
} from "react-native";
import { api, type CookMode } from "../../lib/api";
import { t } from "../../lib/theme";

/**
 * Cook mode: numbered steps with the timers the server parsed out of them.
 *
 * Scaling changes quantities only — cooking times are deliberately left alone, because
 * doubling a recipe barely changes how long it takes and multiplying that would be
 * dangerous advice.
 */
export default function Cook() {
  const { id } = useLocalSearchParams<{ id: string }>();
  const [cook, setCook] = useState<CookMode | null>(null);
  const [servings, setServings] = useState<number | null>(null);
  const [done, setDone] = useState<Set<number>>(new Set());

  useEffect(() => {
    const query = servings ? `?servings=${servings}` : "";
    api.get<CookMode>(`/api/recipes/${id}/cook${query}`).then(setCook).catch(() => setCook(null));
  }, [id, servings]);

  if (!cook) return <ActivityIndicator style={{ marginTop: 32 }} />;

  return (
    <ScrollView contentContainerStyle={styles.wrap}>
      <Text style={styles.title}>{cook.title}</Text>

      <View style={styles.scaler}>
        <Text style={styles.muted}>Serves</Text>
        {[2, 4, 6, 8].map((n) => (
          <Pressable
            key={n}
            style={[styles.chip, cook.servings === n && styles.chipOn]}
            onPress={() => setServings(n)}
          >
            <Text style={cook.servings === n ? styles.chipTextOn : styles.chipText}>{n}</Text>
          </Pressable>
        ))}
      </View>

      <Text style={styles.heading}>Ingredients</Text>
      {cook.ingredients.map((i, n) => (
        <Text key={n} style={styles.ingredient}>
          {[i.quantity, i.unit].filter(Boolean).join(" ")} {i.item}
        </Text>
      ))}

      <Text style={styles.heading}>Method</Text>
      {cook.steps.map((step) => {
        const finished = done.has(step.number);

        return (
          <Pressable
            key={step.number}
            style={[styles.step, finished && styles.stepDone]}
            onPress={() => {
              const next = new Set(done);
              finished ? next.delete(step.number) : next.add(step.number);
              setDone(next);
            }}
          >
            <Text style={[styles.stepText, finished && styles.strike]}>
              {step.number}. {step.text}
            </Text>
            {step.timers.map((timer, n) => (
              <Text key={n} style={styles.timer}>
                ⏱ {Math.round(timer.seconds / 60)} min — {timer.label}
              </Text>
            ))}
          </Pressable>
        );
      })}
    </ScrollView>
  );
}

const styles = StyleSheet.create({
  wrap: { padding: t.pad, gap: 6, paddingBottom: 64 },
  title: { fontSize: 22, fontWeight: "700", color: t.ink },
  muted: { color: t.muted, fontSize: 14 },
  scaler: { flexDirection: "row", alignItems: "center", gap: 8, marginVertical: 12 },
  chip: {
    borderWidth: 1, borderColor: t.line, borderRadius: 99,
    paddingHorizontal: 14, paddingVertical: 6, backgroundColor: t.panel,
  },
  chipOn: { backgroundColor: t.accent, borderColor: t.accent },
  chipText: { color: t.ink },
  chipTextOn: { color: "#fff", fontWeight: "600" },
  heading: { fontSize: 17, fontWeight: "700", color: t.ink, marginTop: 18, marginBottom: 4 },
  ingredient: { color: t.ink, fontSize: 15, paddingVertical: 3 },
  step: {
    backgroundColor: t.panel, borderWidth: 1, borderColor: t.line,
    borderRadius: t.radius, padding: 14, marginTop: 8, gap: 6,
  },
  stepDone: { opacity: 0.5 },
  stepText: { color: t.ink, fontSize: 15, lineHeight: 22 },
  strike: { textDecorationLine: "line-through" },
  timer: { color: t.accent, fontSize: 14, fontWeight: "600" },
});
