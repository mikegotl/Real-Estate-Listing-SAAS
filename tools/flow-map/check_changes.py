"""CI reminder: warns when a pull request changes Overview, Media or Campaign logic but not the flow map.

python3 tools/flow-map/check_changes.py BASE_REF

Never fails the build (the owner chose a reminder, not a gate). It prints GitHub warning annotations and, when
GITHUB_STEP_SUMMARY is set, a short table of the flow map areas the change touches.
"""
import fnmatch
import os
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
WEB = "src/ListingStudio.Web/Components/Properties/"
APP = "src/ListingStudio.Application/"
INFRA = "src/ListingStudio.Infrastructure/"

# Changed path pattern -> flow map area prefixes in cases.py. Keep in step with the sections there.
AREAS = [
    ("src/ListingStudio.Web/Components/Pages/PropertyDetails.razor", "OV"),
    (WEB + "PropertyNextStep.cs", "OV"),
    (WEB + "PropertyListingSourceManager*", "OV"),
    (WEB + "PropertyMediaManager*", "MT MP XP PF"),
    (WEB + "PropertyWalkthroughMap*", "MP WC RN"),
    (WEB + "WalkthroughMapStopShots*", "MT MP"),
    (WEB + "NewTourStopDialog*", "MT XP"),
    (WEB + "PhotoClutterMaskDialog*", "PF XP"),
    (WEB + "PhotoFix*", "PF"),
    (WEB + "PhotoAssistant*", "PF"),
    (WEB + "PropertyAerialIntro*", "DI"),
    (WEB + "PropertyVideoManager*", "VD"),
    (WEB + "PropertyStoryManager*", "IN"),
    (WEB + "PropertyNarrationScriptManager*", "IN"),
    (WEB + "NeighborhoodInsightsManager*", "IN"),
    (WEB + "CampaignGenerationManager*", "CG CT CS CA CX CR CD KP"),
    (WEB + "CampaignSettingsPresetBar*", "SP"),
    (WEB + "VerticalCampaignPlanner*", "VL"),
    (WEB + "VerticalFormatInputs*", "VL"),
    (WEB + "VerticalPerformanceTable*", "VL"),
    (WEB + "CampaignVideoLibraryResults*", "VL KP"),
    (WEB + "CampaignPerformancePanel*", "VL"),
    (WEB + "CaptionStyle*", "CX"),
    (WEB + "KlingRequestDetails*", "KP"),
    (WEB + "CampaignVideoChatPanel*", "VC"),
    ("src/ListingStudio.Web/Components/Layout/HelpChatWindow*", "VC"),
    ("src/ListingStudio.Web/Components/Layout/PlanUnlock*", "DI CA"),
    ("src/ListingStudio.Web/Components/Pages/Profile.razor", "IN CA"),
    ("src/ListingStudio.Web/Components/Pages/Billing.razor", "CR"),
    ("src/ListingStudio.Domain/Administration/FeatureFlag.cs", "VL KP"),
    (APP + "Campaigns/Vertical*", "VL"),
    (APP + "Campaigns/CampaignPreset*", "SP"),
    (INFRA + "Campaigns/CampaignSettingsPreset*", "SP"),
    (APP + "Videos/CampaignCreative*", "CT CS CX"),
    (APP + "Videos/IWalkthroughVideoProvider.cs", "KP"),
    (APP + "Properties/Walkthrough*", "MT MP"),
    (APP + "Billing/*", "CR"),
    (INFRA + "Campaigns/CampaignGeneration*", "CR CD KP"),
    (INFRA + "Billing/*", "CR"),
    (INFRA + "Properties/AerialIntro*", "DI"),
    (INFRA + "Properties/PropertyPhotoFix*", "PF"),
    (INFRA + "Properties/WalkthroughTransition*", "WC"),
]
MAP_FILES = {"tools/flow-map/cases.py"}
GENERATED = ["src/ListingStudio.Web/AdminFlowMap/user-flow-map.html", "src/ListingStudio.Web/AdminFlowMap/user-flow-test-cases.csv"]


def changed_files(base):
    out = subprocess.run(["git", "diff", "--name-only", f"{base}...HEAD"], cwd=ROOT, check=True,
                         capture_output=True, text=True).stdout
    return [line for line in out.splitlines() if line]


def touched_areas(files):
    hits = {}
    for path in files:
        for pattern, areas in AREAS:
            if fnmatch.fnmatch(path, pattern):
                for area in areas.split():
                    hits.setdefault(area, set()).add(path)
    return hits


def generated_is_stale():
    """Rebuilds into a scratch folder and compares with the committed page and CSV."""
    import tempfile
    with tempfile.TemporaryDirectory() as tmp:
        subprocess.run([sys.executable, str(ROOT / "tools/flow-map/build.py"), tmp, "--app"], check=True,
                       capture_output=True)
        return [rel for rel in GENERATED
                if (Path(tmp) / Path(rel).name).read_bytes() != (ROOT / rel).read_bytes()]


def main():
    base = sys.argv[1] if len(sys.argv) > 1 else "origin/main"
    files = changed_files(base)
    hits = touched_areas(files)
    map_updated = any(path in MAP_FILES for path in files)
    lines = []

    if hits and not map_updated:
        areas = ", ".join(sorted(hits))
        print(f"::warning title=Flow map not updated::This change touches flow map areas {areas} but not "
              "tools/flow-map/cases.py. Update the cases for any changed behavior and run "
              "python3 tools/flow-map/build.py (see tools/flow-map/README.md).")
        lines += ["### Flow map reminder", "",
                  "This pull request changes Overview, Media or Campaign code but not `tools/flow-map/cases.py`. "
                  "If behavior changed, update those cases and run `python3 tools/flow-map/build.py`.", "",
                  "| Area | Changed files |", "|---|---|"]
        lines += [f"| {area} | {'<br>'.join(sorted(paths))} |" for area, paths in sorted(hits.items())]

    stale = generated_is_stale()
    if stale:
        print("::warning title=Flow map page out of date::cases.py and the generated admin page differ. "
              "Run python3 tools/flow-map/build.py and commit the result.")
        lines += ["", "The generated flow map files are out of date: " + ", ".join(f"`{s}`" for s in stale)]

    if not lines:
        print("Flow map: nothing to flag." if hits else "Flow map: no mapped areas changed.")
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary and lines:
        with open(summary, "a", encoding="utf-8") as handle:
            handle.write("\n".join(lines) + "\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
