import * as AppleAuthentication from "expo-apple-authentication";
import { router } from "expo-router";
import { useEffect, useState } from "react";
import { StyleSheet, Text, View } from "react-native";
import { signInWithApple } from "../lib/api";
import { t } from "../lib/theme";

export default function SignIn() {
  const [available, setAvailable] = useState<boolean | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    AppleAuthentication.isAvailableAsync().then(setAvailable);
  }, []);

  async function onPress() {
    setError(null);

    try {
      const credential = await AppleAuthentication.signInAsync({
        requestedScopes: [
          AppleAuthentication.AppleAuthenticationScope.FULL_NAME,
          AppleAuthentication.AppleAuthenticationScope.EMAIL,
        ],
      });

      if (!credential.identityToken) {
        setError("Apple did not return a token.");
        return;
      }

      // Apple sends the name on the first authorization only, and never again. If it is
      // not passed now it is lost for good, so send it whenever it is present.
      const name = [credential.fullName?.givenName, credential.fullName?.familyName]
        .filter(Boolean)
        .join(" ");

      await signInWithApple(credential.identityToken, name || undefined);
      router.replace("/");
    } catch (e: unknown) {
      // Cancelling is not a failure and should not show an error.
      if ((e as { code?: string }).code === "ERR_REQUEST_CANCELED") return;
      setError(e instanceof Error ? e.message : String(e));
    }
  }

  return (
    <View style={styles.wrap}>
      <Text style={styles.brand}>Sousie</Text>
      <Text style={styles.lede}>
        Turn the food videos you have saved into recipes you can actually cook from.
      </Text>

      {available === false && (
        <Text style={styles.error}>
          Sign in with Apple is not available on this device. It needs a real device or a
          simulator signed into an Apple ID.
        </Text>
      )}

      {available && (
        <AppleAuthentication.AppleAuthenticationButton
          buttonType={AppleAuthentication.AppleAuthenticationButtonType.SIGN_IN}
          buttonStyle={AppleAuthentication.AppleAuthenticationButtonStyle.BLACK}
          cornerRadius={t.radius}
          style={styles.button}
          onPress={onPress}
        />
      )}

      {error && <Text style={styles.error}>{error}</Text>}
    </View>
  );
}

const styles = StyleSheet.create({
  wrap: { flex: 1, justifyContent: "center", padding: 32, gap: 16, backgroundColor: t.bg },
  brand: { fontSize: 40, fontWeight: "700", color: t.ink, letterSpacing: -1 },
  lede: { fontSize: 16, color: t.muted, marginBottom: 24, lineHeight: 22 },
  button: { height: 50 },
  error: { color: t.bad, fontSize: 14, lineHeight: 20 },
});
