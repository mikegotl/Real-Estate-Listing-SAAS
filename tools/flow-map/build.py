"""Builds the user-flow map page and a CSV of its test cases from cases.py.

python3 tools/flow-map/build.py            rebuilds the admin Flow map page (src/ListingStudio.Web/AdminFlowMap)
python3 tools/flow-map/build.py OUT --artifact   full page that loads mermaid from jsDelivr
"""
import csv
import html
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from cases import SECTIONS, FINDINGS, STAMP  # noqa: E402

args = [a for a in sys.argv[1:] if not a.startswith("--")]

# --app (the default with no OUT): full page for the admin Flow map page, using the mermaid copy bundled in wwwroot/lib/mermaid.
# --artifact: full page for a claude.ai artifact, loading the same mermaid version from jsDelivr.
MERMAID_SRC = ("/lib/mermaid/mermaid.min.js" if "--app" in sys.argv or not args
               else "https://cdn.jsdelivr.net/npm/mermaid@10.9.1/dist/mermaid.min.js" if "--artifact" in sys.argv
               else None)
out = Path(args[0]) if args else Path(__file__).resolve().parents[2] / "src" / "ListingStudio.Web" / "AdminFlowMap"
out.mkdir(parents=True, exist_ok=True)

GROUPS = [
    ("Overview", ["OV"]),
    ("Media", ["MT", "MP", "XP", "PF", "WC", "DI", "RN", "VD"]),
    ("Other tabs", ["IN"]),
    ("Campaign", ["CG", "SP", "VL", "KP", "CT", "CS", "CA", "CX", "CR", "CD", "VC"]),
]

JOURNEY = r'''
flowchart LR
  OV["Overview · OV"] --> MT["Tour Map photos and stops · MT"]
  MT --> MP["Route map · MP"]
  MT --> PF["Photo fixes · PF"]
  MT --> XP["Not in tour · XP"]
  MT --> DI["Drone fly-in · DI"]
  MP --> WC["Walk-through clips · WC"]
  MP --> RN["Room narration · RN"]
  VD["Listing videos · VD"] -->|Find room photos| XP
  XP -->|Add to stop| MT
  IN["Story, Neighborhood, Profile · IN"] --> CG
  MT --> CG["Campaign setup · CG SP CT CS CA CX"]
  CG --> VL["Vertical video library · VL"]
  CG --> KP["Kling 4 pilots · KP"]
  VL --> CR
  KP --> CR
  WC --> CG
  DI --> CG
  RN --> CG
  VD --> CG
  CG --> CR["Generate and run · CR"]
  CR --> CD["Deliverables · CD"]
  CD --> VC["Video chat · VC"]
'''

INIT = ("%%{init: {'theme':'base','flowchart':{'htmlLabels':false,'curve':'basis','wrappingWidth':220},"
        "'themeVariables':{'fontFamily':'system-ui, -apple-system, Segoe UI, Roboto, sans-serif','fontSize':'14px',"
        "'primaryColor':'#ffffff','primaryBorderColor':'#1f3a5c','primaryTextColor':'#14243a',"
        "'lineColor':'#6b7a8f','secondaryColor':'#f6efe2','tertiaryColor':'#f6efe2',"
        "'edgeLabelBackground':'#fbf8f2','clusterBkg':'#fbf8f2'}}}%%\n")


def esc(text):
    return html.escape(text, quote=True)


def needs_chips(needs):
    if not needs:
        return '<span class="free">Local run</span>'
    return "".join(f'<span class="need">{esc(n.strip())}</span>' for n in needs.split(","))


rows = []
for sec in SECTIONS:
    for case in sec["cases"]:
        rows.append(dict(zip(["id", "where", "action", "expected", "priority", "needs", "matrix"], case), area=sec["title"], key=sec["key"]))

# CSV for spreadsheets and test-script tools
with open(out / "user-flow-test-cases.csv", "w", newline="", encoding="utf-8") as handle:
    writer = csv.writer(handle)
    writer.writerow(["ID", "Area", "Starting state", "Steps", "Expected result", "Priority", "Needs", "Matrix ref"])
    for row in rows:
        writer.writerow([row["id"], row["area"], row["where"], row["action"], row["expected"], row["priority"], row["needs"] or "Local run", row["matrix"]])

counts = {p: sum(1 for r in rows if r["priority"] == p) for p in ("P1", "P2", "P3")}
paid = sum(1 for r in rows if r["needs"])

nav = []
for group, keys in GROUPS:
    links = "".join(
        f'<a href="#{k}" class="navchip"><b>{k}</b> {esc(next(s["title"] for s in SECTIONS if s["key"] == k).split(": ")[-1])}</a>'
        for k in keys)
    nav.append(f'<div class="navgroup"><span class="navlabel">{group}</span><div class="navchips">{links}</div></div>')

sections_html = []
for sec in SECTIONS:
    body = []
    for c in sec["cases"]:
        cid, where, action, expected, prio, needs, matrix = c
        search = " ".join([cid, where, action, expected, needs, matrix, sec["title"]]).lower()
        mref = f'<span class="mref">{esc(matrix)}</span>' if matrix else ""
        body.append(
            f'<tr data-p="{prio}" data-paid="{1 if needs else 0}" data-s="{esc(search)}">'
            f'<td class="cid"><span class="idtag">{cid}</span><span class="prio {prio.lower()}">{prio}</span></td>'
            f'<td>{esc(where)}</td><td>{esc(action)}</td><td>{esc(expected)}</td>'
            f'<td class="meta">{needs_chips(needs)}{mref}</td></tr>')
    sections_html.append(f'''
<section class="area" id="{sec["key"]}" data-key="{sec["key"]}">
  <header class="area-head">
    <span class="prefix">{sec["key"]}</span>
    <div><h2>{esc(sec["title"])}</h2><p>{esc(sec["blurb"])}</p></div>
    <span class="area-count">{len(sec["cases"])} cases</span>
  </header>
  <div class="diagram"><pre class="mermaid">{esc(INIT + sec["diagram"])}</pre></div>
  <div class="tablewrap"><table>
    <thead><tr><th>ID</th><th>Starting state</th><th>Steps</th><th>Expected result</th><th>Needs · matrix</th></tr></thead>
    <tbody>{"".join(body)}</tbody>
  </table></div>
  <p class="to-top"><a href="#top">↑ Back to top</a></p>
</section>''')

findings_html = "".join(
    f'<li><strong>{esc(t)}</strong><span>{esc(d)}</span></li>' for t, d in FINDINGS)

page = f'''<title>Creative Listing Studio Flow Map</title>
<meta name="claude:viewport-fit-sides" content="cover">
<link rel="preconnect" href="https://fonts.googleapis.com">
<link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=Fraunces:opsz,wght@9..144,500;9..144,650&family=Inter:wght@400;500;600&family=JetBrains+Mono:wght@500&display=swap">
<style>
/* Layout: a test planner's binder. Summary and findings first, then one tab per area: diagram on drafting paper, then its case table. */
:root {{
  --bg: #fbf8f2; --surface: #ffffff; --ink: #14243a; --muted: #5a6779; --line: #e4ddd0;
  --navy: #1f3a5c; --brass: #a8792a; --brass-soft: #f3e7cf; --paper: #fbf8f2;
  --p1: #b4412f; --p2: #a8792a; --p3: #5f7a65; --chip: #eef1f5;
  --display: "Fraunces", Georgia, serif; --body: "Inter", system-ui, -apple-system, sans-serif;
  --mono: "JetBrains Mono", ui-monospace, Menlo, monospace;
}}
@media (prefers-color-scheme: dark) {{ :root:not([data-theme="light"]) {{
  --bg: #0f1724; --surface: #16202f; --ink: #e8ecf2; --muted: #9eabbd; --line: #2a3648;
  --navy: #9fbbe0; --brass: #d9a94f; --brass-soft: #3a2f1a; --paper: #f4efe4;
  --p1: #f08a76; --p2: #e2b45e; --p3: #9cc2a3; --chip: #223045; color-scheme: dark; }} }}
:root[data-theme="dark"] {{
  --bg: #0f1724; --surface: #16202f; --ink: #e8ecf2; --muted: #9eabbd; --line: #2a3648;
  --navy: #9fbbe0; --brass: #d9a94f; --brass-soft: #3a2f1a; --paper: #f4efe4;
  --p1: #f08a76; --p2: #e2b45e; --p3: #9cc2a3; --chip: #223045; color-scheme: dark; }}
* {{ box-sizing: border-box; }}
body {{ background: var(--bg); color: var(--ink); font-family: var(--body); font-size: 15px; line-height: 1.5; }}
.wrap {{ max-width: 1240px; margin: 0 auto; padding-block: 28px 64px;
  padding-left: max(16px, env(safe-area-inset-left, 0px)); padding-right: max(16px, env(safe-area-inset-right, 0px)); }}
h1, h2, h3 {{ font-family: var(--display); font-weight: 650; text-wrap: balance; margin: 0; }}
h1 {{ font-size: clamp(28px, 4vw, 40px); letter-spacing: -0.01em; }}
.lede {{ color: var(--muted); max-width: 70ch; margin: 8px 0 0; }}
.stamp {{ font-family: var(--mono); font-size: 12px; color: var(--brass); letter-spacing: .06em; text-transform: uppercase; }}
.top {{ display: grid; gap: 20px; align-items: start; grid-template-columns: minmax(0,1.3fr) minmax(0,1fr); margin-top: 22px; }}
@media (max-width: 860px) {{ .top {{ grid-template-columns: 1fr; }} }}
.panel {{ background: var(--surface); border: 1px solid var(--line); border-radius: 12px; padding: 18px 20px; min-width: 0; }}
.panel h3 {{ font-size: 18px; margin-bottom: 10px; }}
.howto {{ display: grid; gap: 8px; margin: 0; padding: 0; list-style: none; }}
.howto li {{ display: grid; grid-template-columns: 92px 1fr; gap: 10px; align-items: baseline; }}
.howto b {{ font-family: var(--mono); font-size: 12px; color: var(--navy); }}
.stats {{ display: flex; gap: 18px; flex-wrap: wrap; margin-top: 14px; font-variant-numeric: tabular-nums; }}
.stats div {{ display: grid; }}
.stats strong {{ font-family: var(--display); font-size: 26px; }}
.stats span {{ font-size: 12px; color: var(--muted); }}
.findings {{ list-style: none; margin: 0; padding: 0; display: grid; gap: 10px; }}
.findings li {{ display: grid; gap: 2px; padding-left: 12px; border-left: 3px solid var(--brass); }}
.findings span {{ color: var(--muted); font-size: 14px; }}
.journey {{ margin-top: 20px; }}
.diagram {{ background: var(--paper); border: 1px solid var(--line); border-radius: 10px; padding: 14px; overflow-x: auto;
  background-image: linear-gradient(rgba(31,58,92,.05) 1px, transparent 1px), linear-gradient(90deg, rgba(31,58,92,.05) 1px, transparent 1px);
  background-size: 22px 22px; }}
.diagram pre {{ margin: 0; color: #14243a; font-family: var(--body); min-width: 560px; text-align: center; }}
.diagram svg {{ max-width: 100%; height: auto; }}
.diagram svg text, .diagram svg tspan {{ font-family: system-ui, -apple-system, "Segoe UI", Roboto, sans-serif; }}
nav.areas {{ display: grid; gap: 10px; margin-top: 20px; }}
.navgroup {{ display: grid; grid-template-columns: 96px 1fr; gap: 10px; align-items: start; }}
@media (max-width: 600px) {{ .navgroup {{ grid-template-columns: 1fr; gap: 4px; }} }}
.navlabel {{ font-size: 12px; text-transform: uppercase; letter-spacing: .08em; color: var(--muted); padding-top: 6px; }}
.navchips {{ display: flex; flex-wrap: wrap; gap: 6px; }}
.navchip {{ text-decoration: none; color: var(--ink); background: var(--surface); border: 1px solid var(--line); border-radius: 999px; padding: 4px 11px; font-size: 13px; }}
.navchip b {{ font-family: var(--mono); color: var(--brass); font-weight: 500; margin-right: 3px; }}
.navchip:hover, .navchip:focus-visible {{ border-color: var(--brass); outline: none; }}
.toolbar {{ position: sticky; top: env(safe-area-inset-top, 0px); z-index: 5; background: var(--bg); padding-block: 12px; margin-top: 20px;
  border-bottom: 1px solid var(--line); display: flex; flex-wrap: wrap; gap: 10px; align-items: center; }}
body::before {{ content: ""; position: fixed; inset: 0 0 auto; height: env(safe-area-inset-top, 0px); z-index: 10; background: var(--bg); }}
.toolbar input[type=search] {{ flex: 1 1 240px; min-width: 0; font: inherit; padding: 8px 12px; border-radius: 8px; border: 1px solid var(--line); background: var(--surface); color: var(--ink); }}
.seg {{ display: inline-flex; border: 1px solid var(--line); border-radius: 8px; overflow: hidden; }}
.seg button {{ font: inherit; font-size: 13px; padding: 7px 11px; background: var(--surface); color: var(--ink); border: 0; border-right: 1px solid var(--line); cursor: pointer; }}
.seg button:last-child {{ border-right: 0; }}
.seg button[aria-pressed=true] {{ background: var(--navy); color: var(--bg); }}
.toolbar label {{ font-size: 13px; display: inline-flex; gap: 6px; align-items: center; }}
.btn {{ font: inherit; font-size: 13px; padding: 7px 12px; border-radius: 8px; border: 1px solid var(--brass); background: var(--brass-soft); color: var(--ink); cursor: pointer; }}
button:focus-visible, input:focus-visible {{ outline: 2px solid var(--brass); outline-offset: 2px; }}
.shown {{ font-size: 13px; color: var(--muted); font-variant-numeric: tabular-nums; }}
.area {{ margin-top: 40px; scroll-margin-top: 80px; }}
.area-head {{ display: grid; grid-template-columns: auto minmax(0,1fr) auto; gap: 14px; align-items: start; margin-bottom: 14px; }}
.area-head h2 {{ font-size: 23px; }}
.to-top {{ margin: 10px 0 0; text-align: right; font-size: 14px; }}
.to-top a {{ color: var(--navy); font-weight: 600; text-decoration: none; }}
.to-top a:hover, .to-top a:focus-visible {{ text-decoration: underline; }}
.area-head p {{ margin: 4px 0 0; color: var(--muted); max-width: 75ch; }}
.prefix {{ font-family: var(--mono); font-size: 15px; background: var(--navy); color: var(--bg); border-radius: 6px; padding: 5px 8px; }}
.area-count {{ font-size: 12px; color: var(--muted); white-space: nowrap; padding-top: 6px; }}
.tablewrap {{ overflow-x: auto; margin-top: 14px; border: 1px solid var(--line); border-radius: 10px; background: var(--surface); }}
table {{ border-collapse: collapse; width: 100%; min-width: 860px; font-size: 14px; }}
th {{ text-align: left; font-size: 11px; text-transform: uppercase; letter-spacing: .07em; color: var(--muted); font-weight: 600; padding: 10px 12px; border-bottom: 1px solid var(--line); }}
td {{ padding: 10px 12px; vertical-align: top; border-bottom: 1px solid var(--line); }}
tbody tr:last-child td {{ border-bottom: 0; }}
th:nth-child(1) {{ width: 92px; }} th:nth-child(2) {{ width: 19%; }} th:nth-child(3) {{ width: 23%; }} th:nth-child(5) {{ width: 128px; }}
.cid {{ white-space: nowrap; }}
.idtag {{ display: block; font-family: var(--mono); font-size: 13px; color: var(--navy); font-weight: 500; }}
.prio {{ display: inline-block; margin-top: 4px; font-size: 11px; font-weight: 600; padding: 1px 7px; border-radius: 999px; border: 1px solid currentColor; }}
.prio.p1 {{ color: var(--p1); }} .prio.p2 {{ color: var(--p2); }} .prio.p3 {{ color: var(--p3); }}
.meta {{ display: flex; flex-wrap: wrap; gap: 4px; align-content: start; }}
.need, .free, .mref {{ font-size: 11px; border-radius: 5px; padding: 1px 6px; }}
.need {{ background: var(--brass-soft); color: var(--ink); }}
.free {{ background: var(--chip); color: var(--muted); }}
.mref {{ font-family: var(--mono); color: var(--navy); border: 1px dashed var(--line); }}
tr.hide {{ display: none; }}
.area.empty {{ display: none; }}
footer {{ margin-top: 48px; color: var(--muted); font-size: 13px; }}
@media (prefers-reduced-motion: no-preference) {{ html {{ scroll-behavior: smooth; }} }}
</style>

<div class="wrap" id="top">
  <span class="stamp">{esc(STAMP)}</span>
  <h1>Creative Listing Studio flow map</h1>
  <p class="lede">Every path a person can take on a property, from the Overview tab through Media to a finished campaign and its video chat. Each branch has an ID you can use as a test case name. Diagrams show the decisions; the table under each one lists the cases with starting state, steps and expected result.</p>

  <div class="top">
    <div class="panel">
      <h3>How to read the IDs</h3>
      <ul class="howto">
        <li><b>MT-31</b><span>Area prefix and case number. Numbers jump by area sub-topic (MT-2x are stops), so new cases can slot in.</span></li>
        <li><b>P1 P2 P3</b><span>P1 = core path or data-loss risk, run every release. P2 = important branch. P3 = edge case or copy check.</span></li>
        <li><b>Needs</b><span>Paid services or settings the case depends on (OpenAI, Higgsfield, Drone AI, Photo edit AI, ElevenLabs, Lip-sync, MediaAnalysis, Google Places, Stripe). "Local run" works without them.</span></li>
        <li><b>S-xx</b><span>Matching scenario in the campaign test matrix spreadsheet (reviews/2026-10-26-campaign-test-matrix, updated 29 Oct). "Pairwise", "Language matrix" and "Deliverable combos" point to its other sheets.</span></li>
      </ul>
      <div class="stats">
        <div><strong>{len(rows)}</strong><span>test cases</span></div>
        <div><strong>{len(SECTIONS)}</strong><span>areas</span></div>
        <div><strong>{counts["P1"]}</strong><span>P1</span></div>
        <div><strong>{counts["P2"]}</strong><span>P2</span></div>
        <div><strong>{counts["P3"]}</strong><span>P3</span></div>
        <div><strong>{len(rows) - paid}</strong><span>run without paid services</span></div>
      </div>
    </div>
    <div class="panel">
      <h3>Found while mapping</h3>
      <ul class="findings">{findings_html}</ul>
    </div>
  </div>

  <div class="journey">
    <div class="diagram"><pre class="mermaid">{esc(INIT + JOURNEY)}</pre></div>
  </div>

  <nav class="areas" aria-label="Areas">{"".join(nav)}</nav>

  <div class="toolbar" role="search">
    <input type="search" id="q" placeholder="Search cases, e.g. drone, consent, S-23" aria-label="Search test cases">
    <div class="seg" role="group" aria-label="Priority">
      <button type="button" data-p="all" aria-pressed="true">All</button>
      <button type="button" data-p="P1" aria-pressed="false">P1</button>
      <button type="button" data-p="P2" aria-pressed="false">P2</button>
      <button type="button" data-p="P3" aria-pressed="false">P3</button>
    </div>
    <label for="free"><input type="checkbox" id="free"> Local run only</label>
    <button type="button" class="btn" id="copy">Copy shown cases</button>
    <span class="shown" id="shown" aria-live="polite"></span>
  </div>

  {"".join(sections_html)}

  <footer>Read from the code (Campaign tab, Media tab components and their services), plus the 23 Oct campaign feature chart and the campaign test matrix (updated 29 Oct). Expected results describe what the code does; nothing here was run against live OpenAI, Higgsfield or ElevenLabs. The same cases are in user-flow-test-cases.csv. Source: tools/flow-map/cases.py in the repository.</footer>
</div>

<script>
(function () {{
  var rows = Array.prototype.slice.call(document.querySelectorAll("tbody tr"));
  var areas = Array.prototype.slice.call(document.querySelectorAll(".area"));
  var q = document.getElementById("q"), free = document.getElementById("free"), shown = document.getElementById("shown");
  var prio = "all";
  function apply() {{
    var text = q.value.trim().toLowerCase(), n = 0;
    rows.forEach(function (r) {{
      var ok = (prio === "all" || r.dataset.p === prio) && (!free.checked || r.dataset.paid === "0") && (!text || r.dataset.s.indexOf(text) !== -1);
      r.classList.toggle("hide", !ok); if (ok) n++;
    }});
    areas.forEach(function (a) {{ a.classList.toggle("empty", !a.querySelector("tbody tr:not(.hide)")); }});
    shown.textContent = n + " of " + rows.length + " shown";
  }}
  q.addEventListener("input", apply);
  free.addEventListener("change", apply);
  document.querySelectorAll(".seg button").forEach(function (b) {{
    b.addEventListener("click", function () {{
      prio = b.dataset.p;
      document.querySelectorAll(".seg button").forEach(function (x) {{ x.setAttribute("aria-pressed", x === b ? "true" : "false"); }});
      apply();
    }});
  }});
  document.getElementById("copy").addEventListener("click", function () {{
    var lines = ["ID\\tArea\\tStarting state\\tSteps\\tExpected result\\tPriority\\tNeeds\\tMatrix"];
    rows.forEach(function (r) {{
      if (r.classList.contains("hide")) return;
      var c = r.children, area = r.closest(".area").querySelector("h2").textContent;
      var needs = Array.prototype.map.call(c[4].querySelectorAll(".need,.free"), function (x) {{ return x.textContent; }}).join(", ");
      var m = c[4].querySelector(".mref");
      lines.push([c[0].querySelector(".idtag").textContent, area, c[1].textContent, c[2].textContent, c[3].textContent, r.dataset.p, needs, m ? m.textContent : ""].join("\\t"));
    }});
    var out = lines.join("\\n"), btn = this;
    function done(msg) {{ btn.textContent = msg; setTimeout(function () {{ btn.textContent = "Copy shown cases"; }}, 2200); }}
    try {{
      navigator.clipboard.writeText(out).then(function () {{ done("Copied, paste into a sheet"); }}, function () {{ done("Copy blocked, use the CSV file"); }});
    }} catch (e) {{ done("Copy blocked, use the CSV file"); }}
  }});
  apply();
}})();
</script>
'''

if MERMAID_SRC and MERMAID_SRC.startswith("/"):
    # Inside the app, use its own font files instead of Google Fonts.
    google = page[page.index('<link rel="preconnect"'):page.index("\n", page.index('family=Fraunces'))]
    page = page.replace(google, '<style>'
        '@font-face {{ font-family: "Inter"; font-weight: 100 900; font-display: swap; src: url("/fonts/inter-latin-wght-normal.woff2") format("woff2"); }}'
        '@font-face {{ font-family: "Fraunces"; font-weight: 100 900; font-display: swap; src: url("/fonts/fraunces-latin-wght-normal.woff2") format("woff2"); }}'
        '</style>'.replace("{{", "{").replace("}}", "}"))

if MERMAID_SRC:
    # Mermaid 10.9.1: 11.x drops the last characters of single-line labels with htmlLabels off.
    page = ('<!DOCTYPE html>\n<html lang="en"><head><meta charset="utf-8">'
            '<meta name="viewport" content="width=device-width, initial-scale=1">'
            '<meta name="robots" content="noindex">\n' + page +
            '\n<script src="' + MERMAID_SRC + '"></script>'
            '\n<script>mermaid.initialize({ startOnLoad: false, securityLevel: "strict" });'
            '\n// Measure labels only after the web fonts load, or the boxes are sized for the fallback font and clip.'
            '\ndocument.fonts.ready.then(function () { mermaid.run(); });</script>\n</html>\n')

(out / "user-flow-map.html").write_text(page, encoding="utf-8")
print(len(rows), counts, paid)
