import { isolateModules, keychain, mockFetch, reply, tokens } from "./support";

isolateModules();

/**
 * Expo Router, reduced to the two things the gate uses: where we are, and the ability to
 * replace where we are.
 */
jest.mock("expo-router", () => {
  const react = require("react");

  return {
    __esModule: true,
    router: { replace: jest.fn() },
    useSegments: jest.fn(() => [] as string[]),
    Stack: Object.assign(
      ({ children }: { children?: unknown }) => react.createElement(react.Fragment, null, children),
      { Screen: () => null },
    ),
  };
});

/**
 * Mounts the gate at a given route.
 *
 * Everything is required *inside* here rather than imported at the top of the file,
 * because the suite resets the module registry between tests to give the api module a
 * clean session. A top-level import would hold a React from before the reset, and two
 * copies of React in one render fail as an invalid hook call.
 *
 * That also rules out the testing library, which registers its own jest hooks when it is
 * imported and so cannot be required from inside a test. The renderer underneath it is
 * enough for a component whose entire output is a spinner or a navigator.
 */
function mount(segments: string[]) {
  const react = require("react");
  const renderer = require("react-test-renderer");
  const { ActivityIndicator } = require("react-native");

  const router = require("expo-router");
  router.useSegments.mockReturnValue(segments);

  const api = require("../lib/api") as typeof import("../lib/api");
  const RootLayout = require("../app/_layout").default;

  const element = () => react.createElement(RootLayout);

  let tree: { root: { findAllByType: (t: unknown) => unknown[] }; update: (e: unknown) => void };
  renderer.act(() => {
    tree = renderer.create(element());
  });

  return {
    api,
    replace: router.router.replace as jest.Mock,
    act: renderer.act as (fn: () => Promise<void>) => Promise<void>,
    /** Whether the gate is still showing its "deciding" spinner. */
    isWaiting: () => tree.root.findAllByType(ActivityIndicator).length > 0,
    /** Simulates the navigation the gate just asked for. */
    at: (next: string[]) => {
      router.useSegments.mockReturnValue(next);
      renderer.act(() => tree.update(element()));
    },
    /** Lets the keychain read and any pending effects settle. */
    settle: async () => {
      await renderer.act(async () => {});
    },
  };
}

describe("the auth gate", () => {
  it("shows nothing and decides nothing until the keychain has been read", async () => {
    keychain().set("sousie.refresh", "refresh-1");
    const gate = mount([]);

    // Redirecting on this first pass is what would sign a returning user out on every
    // cold start, before the stored session had been found.
    expect(gate.replace).not.toHaveBeenCalled();
    expect(gate.isWaiting()).toBe(true);

    await gate.settle();
  });

  it("sends a signed-out visitor to sign-in", async () => {
    const gate = mount([]);
    await gate.settle();

    expect(gate.replace).toHaveBeenCalledWith("/sign-in");
  });

  it("leaves a returning user where they were", async () => {
    keychain().set("sousie.refresh", "refresh-1");
    const gate = mount([]);
    await gate.settle();

    expect(gate.replace).not.toHaveBeenCalled();
  });

  it("does not redirect someone already on the sign-in screen", async () => {
    const gate = mount(["sign-in"]);
    await gate.settle();

    expect(gate.replace).not.toHaveBeenCalled();
  });

  it("moves to the app the moment a sign-in succeeds", async () => {
    mockFetch(() => reply(tokens("access-1", "refresh-1")));
    const gate = mount(["sign-in"]);
    await gate.settle();

    await gate.act(async () => {
      await gate.api.signInWithApple("apple-identity-token");
    });

    expect(gate.replace).toHaveBeenCalledWith("/");
  });

  it("does not bounce back to sign-in once signed in", async () => {
    // The regression. The gate used to re-read the keychain asynchronously on every
    // navigation, so arriving home still carried the previous answer — signed out — and
    // redirected straight back. The tokens were written; the gate had not noticed yet.
    mockFetch(() => reply(tokens("access-1", "refresh-1")));
    const gate = mount(["sign-in"]);
    await gate.settle();

    await gate.act(async () => {
      await gate.api.signInWithApple("apple-identity-token");
    });

    // Now let the navigation to "/" actually land.
    gate.replace.mockClear();
    gate.at([]);
    await gate.settle();

    expect(gate.replace).not.toHaveBeenCalled();
  });

  it("sends the user to sign-in when the session ends mid-use", async () => {
    // A refresh token that no longer works clears the keychain from inside a request.
    // The user should land on sign-in then, not on whichever screen navigates next.
    keychain().set("sousie.refresh", "refresh-1");
    const gate = mount([]);
    await gate.settle();
    expect(gate.replace).not.toHaveBeenCalled();

    await gate.act(async () => {
      await gate.api.clearTokens();
    });

    expect(gate.replace).toHaveBeenCalledWith("/sign-in");
  });

  it("sends the user to sign-in after signing out", async () => {
    mockFetch(() => reply(null, { status: 204 }));
    keychain().set("sousie.refresh", "refresh-1");
    const gate = mount([]);
    await gate.settle();

    await gate.act(async () => {
      await gate.api.signOut();
    });

    expect(gate.replace).toHaveBeenCalledWith("/sign-in");
  });
});
