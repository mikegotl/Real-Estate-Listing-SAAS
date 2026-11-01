## Prompt 11: Article and product content

**The repository already has a public article system** (`/articles`, PR #110), so the article was added to it instead of a new `/learn/...` page. It gets:
- the existing canonical URL, Open Graph tags and `article:published_time`
- Article JSON-LD, the publication date, byline and read time
- a place on the Articles page shelves and in the sitemap

**Article.**
- URL: `/articles/one-listing-four-tiktoks`. Component: `Components/Articles/Content/OneListingFourTikToks.razor`.
- Title: "One Listing Four TikToks". Meta and Open Graph description, verbatim from the handoff: "Learn how Listing Studio analyzes verified property facts and media to recommend purpose-built vertical videos for reach engagement and buyer interest." The handoff's wording says "Listing Studio"; the body uses the site's full name, Creative Listing Studio.
- Sections:
  - why one generic walkthrough isn't enough
  - how the recommendation engine chooses formats
  - the four first-release formats: a table of purpose, opening and required inputs, taken from `VerticalFormatRules`, plus the optional Exclusive access format
  - required inputs and fallbacks (falls back to the single shared vertical video)
  - factual verification (no inferred renovation, exclusivity, neighborhood, affordability, price-change or seller claims; presenter consent)
  - review before publishing
  - the metrics to watch (reach versus business results, matching Prompt 10)
- **Like-rate examples are labelled directional research**, with a note that they are not a forecast or guarantee. The examples are the 12% to 23% likes per view for price-hook walkthroughs and the 3.1% typical engagement, with sources (The Negotiator, Estate Agent Today, Dash Social).
- **Wording.** It uses "recommended for this listing", "designed for" and "performance-informed" language. The page is tested to contain none of "guaranteed viral", "guaranteed views", "best performing" or "guaranteed results". One source title, "…go viral on TikTok?", is a third-party headline.
- **Dates.** Published 2026-11-01. The article system shows "Updated" (and `dateModified`) when an editor sets `UpdatedOn`; there is no separate "reviewed" field.

**Gating.** `ArticleSummary.FeatureKey` marks a feature article. `ArticleVisibility` asks the central `IFeatureStateService`:
- **Public visitors and the sitemap** see the article only when VerticalVideoLibraryV1 is globally on and on for every workspace.
- **A signed-in member** also sees it when the flag is on for their workspace (the pilot list).
- **Global off** hides it from everyone.
- **When hidden:**
  - `/articles/one-listing-four-tiktoks` returns the standard 404 page with none of the article's metadata or JSON-LD
  - the Articles page and sitemap leave it out
  - every link to it is gone (tested off, pilot-only, on for everyone, and global off over everyone)

**Product headline.** Home page hero, only while the flag applies to the visitor:
- Headline "Turn one listing into the vertical videos most likely to earn attention."
- Supporting copy "Creative Listing Studio analyzes your property's strongest visuals, verified facts and approved stories, then recommends ready-to-post creative angles."
- A "How it works: One Listing Four TikToks" link.
- With the flag off, the previous headline and copy are unchanged.

**Every product string replaced (only while the flag applies):**
| Where | Before | With the flag on |
|---|---|---|
| Home hero headline | "Every listing deserves a standout story." | "Turn one listing into the vertical videos most likely to earn attention." |
| Home hero subtitle | "Organize property details and media, develop grounded marketing narratives, and prepare campaign assets from one polished workspace." | "Creative Listing Studio analyzes your property's strongest visuals, verified facts and approved stories, then recommends ready-to-post creative angles." |
| Article "Creative social media campaigns…", feature list | "One vertical video for every feed. A beat-cut vertical edit…" | "The vertical videos this listing supports best. Creative Listing Studio recommends purpose-built vertical videos from your verified facts and media, each with its own hook…" plus a "See how it works" link |

The Campaign tab strings ("Vertical videos for TikTok, Reels and Shorts" and the per-option detail) were already switched by the flag in Prompt 7, and are unchanged here.

**Every page that links to the article** (all through `/go/article/one-listing-four-tiktoks?from=<placement>`):
| Placement | Where | Shown when |
|---|---|---|
| `campaign-selector` | Campaign tab, "Choose your deliverables" | flag on for the workspace |
| `recommendations` | Campaign tab, "2. Videos this listing can support" | the planner only renders with the flag on |
| `library-gallery` | Campaign tab, "Vertical video library" results (the variant review gallery) | flag on now (the Campaign tab passes it) |
| `admin-creative` | Admin TikTok Creative Testing Studio, Creative format testing step 1 | the tab only renders with the flag on |
| `help-chat` | Ask AI window, "Guides" (no separate help search exists; the Ask AI window is the in-product help entry point) | flag on for the workspace |
| `article-cta` | Home hero link, the other article's feature bullet, and the article's own "Start creating" button (to registration) | flag applies to the visitor |

**Click tracking.**
- `GET /go/article/{slug}?from=<placement>[&to=<target>]` records one row in the existing `OperationalEvents` table: kind `article-link`, the article slug, and the placement.
- It stores no property, campaign, user, address or IP. Unknown placements are stored as `other`.
- It then redirects to the article, or to an allowed target. The only one is `/Account/Register`; anything else goes to the article, so it can't be used as an open redirect.
- A hidden or unknown article redirects to `/articles` and records nothing.

### Flag, schema, queue and rollback
- **Flag off:** the previous public and in-product copy, no feature article anywhere, no guide links, and the click endpoint counts nothing.
- **Schema:** none. Clicks use the existing `OperationalEvents` table.
- **Queued work:** none.
- **Rollback:** switch the flag off. Reverting removes the article component and links; the click rows are ordinary operational events.

### Files
- Application: `Articles/ArticleShelving.cs` (`FeatureKey`)
- Web:
  - `Components/Articles/`:
    - `OneListingFourTikToks.razor`
    - `ArticleLibrary.cs`
    - `ArticleVisibility.cs`
    - `ArticleLinks.cs`
    - `ArticleEndpoints.cs`
    - `ArticleGuideLink.razor` (+ css)
    - `SocialCampaignsForAgents.razor`
  - `Pages/`: `Articles.razor`, `ArticleDetails.razor`, `Home.razor`
  - `Seo/SeoEndpoints.cs`
  - `Layout/HelpChatWindow.razor`
  - `Properties/`: `CampaignGenerationManager.razor`, `VerticalCampaignPlanner.razor`, `CampaignVideoLibraryResults.razor`
  - `Administration/AdminCreativeTestingTab.razor`
  - `_Imports.razor`, `Program.cs`, `wwwroot/app.css`
- Tests:
  - `IntegrationTests/VerticalLibraryArticleTests.cs`
  - `UnitTests/CampaignVideoLibraryResultsTests.cs`, `VerticalCampaignPlannerTests.cs` and `AdminCreativeTestingTabTests.cs` (extended)

### Commands run
- `dotnet build ListingStudio.slnx`: 0 warnings.
- New integration test `TheArticleHeadlineAndLinksFollowTheSwitch` (real HTTP against the app):
  - **Off:** the article is a 404 with none of its metadata. The Articles page, sitemap and home page carry nothing of it, the old headline shows, and the click endpoint redirects to `/articles` with no row.
  - **Pilot only:** still hidden publicly and from the sitemap. Visibility is true for a pilot member and false for a member of another workspace or an anonymous visitor.
  - **On for everyone**, the article page has:
    - the title, the verbatim meta description, canonical URL, Open Graph tags and JSON-LD Article with its headline and date
    - the directional-research note and every required section
    - no banned phrases, a single h1 and table headers
  - **On for everyone**, elsewhere:
    - the Articles page and sitemap list the article
    - the new headline replaces the old one
    - the other article's bullet changes
  - **Clicks:**
    - tracked links redirect correctly: the article, registration, and an external target refused
    - three rows are recorded with slug and placement only
  - **Global off over everyone-on:** hidden again.
- bUnit: the guide link appears with its tracked placement in the recommendations, the review gallery (only when `ShowGuideLink`) and the admin studio.
- Full run `RUN_FFMPEG_E2E=1 dotnet test ListingStudio.slnx`: 605 unit and 241 integration tests passed.
- Screenshots (desktop and phone): project files `reviews/2026-11-01-vertical-prompt-11/`. They are the real server-rendered pages from the test app, styled with the app's CSS.

### Remaining risks
- The public article and headline switch only when the flag is on for everyone. During a pilot, only signed-in pilot members see the new copy, by design.
- The four-format table on a phone is narrow (four columns of prose); it is readable but long.
- Click counts aren't shown on the admin dashboard yet. They are in `OperationalEvents` (kind `article-link`) for querying.
