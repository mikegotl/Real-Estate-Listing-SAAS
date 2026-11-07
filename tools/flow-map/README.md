# Flow map

The flow map lists every path a person can take on a property, from the Overview tab through Media to a finished
campaign, with an ID on each branch to use as a test case name. Platform admins open it at `/admin/flow-map`
(System overview → Flow map), with a CSV of the cases.

- `cases.py` holds the diagrams (mermaid) and test cases. Edit this file.
- `build.py` writes the page and CSV into `src/ListingStudio.Web/AdminFlowMap/`, which the Web project embeds.
  `python3 tools/flow-map/build.py OUT --artifact` writes a stand-alone copy that loads mermaid from jsDelivr.
- `check_changes.py` runs in CI on pull requests. It never fails the build; it warns when a change touches a mapped
  area without updating `cases.py`, or when the generated files don't match `cases.py`.

## Keeping it current

Any pull request that changes behavior on the Overview, Media or Campaign tabs (or the services behind them, billing
gates and release switches) updates the matching cases in the same pull request:

1. Change, add or remove the affected cases and diagram branches in `cases.py`. Keep existing IDs stable; add new
   IDs at the end of an area. Describe what the code does, not what was intended.
2. Update `STAMP` (main commit, date) and `FINDINGS` if something is now fixed or newly found.
3. Run `python3 tools/flow-map/build.py` and commit `cases.py` with the regenerated files.

The mermaid script in `src/ListingStudio.Web/wwwroot/lib/mermaid/` is version 10.9.1 (MIT). Mermaid 11 drops the
last characters of single-line labels with `htmlLabels` off, so don't upgrade it without checking the diagrams.
