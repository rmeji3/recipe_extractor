import { Stack, router, useSegments } from "expo-router";
import { useEffect, useSyncExternalStore } from "react";
import { ActivityIndicator, View } from "react-native";
import { loadSession, sessionSnapshot, subscribeSession } from "../lib/api";
import { t } from "../lib/theme";

/**
 * Root layout and auth gate.
 *
 * Every screen but sign-in needs a session, so the check lives here rather than in each
 * one.
 *
 * The session comes from a store rather than a keychain read per navigation. Re-reading
 * asynchronously meant that immediately after sign-in this effect ran with the previous
 * answer and redirected back to the sign-in screen — the tokens were written, but the
 * gate had not learned it yet. Signing in and out now update the store as they happen,
 * so a redirect always acts on current state.
 *
 * It also means an expired session discovered mid-request lands the user on sign-in
 * straight away, rather than on whichever screen next happened to navigate.
 */
export default function RootLayout() {
  const session = useSyncExternalStore(subscribeSession, sessionSnapshot);
  const segments = useSegments();

  useEffect(() => {
    loadSession();
  }, []);

  useEffect(() => {
    // Unknown until the keychain has been read once; redirecting before then would
    // bounce a signed-in user out on every cold start.
    if (session === null) return;

    const onSignIn = segments[0] === "sign-in";

    if (!session && !onSignIn) router.replace("/sign-in");
    else if (session && onSignIn) router.replace("/");
  }, [session, segments]);

  if (session === null) {
    return (
      <View style={{ flex: 1, alignItems: "center", justifyContent: "center", backgroundColor: t.bg }}>
        <ActivityIndicator />
      </View>
    );
  }

  return (
    <Stack
      screenOptions={{
        headerStyle: { backgroundColor: t.panel },
        headerTintColor: t.ink,
        contentStyle: { backgroundColor: t.bg },
      }}
    >
      <Stack.Screen name="sign-in" options={{ headerShown: false }} />
      <Stack.Screen name="index" options={{ title: "Sousie" }} />
      <Stack.Screen name="cookbook" options={{ title: "Cookbook" }} />
      <Stack.Screen name="recipe/[id]" options={{ title: "Recipe" }} />
      <Stack.Screen name="cook/[id]" options={{ title: "Cooking" }} />
    </Stack>
  );
}
