import { QueryClient, QueryClientProvider, keepPreviousData, useQuery } from "@tanstack/react-query";
import { useCallback, type ReactNode } from "react";
import { api, ApiError } from "./api";

/**
 * Data the portals have read is kept for a few minutes, so going back to a page shows what it had at once and then refreshes
 * quietly (no loading skeleton the second time). Nothing is trusted as fresh: every page that opens asks the API again.
 */
export const queryClient = new QueryClient({
  defaultOptions: {
    queries: { staleTime: 0, gcTime: 5 * 60_000, retry: false, refetchOnWindowFocus: false },
  },
});

export function QueryProvider({ children }: { children: ReactNode }) {
  return <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;
}

/** Forget everything that was read, for example when someone else signs in. */
export function clearCachedData() {
  queryClient.clear();
}

/** Loads data from the API (cached by its address); `reload` fetches it again. */
export function useApi<T>(path: string | null) {
  const q = useQuery<T, Error>({
    queryKey: ["api", path],
    queryFn: () => api<T>(path!),
    enabled: path !== null,
    placeholderData: keepPreviousData,
  });
  const { refetch } = q;
  const reload = useCallback(() => { void refetch(); }, [refetch]);
  const error = q.error ? (q.error instanceof ApiError ? q.error.message : q.error.message || String(q.error)) : null;
  return { data: (q.data ?? null) as T | null, error, loading: q.isFetching, reload };
}

/** Marks everything cached under these address prefixes as old, so open pages read it again. */
export function invalidateApi(prefix: string) {
  return queryClient.invalidateQueries({ queryKey: ["api"], predicate: (query) => typeof query.queryKey[1] === "string" && (query.queryKey[1] as string).startsWith(prefix) });
}

/**
 * Optimistic update: change what is cached for `path` straight away so the screen answers at once, then run the request.
 * If the request fails the cached data goes back to how it was and the error is thrown for the caller to show.
 */
export async function optimisticUpdate<T>(path: string, change: (current: T) => T, request: () => Promise<unknown>): Promise<void> {
  const key = ["api", path];
  await queryClient.cancelQueries({ queryKey: key });
  const before = queryClient.getQueryData<T>(key);
  if (before !== undefined) queryClient.setQueryData<T>(key, change(before));
  try {
    await request();
  } catch (e) {
    if (before !== undefined) queryClient.setQueryData<T>(key, before);
    throw e;
  }
}
