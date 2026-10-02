/**
 * Helpers for driving the API layer against a fake network.
 */

/** A response shaped like the parts of `fetch`'s that the app actually reads. */
export function reply(
  body: unknown,
  init: { status?: number; headers?: Record<string, string> } = {},
) {
  const status = init.status ?? 200;

  return {
    ok: status >= 200 && status < 300,
    status,
    statusText: `status ${status}`,
    headers: { get: (name: string) => init.headers?.[name] ?? null },
    json: async () => body,
    text: async () => (typeof body === "string" ? body : JSON.stringify(body)),
  };
}

export interface Call {
  url: string;
  method: string;
  body: unknown;
  authorization: string | null;
}

/**
 * Installs a fetch that answers from `handler` and records what was asked.
 *
 * Returning an Error from the handler makes the call reject, which is how a dropped
 * connection is simulated.
 */
export function mockFetch(handler: (call: Call) => unknown) {
  const calls: Call[] = [];

  const fetch = jest.fn(async (url: string, init: RequestInit = {}) => {
    const headers = (init.headers ?? {}) as Record<string, string>;

    const call: Call = {
      url,
      method: init.method ?? "GET",
      body: init.body ? JSON.parse(init.body as string) : undefined,
      authorization: headers.Authorization ?? null,
    };

    calls.push(call);

    const result = handler(call);
    if (result instanceof Error) throw result;
    return result;
  });

  (globalThis as Record<string, unknown>).fetch = fetch;
  return calls;
}

/** The token payload the server returns from sign-in and refresh. */
export function tokens(access: string, refresh: string) {
  return {
    accessToken: access,
    expiresIn: 3600,
    refreshToken: refresh,
    user: { id: "user-1", email: "a@example.com", displayName: "Rafael" },
  };
}

export const keychain = () => (globalThis as Record<string, unknown>).keychain as Map<string, string>;

/**
 * A fresh copy of the api module.
 *
 * It holds the session in module state, so a test that does not reset the registry first
 * inherits whatever the previous one left behind.
 */
export const load = () => require("../lib/api") as typeof import("../lib/api");

/**
 * Resets the module registry between tests.
 *
 * Only for suites that need a clean api module. It is deliberately not global: resetting
 * the registry hands out a second copy of React to anything required afterwards, which
 * breaks rendering for any test whose imports are at the top of the file.
 */
export function isolateModules() {
  beforeEach(() => jest.resetModules());
}
