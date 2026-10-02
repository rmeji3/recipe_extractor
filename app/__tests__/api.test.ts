import { isolateModules, keychain, load, mockFetch, reply, tokens } from "./support";

isolateModules();

describe("the session store", () => {
  it("reports no session when the keychain is empty", async () => {
    const api = load();

    expect(api.sessionSnapshot()).toBeNull(); // not read yet
    await api.loadSession();

    expect(api.sessionSnapshot()).toBe(false);
  });

  it("reports a session when a refresh token is stored", async () => {
    keychain().set("sousie.refresh", "stored-refresh-token");

    const api = load();
    await api.loadSession();

    expect(api.sessionSnapshot()).toBe(true);
  });

  it("knows about a sign-in without re-reading the keychain", async () => {
    // The bug this replaces: the auth gate answered this question with an asynchronous
    // keychain read, so a redirect fired on the same tick as a sign-in saw the old
    // answer and sent the user back to the sign-in screen.
    mockFetch(() => reply(tokens("access-1", "refresh-1")));

    const api = load();
    await api.loadSession();
    expect(api.sessionSnapshot()).toBe(false);

    await api.signInWithApple("apple-identity-token");

    expect(api.sessionSnapshot()).toBe(true);
  });

  it("tells subscribers when the session starts and ends", async () => {
    mockFetch(() => reply(tokens("access-1", "refresh-1")));

    const api = load();
    const seen: (boolean | null)[] = [];
    api.subscribeSession(() => seen.push(api.sessionSnapshot()));

    await api.loadSession();
    await api.signInWithApple("apple-identity-token");
    await api.signOut();

    expect(seen).toEqual([false, true, false]);
  });

  it("stops telling a subscriber that has unsubscribed", async () => {
    mockFetch(() => reply(tokens("access-1", "refresh-1")));

    const api = load();
    const listener = jest.fn();
    const unsubscribe = api.subscribeSession(listener);

    await api.loadSession();
    unsubscribe();
    await api.signInWithApple("apple-identity-token");

    expect(listener).toHaveBeenCalledTimes(1);
  });
});

describe("signing in", () => {
  it("stores both tokens in the keychain", async () => {
    mockFetch(() => reply(tokens("access-1", "refresh-1")));

    await load().signInWithApple("apple-identity-token", "Rafael");

    expect(keychain().get("sousie.access")).toBe("access-1");
    expect(keychain().get("sousie.refresh")).toBe("refresh-1");
  });

  it("sends the name Apple only ever provides once", async () => {
    const calls = mockFetch(() => reply(tokens("access-1", "refresh-1")));

    await load().signInWithApple("apple-identity-token", "Rafael");

    expect(calls[0].body).toEqual({
      identityToken: "apple-identity-token",
      displayName: "Rafael",
    });
  });

  it("surfaces a rejected sign-in rather than storing anything", async () => {
    mockFetch(() => reply("That sign-in could not be verified.", { status: 400 }));

    const api = load();

    await expect(api.signInWithApple("bad-token")).rejects.toThrow(
      "That sign-in could not be verified.",
    );
    expect(keychain().has("sousie.refresh")).toBe(false);
  });
});

describe("signing out", () => {
  it("revokes the refresh token and clears the keychain", async () => {
    const calls = mockFetch(() => reply(null, { status: 204 }));
    keychain().set("sousie.access", "access-1");
    keychain().set("sousie.refresh", "refresh-1");

    await load().signOut();

    expect(calls[0].url).toBe("http://api.test/api/auth/signout");
    expect(calls[0].body).toEqual({ refreshToken: "refresh-1" });
    expect(keychain().size).toBe(0);
  });

  it("clears the keychain even when the server cannot be reached", async () => {
    // A failed revoke must not leave someone stuck signed in on their own device.
    mockFetch(() => new Error("network down"));
    keychain().set("sousie.refresh", "refresh-1");

    const api = load();
    await api.signOut();

    expect(keychain().size).toBe(0);
    expect(api.sessionSnapshot()).toBe(false);
  });
});

describe("an expired access token", () => {
  beforeEach(() => {
    keychain().set("sousie.access", "stale-access");
    keychain().set("sousie.refresh", "refresh-1");
  });

  it("is refreshed and the original request replayed", async () => {
    const calls = mockFetch((call) => {
      if (call.url.endsWith("/api/auth/refresh")) return reply(tokens("fresh-access", "refresh-2"));
      if (call.authorization === "Bearer stale-access") return reply(null, { status: 401 });
      return reply({ id: "recipe-1" });
    });

    const result = await load().api.get<{ id: string }>("/api/recipes/recipe-1");

    expect(result).toEqual({ id: "recipe-1" });
    expect(calls.map((c) => c.authorization)).toEqual([
      "Bearer stale-access",
      null, // the refresh call itself carries no bearer
      "Bearer fresh-access",
    ]);
  });

  it("rotates the stored tokens", async () => {
    mockFetch((call) => {
      if (call.url.endsWith("/api/auth/refresh")) return reply(tokens("fresh-access", "refresh-2"));
      if (call.authorization === "Bearer stale-access") return reply(null, { status: 401 });
      return reply({});
    });

    await load().api.get("/api/recipes/recipe-1");

    expect(keychain().get("sousie.access")).toBe("fresh-access");
    expect(keychain().get("sousie.refresh")).toBe("refresh-2");
  });

  it("is refreshed once even when several screens notice at the same moment", async () => {
    // Refresh tokens rotate on use, so a second concurrent refresh would present a token
    // the first had already revoked — and sign the user out for no reason.
    const calls = mockFetch((call) => {
      if (call.url.endsWith("/api/auth/refresh")) return reply(tokens("fresh-access", "refresh-2"));
      if (call.authorization === "Bearer stale-access") return reply(null, { status: 401 });
      return reply({});
    });

    const api = load();
    await Promise.all([
      api.api.get("/api/recipes"),
      api.api.get("/api/pantry"),
      api.api.get("/api/cookbook"),
    ]);

    expect(calls.filter((c) => c.url.endsWith("/api/auth/refresh"))).toHaveLength(1);
  });

  it("only retries once, so a server stuck on 401 cannot loop", async () => {
    const calls = mockFetch((call) =>
      call.url.endsWith("/api/auth/refresh")
        ? reply(tokens("fresh-access", "refresh-2"))
        : reply(null, { status: 401 }),
    );

    const api = load();
    await expect(api.api.get("/api/recipes")).rejects.toBeInstanceOf(api.ApiError);

    expect(calls).toHaveLength(3); // request, refresh, replay — and stop
  });

  it("ends the session when the refresh token is no longer good", async () => {
    mockFetch((call) =>
      call.url.endsWith("/api/auth/refresh")
        ? reply(null, { status: 400 })
        : reply(null, { status: 401 }),
    );

    const api = load();
    await api.loadSession();

    await expect(api.api.get("/api/recipes")).rejects.toBeInstanceOf(api.SignedOutError);
    expect(keychain().size).toBe(0);
    expect(api.sessionSnapshot()).toBe(false);
  });
});

describe("errors the user has to understand", () => {
  it("explains an unreachable server by name", async () => {
    mockFetch(() => new Error("connection refused"));

    const api = load();
    await expect(api.api.get("/api/recipes")).rejects.toThrow("Cannot reach Sousie at http://api.test.");
  });

  it("turns a rate limit into a wait the user can act on", async () => {
    mockFetch(() => reply(null, { status: 429, headers: { "Retry-After": "600" } }));

    const api = load();
    await expect(api.api.get("/api/recipes")).rejects.toThrow("Try again in 10 minutes.");
  });

  it("still gives a wait when the server sends no Retry-After", async () => {
    mockFetch(() => reply(null, { status: 429 }));

    const api = load();
    await expect(api.api.get("/api/recipes")).rejects.toThrow("Try again in 1 minutes.");
  });
});

describe("adding a recipe from a link", () => {
  beforeEach(() => jest.useFakeTimers());
  afterEach(() => jest.useRealTimers());

  const recipe = (status: string) => ({ id: "recipe-1", status, title: "Orzo" });

  it("returns immediately when the video is already known", async () => {
    // A cross-user cache hit comes back finished, and must not be polled for.
    const calls = mockFetch(() => reply(recipe("Extracted")));

    const result = await load().addFromUrl("https://www.tiktok.com/@chef/video/1");

    expect(result.status).toBe("Extracted");
    expect(calls).toHaveLength(1);
  });

  it("polls a queued extraction until it settles", async () => {
    let polls = 0;
    mockFetch((call) => {
      if (call.method === "POST") return reply(recipe("Pending"));
      polls += 1;
      return reply(recipe(polls < 2 ? "Processing" : "Extracted"));
    });

    const api = load();
    const pending = api.addFromUrl("https://www.tiktok.com/@chef/video/1");

    await jest.advanceTimersByTimeAsync(2000);
    await jest.advanceTimersByTimeAsync(2000);

    await expect(pending).resolves.toMatchObject({ status: "Extracted" });
  });

  it("reports each status change while it waits", async () => {
    let polls = 0;
    mockFetch((call) => {
      if (call.method === "POST") return reply(recipe("Pending"));
      polls += 1;
      return reply(recipe(polls < 2 ? "Processing" : "Extracted"));
    });

    const seen: string[] = [];
    const api = load();
    const pending = api.addFromUrl("https://www.tiktok.com/@chef/video/1", (r) => seen.push(r.status));

    await jest.advanceTimersByTimeAsync(2000);
    await jest.advanceTimersByTimeAsync(2000);
    await pending;

    expect(seen).toEqual(["Pending", "Processing", "Extracted"]);
  });

  it("stops waiting on a video that never finishes", async () => {
    // Without a deadline a stuck job leaves the screen spinning forever.
    mockFetch(() => reply(recipe("Processing")));

    const api = load();
    const pending = api.addFromUrl("https://www.tiktok.com/@chef/video/1");

    await jest.advanceTimersByTimeAsync(4 * 60 * 1000);

    await expect(pending).resolves.toMatchObject({ status: "Processing" });
  });

  it("gives up on a failed extraction rather than polling it", async () => {
    const calls = mockFetch(() => reply(recipe("NotARecipe")));

    const result = await load().addFromUrl("https://www.tiktok.com/@chef/video/1");

    expect(result.status).toBe("NotARecipe");
    expect(calls).toHaveLength(1);
  });
});
