/**
 * Fake edges for the things the app cannot reach in a test: the keychain, Expo's config,
 * and the network.
 *
 * The keychain mock is a working in-memory store rather than a stack of jest.fn()s, so
 * the token read/write paths run for real and a test can assert on what was actually
 * persisted. It is built inside the factory and published on `globalThis` because
 * `jest.mock` is hoisted above everything else in the file.
 */

jest.mock("expo-secure-store", () => {
  // Reused rather than recreated: `jest.resetModules()` runs this factory again, and a
  // fresh Map each time would silently discard whatever a test had seeded.
  const globals = globalThis as Record<string, unknown>;
  globals.keychain ??= new Map<string, string>();
  const store = globals.keychain as Map<string, string>;

  return {
    getItemAsync: jest.fn(async (key: string) => store.get(key) ?? null),
    setItemAsync: jest.fn(async (key: string, value: string) => void store.set(key, value)),
    deleteItemAsync: jest.fn(async (key: string) => void store.delete(key)),
  };
});

jest.mock("expo-constants", () => ({
  __esModule: true,
  default: { expoConfig: { extra: { apiUrl: "http://api.test" } } },
}));

beforeEach(() => {
  // Touch the mock so its factory has run and the store exists.
  require("expo-secure-store");
  ((globalThis as Record<string, unknown>).keychain as Map<string, string>).clear();
});
