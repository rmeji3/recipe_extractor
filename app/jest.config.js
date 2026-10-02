/**
 * The preset supplies the React Native transform and environment; everything the app
 * actually talks to — the keychain, the network, Expo's config — is mocked in the setup
 * file so tests exercise real logic against fake edges.
 */
module.exports = {
  preset: "jest-expo",
  setupFilesAfterEnv: ["<rootDir>/jest-setup.ts"],
  // Tests live beside nothing: keeping them in one tree stops the Metro bundler and the
  // router's file-based routing from ever treating a test as a screen.
  testMatch: ["<rootDir>/__tests__/**/*.test.ts?(x)"],
};
