import { api, ApiError, type ImportSummary, type Paginated, type SavedPost } from "@/lib/api";

export const dynamic = "force-dynamic";

/**
 * The bulk-import side: every saved post, and where it sits in the pipeline.
 *
 * This is the view that makes the import legible. "214 recipes found in 786 saves" only
 * reads as the app being smart about a messy library if the other 572 are visible and
 * accounted for — otherwise it reads as the app losing things.
 */
export default async function LibraryPage({
  searchParams,
}: {
  searchParams: Promise<{ import?: string }>;
}) {
  const { import: importId } = await searchParams;

  let imports: Paginated<ImportSummary> | null = null;
  let posts: Paginated<SavedPost> | null = null;
  let error: string | null = null;

  try {
    imports = await api.get<Paginated<ImportSummary>>("/api/import?pageSize=25");
    const chosen = importId ?? imports.items.find((i) => i.importedCount > 0)?.id;

    if (chosen) {
      posts = await api.get<Paginated<SavedPost>>(
        `/api/import/${chosen}/posts?pageSize=100`,
      );
    }
  } catch (e) {
    error = e instanceof ApiError ? e.message : String(e);
  }

  return (
    <>
      <h1>Library</h1>
      <p className="lede">Everything imported, and how far through the pipeline it is.</p>

      {error && <div className="error">{error}</div>}

      {imports && imports.items.length > 0 && (
        <div className="list">
          {imports.items.map((job) => (
            <a key={job.id} className="item" href={`/library?import=${job.id}`}>
              <div className="row wrap">
                <strong className="grow">{job.platform}</strong>
                <span className="small muted mono">
                  {job.importedCount} imported
                  {job.duplicateCount > 0 && ` · ${job.duplicateCount} duplicates`}
                  {job.skippedCount ? ` · ${job.skippedCount} unreadable` : ""}
                </span>
              </div>
              <div className="small muted">{new Date(job.createdAt).toLocaleString()}</div>
            </a>
          ))}
        </div>
      )}

      {posts && (
        <>
          <h2>
            Posts <span className="muted small">({posts.totalCount})</span>
          </h2>
          <p className="small muted">
            Ranked by how confidently each looks like a recipe — the top of this list is
            what a user would see first. A post with no caption is waiting on stage&nbsp;1;
            one marked unavailable is gone from the platform for good.
          </p>

          <div className="card" style={{ marginBottom: "1rem" }}>
            <div className="stats">
              {(["Food", "Uncertain", "NotFood", "Unclassifiable", "Pending"] as const).map(
                (tier) => (
                  <div key={tier}>
                    <b>{posts.items.filter((p) => p.classificationStatus === tier).length}</b>
                    <span className="small muted">{label(tier)}</span>
                  </div>
                ),
              )}
            </div>
            <p className="small muted" style={{ margin: "0.6rem 0 0" }}>
              Counts are for the {posts.items.length} shown, not all {posts.totalCount}.
            </p>
          </div>
          <div className="list">
            {posts.items.map((post) => (
              <div key={post.id} className="item">
                <div className="row wrap">
                  <span className="grow">
                    {post.caption
                      ? post.caption.slice(0, 110)
                      : <span className="muted">no caption yet</span>}
                  </span>
                  <ClassificationTag post={post} />
                  <MetadataTag status={post.metadataStatus} />
                </div>
                <div className="small muted" style={{ marginTop: "0.3rem" }}>
                  {post.creatorHandle ? `@${post.creatorHandle} · ` : ""}
                  <a href={post.url} target="_blank" rel="noreferrer">source</a>
                  {post.savedAt && ` · saved ${new Date(post.savedAt).toLocaleDateString()}`}
                  {post.classifiedBy && ` · judged by ${post.classifiedBy}`}
                </div>
              </div>
            ))}
          </div>
        </>
      )}
    </>
  );
}

function label(tier: string): string {
  return tier === "NotFood"
    ? "skipped"
    : tier === "Unclassifiable"
      ? "unreadable"
      : tier.toLowerCase();
}

/** The ranked-queue tier, with the confidence that put it there. */
function ClassificationTag({ post }: { post: SavedPost }) {
  if (post.classificationStatus === "Pending") {
    return <span className="tag flat">pending</span>;
  }

  const tone =
    post.classificationStatus === "Food"
      ? "good"
      : post.classificationStatus === "Uncertain"
        ? "warn"
        : "flat";

  return (
    <span className={`tag ${tone} mono`}>
      {label(post.classificationStatus)} {post.foodConfidence.toFixed(2)}
    </span>
  );
}

function MetadataTag({ status }: { status: string }) {
  const tone =
    status === "Fetched" || status === "NotNeeded"
      ? "good"
      : status === "Unavailable"
        ? "bad"
        : "flat";
  return <span className={`tag ${tone}`}>{status}</span>;
}
