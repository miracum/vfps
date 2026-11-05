---
target: "the current admin UI (localhost:8080/ui)"
total_score: 25
max_score: 40
na_heuristics:
p0_count: 0
p1_count: 4
target_identity: "file:/home/gulch/proj/github/miracum/vfps/src/Vfps/Components"
timestamp: 2026-11-04T14-52-59Z
slug: src-vfps-components
---
Method: dual-agent (A: design-review sub-agent · B: detector and browser-evidence sub-agent)

# Critique: vfps admin UI (src/Vfps/Components, live at localhost:8080/ui)

Run notes: logged in as admin through Keycloak; headless Playwright at 1440/1280/390px, light and dark, plus German. Database almost empty (one namespace `development`, 0 pseudonyms), so populated-state judgments come from source. B's report reached the parent before A finished; A stayed isolated. Geist is declared but never loaded, so screenshots render in DejaVu Sans (Linux fallback); measured sizes don't depend on the font.

## Design Health Score
| # | Heuristic | Score | Key issue |
|---|---|---|---|
| 1 | Visibility of System Status | 3 | Good skeletons and job progress; revoke/delete token and delete grant give no feedback beyond the list reloading |
| 2 | Match System / Real World | 3 | Precise domain terms; empty namespace says "No results found"; single-namespace picker labelled "Namespaces"; German mixes "Namespace" and "Namensraum" |
| 3 | User Control and Freedom | 2 | Token revoke/delete and grant delete act on one click, no undo; grant switches save instantly |
| 4 | Consistency and Standards | 2 | Three create patterns; card titles same size as page title; inputs 32px and 40px; mixed nav casing |
| 5 | Error Prevention | 2 | Typed-name delete and live regex check are good; no confirmation for token revoke, grant delete, granting reverse lookup or starting de-pseudonymization |
| 6 | Recognition Rather Than Recall | 3 | Column picker reads the CSV header; Access Control points to "Configure access on the Namespaces page" without a link |
| 7 | Flexibility and Efficiency | 2 | Tab doesn't reach the main nav; no sort/filter/bulk for jobs, tokens, grants |
| 8 | Aesthetic and Minimalist Design | 2 | Oversized card titles, box inside a box, 140-160px cards that only say "No tokens yet." |
| 9 | Error Recovery | 3 | Errors inside their dialogs; CSV job error shows at page top, far from the submit button |
| 10 | Help and Documentation | 3 | Field help, REST/gRPC usage examples, docs link |
| Total | | 25/40 | Acceptable |

## Design Specificity Verdict
Design review: stock component-library look (white rounded 24px-padded cards, preset muted-blue theme, Lucide icon before every page title, corner-icon stat tiles). Identity lives in the copy, which is unusually good. Fixed-at-creation settings look editable; revealing original values (reverse lookup, de-pseudonymization, Reverse-lookup permission) looks like any harmless action; every page is a title over a stack of full-width cards.
Deterministic scan: source scan 0 findings but an undercount (directory walk skips .razor; passed explicitly, 23 files scanned regex-only, element rules couldn't run). Rendered pages: 11 findings on 5 pages: skipped-heading x4 (real; card title renders h3 under h1), line-length x1 (real; CSV column-mappings help ~177 chars/line, no content max-width), nested-cards x1 (borderline; bordered mapping row inside New job card), straggler x1 (advisory), overused-font x4 (mostly false positive; declared Geist never loads).
Visual overlays: injected in a headless browser, so nothing visible in the user's own browser; overlay screenshots in scratchpad/critique/b/shots/*-overlay.png.

## Overall Impression
The "big" feeling is measurable: flat type scale (card titles 24px/600 = page title), generously padded components (40px buttons/inputs, 24px card padding, 144-184px of header and title before the first card, content 1392px wide with no max-width), and 36px small buttons with 12px labels. Buttons feel strangely placed because there's no single rule for where actions go: Create sits top-right on two pages, at the bottom of an always-open form on three, next to an input on one; destructive Delete is the most saturated element on the Namespaces page. Biggest opportunity: one layout rule for actions plus one density setting, then spend the saved attention on permanence and revealing original values.

## What's Working
1. The copy explains consequences, as PRODUCT.md asks (token expiry rationale, edit dialog's regex note, export and de-pseudonymize warnings).
2. Loading and error states are thought through: row-shaped skeleton grid, cancellable search, upload progress separate from job progress, errors inside their dialogs, home attention panel for expiring tokens and failed jobs.
3. Dark mode is a complete set of colors, not ad-hoc overrides; destructive and primary re-tuned.

## Priority Issues
[P1] 1. Flat type scale and oversized controls (the "big" feel). Card titles render 24px/600, line-height 1, same as page title; Namespaces stacks "Namespaces" over "Namespace hierarchy" (Namespaces.razor:430). Default buttons/inputs and nav links 40px; small buttons 36px with 12px labels; card padding 24px; no max-width, inputs up to 663px. Fix: data-bb-density="compact" on <html> (App.razor:3; verified in library CSS, ~12% less spacing); card titles bb:text-base/lg + leading-tight, page title stays text-2xl; drop redundant card titles; main max-width ~80rem, prose ~70ch; one button size per role with 14px labels; load Geist or remove it. Command: /impeccable typeset, then /impeccable layout.
[P1] 2. Buttons placed and weighted against expectations. Three create patterns (top-right button + dialog on Namespaces/Projects; always-open form above the list on Access tokens/Service accounts/CSV jobs; field + button on Pseudonyms). "Create namespace" ~500px from its tree. Namespace Delete filled red in the routine action row (Namespaces.razor:552-559); a red Delete on every project card. Confirmation at page top ~350px from its trigger, pushes the button down. Three filled primaries on Pseudonyms. Inspector labels and values stretched ~630px apart. Fix: one create pattern (top-right Create + dialog, list first); Delete outline/ghost destructive, last and pushed right, or in a More menu; in-place confirmation or alert dialog; one primary per view; "Browse pseudonyms"; fixed label column. Command: /impeccable layout.
[P1] 3. Irreversible and data-revealing actions look routine. No confirmation or undo for token revoke/delete (AccessTokens.razor:398-416, ServiceAccounts.razor:232-250), grant delete (NamespaceAccessGrantsEditor.razor:146-153) or the instantly-saving grant switches. Reverse-lookup switch identical to Read/Write. De-pseudonymization submit still "Upload and process", no review. Namespace delete confirmation omits pseudonym count. Fix: confirmation or undo toast naming token and last use; one warning style for reveal actions (switch, "Reveal" verb, de-pseudonymize review of file/columns/namespaces); count and children in delete confirmation. Command: /impeccable harden, then /impeccable clarify.
[P1] 4. Keyboard and screen-reader access broken in key places. Nav links rendered role="menuitem" tabindex="-1" (MainLayout.razor:18), Tab skips from logo to theme toggle. Native selects focus-visible:outline-none with no ring (e.g. CsvJobs.razor:80, Namespaces.razor:178). h1 jumps to h3 on most pages (detector confirmed on 4). Avatar named "AU"; bare check icons/dashes as values. Fix: plain links or one tabindex="0" item; global select:focus-visible ring in app.css; As="h2" on card titles; skip link; label the avatar. Command: /impeccable audit.
[P2] 5. CSV job form poorly structured and broken at narrow widths. De-pseudonymize warning squeezed into a grid cell (CsvJobs.razor:99-109); Direction options truncated at 437px; Encoding/Delimiter before the file; at 390px mapping inputs shrink to ~56px; missing space "...in place).Pick a CSV file"; German header overflows by 32px at 1280px, "Abmelden" clipped. Fix: Direction and File first, full-width warning row, collapsed "File format" group, mapping grid that stacks, earlier nav collapse. Command: /impeccable adapt.

## Persona Red Flags
Alex: Tab doesn't reach the nav; one click per tree node to see method/length/count; no sort/filter/bulk on jobs, tokens, grants; token-create form above token list; cross-page service-account setup loop.
Sam: nav unreachable; selects no focus ring; h1 to h3 jumps; avatar announced "AU"; namespace delete moves focus to a page-top input without announcing context.
Dr. Brandt (data steward re-identifying a cohort): truncated Direction option; field vanishes and layout jumps on direction change; namespace picker doesn't show reverse-lookup rights, error appears at page top after submit; no review of columns to re-identify; "Download" gives no identifying-data reminder.

## Minor Observations
"Vfps" in footer, logo alt and page title against lowercase brand; mixed nav casing; key-round icon for both Pseudonyms and Access tokens (Home uses fingerprint); "No results found" empty states; Namespaces search 32px vs Pseudonyms 40px; header cluster heights 40/32/36px; "Use a token" sample clipped at 1440px; faint dark-mode tree selection; home stat tiles ~560px tall at 390px; German "Eltern" and Namensraum/Namespace mix.

## Questions to Consider
- Should Namespaces be a dense, sortable table with hierarchy as indentation instead of tree + side panel?
- Should revealing original values get one unmistakable look across reverse lookup, de-pseudonymization, export and the Reverse-lookup permission?
- Per-namespace page with tabs (Pseudonyms, Settings, Access, Projects) and a Danger zone for Delete?
