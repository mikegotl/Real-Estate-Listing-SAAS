# Test cases for the Creative Listing Studio user-flow map. Last read from main 58be71a (2026-11-07).
# Any PR that changes one of these flows updates its cases here and rebuilds (see README.md).
# Each tuple: (id, where / precondition, action, expected outcome, priority, needs, matrix ref)
# needs: services or settings the case depends on. "" = works on a plain local run.

STAMP = "Built from main 58be71a · covers PRs up to #150 · PR #151 not merged · 7 Nov 2026"

SECTIONS = []


def section(key, title, blurb, diagram, cases):
    SECTIONS.append(dict(key=key, title=title, blurb=blurb, diagram=diagram.strip("\n"), cases=cases))


# ---------------------------------------------------------------- Overview
section("OV", "Overview tab and property header",
"What every property page shows before any tab is opened: the header, the Next step card, Edit and Archive, and the Zillow link on the Overview tab.",
r'''
flowchart TD
  A["Open /properties/id"] --> B{"Property in your organization?"}
  B -->|no| OV02["OV-02 Not found message"]
  B -->|yes| C{"Archived?"}
  C -->|yes| OV17["OV-17 Archived listing, read-only"]
  C -->|no| N{"Next step card, first rule that matches"}
  N -->|0 Tour Map photos| OV10["OV-10 Add listing photos"]
  N -->|campaign queued or running| G{"Any photo analysis failed?"}
  G -->|yes| OV14["OV-14 Campaign waiting on photo analysis"]
  G -->|no| OV13["OV-13 Campaign in progress"]
  N -->|campaign failed| OV15["OV-15 Campaign needs attention"]
  N -->|campaign completed| OV16["OV-16 Campaign ready"]
  N -->|1 or 2 photos| OV11["OV-11 Add a few more photos"]
  N -->|3 or more photos| OV12["OV-12 Generate your campaign"]
  C -->|no| H["Header and Overview actions"]
  H -->|Edit| OV20["OV-20 Edit form"]
  H -->|Archive| AR{"Confirm?"}
  AR -->|Cancel| OV21["OV-21 Nothing changes"]
  AR -->|Archive| OV22["OV-22 Back to list, read-only"]
  H -->|Zillow link| Z{"URL and address confirmed?"}
  Z -->|missing either| OV30["OV-30 Link button disabled"]
  Z -->|valid Zillow home link| OV31["OV-31 Zillow linked"]
  Z -->|not a Zillow home link| OV32["OV-32 Error message"]
''',
[
("OV-01", "Property you own, with Tour Map photos", "Open the property page.", "Header shows status chip, price, address and the first Tour Map photo as the cover. Fact strip shows beds, baths, square feet, lot, year built and type ('—' when blank). Five tabs: Overview, Media, Neighborhood, Story & script, Campaign.", "P1", "", ""),
("OV-02", "Property ID from another organization, or a made-up ID", "Open /properties/{id}.", "'Property not found or unavailable to your organization.' and a Back to properties button. No data from the other organization appears.", "P1", "", ""),
("OV-03", "No photos placed on the Tour Map", "Open the property.", "Header shows 'Your first listing photo becomes the cover image'. Media tab has no count badge.", "P2", "", ""),
("OV-04", "Photos only in Extra photos (none on the Tour Map)", "Open the property.", "Still no cover and no Media badge: only Tour Map photos count.", "P2", "", ""),
("OV-05", "Property with no description", "Open the Overview tab.", "Shows 'No description yet. Add one from Edit so generated marketing can draw on it.'", "P3", "", ""),
("OV-06", "Any property", "Open /properties/{id}?tab=media, then ?tab=campaign, ?tab=story, ?tab=neighborhood.", "The named tab opens each time. An unknown tab value opens Overview.", "P3", "", ""),
("OV-10", "0 Tour Map photos, not archived", "Read the Next step card, press Add photos.", "Title 'Add listing photos'. Button switches to the Media tab.", "P1", "", ""),
("OV-11", "1 or 2 Tour Map photos, no campaign", "Read the Next step card.", "'Add a few more photos' with the current count and 'At least 3' advice. Button opens Media.", "P2", "", ""),
("OV-12", "3+ Tour Map photos, no campaign yet", "Read the card, press Go to campaign.", "'Generate your campaign'. Button opens the Campaign tab.", "P1", "", ""),
("OV-13", "Campaign queued or running, no failed photo analysis", "Read the card.", "'Campaign in progress', button View progress opens Campaign. Card updates on its own when the campaign finishes.", "P2", "", ""),
("OV-14", "Campaign running and one Tour Map photo with failed analysis", "Read the card.", "'Campaign waiting on photo analysis', button Review photos opens Media.", "P2", "OpenAI", ""),
("OV-15", "Latest campaign failed", "Read the card.", "'Campaign needs attention', button Open campaign.", "P2", "", ""),
("OV-16", "Latest campaign completed", "Read the card.", "'Campaign ready: Preview your videos and still posts, then download them and copy the social captions.' Button View deliverables.", "P3", "", ""),
("OV-17", "Archived property", "Open it from the archived filter.", "Card 'Archived listing' with no button. No Edit or Archive buttons. Media, map, narration, Campaign controls are read-only; Start Again is hidden.", "P1", "", "S-31"),
("OV-20", "Active property", "Press Edit, change price and beds, save.", "Returns to the property; header price and fact strip show the new values.", "P2", "", ""),
("OV-21", "Active property", "Press Archive, then Cancel.", "Dialog closes, nothing changes.", "P2", "", ""),
("OV-22", "Active property", "Press Archive, confirm.", "Goes to /properties; the property is hidden from the default list. Reopening it gives OV-17.", "P1", "", ""),
("OV-30", "No Zillow link yet", "Look at External listing research; type a URL without ticking the address box.", "Info alert explains links are saved only. 'Link Zillow page' stays disabled until both URL and confirmation are present.", "P2", "", ""),
("OV-31", "No Zillow link yet", "Paste a real zillow.com/homedetails/..._zpid/ link, tick the address box, Link.", "'Zillow linked' chip, link card with Zillow property ID and confirmation time. Button becomes 'Replace linked page'; Remove link appears.", "P2", "", ""),
("OV-32", "No Zillow link yet", "Paste a non-Zillow URL (or a Zillow search page) with the box ticked.", "Error alert explaining the link must be a Zillow home details page. Nothing saved.", "P2", "", ""),
("OV-34", "No Zillow link yet", "Paste a valid Zillow home link for a different address, box ticked.", "'The Zillow link does not appear to match this property's street address and ZIP code…' Nothing saved.", "P2", "", ""),
("OV-33", "Zillow linked", "Replace with another valid link; then Remove link.", "Card updates to the new ID; after Remove the empty state returns.", "P3", "", ""),
])

# ---------------------------------------------------------------- Tour map photos
section("MT", "Media: Tour Map photos and stops",
"Building the route: the required Exterior front stop, adding stops, uploading, ordering and deleting photos, photo analysis, and Start Again.",
r'''
flowchart TD
  S{"Exterior front has a photo?"} -->|no| MT01["MT-01 Required Exterior Front card"]
  MT01 --> U["Upload photos"]
  S -->|yes| MAP["Route map and stop panel"]
  MAP --> U
  U --> V{"Files OK?"}
  V -->|JPG PNG WEBP up to 20 MB| MT02["MT-02 Added to the stop"]
  V -->|wrong type or too big| MT04["MT-04 or MT-05 Failed, Retry upload"]
  V -->|would pass 50 photos| MT06["MT-06 Limit message, nothing uploaded"]
  MT02 --> AN{"Photo analysis"}
  AN -->|recognized| MT08["MT-08 Room type and quality badge"]
  AN -->|failed| MT09["MT-09 Retry analysis"]
  MAP --> ADD["Where does the buyer go next?"]
  ADD -->|suggested chip| MT20["MT-20 Pending stop card"]
  ADD -->|custom name| MT21["MT-21 Pending custom stop"]
  ADD -->|name already used| MT22["MT-22 Already in the walkthrough"]
  MT20 -->|add photos| MT26["MT-26 Stop joins the map and opens"]
  MT20 -->|remove or reload| MT24["MT-24 or MT-25 Pending stop gone"]
  MAP --> PH["Photo card"]
  PH -->|arrows| MT28["MT-28 Order saved"]
  PH -->|Move| MT29["MT-29 Moved to stop"]
  PH -->|Create new stop| MT38["MT-38 Room tag offered as the name"]
  PH -->|Move to extra photos| MT30["MT-30 Leaves the tour"]
  PH -->|Delete| MT31["MT-31 Confirm then deleted"]
  PH -->|Fix| PFX["see Photo fixes"]
  MAP -->|Start Again| MT36["MT-35 to MT-37 Media reset"]
''',
[
("MT-01", "Property with no photos", "Open the Media tab.", "Tour Map shows 'Stop 1 · Required, Exterior Front' with 'Upload exterior front'. No route map yet. Counter reads 0 of 50 photos.", "P1", "", ""),
("MT-02", "MT-01 state", "Upload one JPG with Upload exterior front.", "Progress bar, then 'Photo added to Exterior front.' Route map appears with stop 1. Header cover and Media badge update; Next step changes to 'Add a few more photos'.", "P1", "", ""),
("MT-03", "Any stop", "Select 5 photos in one go.", "Each file gets its own progress bar; all land in that stop in selection order.", "P2", "", ""),
("MT-04", "Any stop", "Upload a GIF, HEIC or a .txt renamed to .jpg.", "That file shows an error and 'Retry upload'; alert 'One or more photos could not be uploaded'. Other files still upload.", "P2", "", ""),
("MT-05", "Any stop", "Upload a 25 MB JPG.", "Fails with a size error and Retry upload.", "P2", "", ""),
("MT-06", "48 photos already uploaded", "Select 3 more.", "'These files would exceed the 50-image property limit.' Nothing uploads.", "P2", "", ""),
("MT-07", "Any stop", "Select 51 files at once.", "'Select no more than 50 images at once.'", "P3", "", ""),
("MT-08", "Media analysis on", "Upload a kitchen photo.", "Card reads 'Recognizing room…', then the room type and a quality badge (hover shows score out of 100). Any potential problems show in amber text.", "P1", "OpenAI, MediaAnalysis", ""),
("MT-09", "A photo whose analysis failed", "Press Retry analysis.", "Card showed 'Analysis attempt N failed: reason'. After retry: 'Photo analysis queued for retry.' and 'Recognizing room…'.", "P2", "OpenAI, MediaAnalysis", "S-28"),
("MT-20", "Exterior front has a photo", "Press a suggested stop chip (e.g. Kitchen).", "'Kitchen added as stop N. Add a photo to keep it: empty stops aren't saved.' A 'New stop' card appears with an upload button and the same notice; the chip disappears from suggestions.", "P1", "", ""),
("MT-21", "Any", "Type 'Wine cellar' in Other room or area, press Enter (and separately the Add stop button).", "Pending custom stop card appears, same as MT-20.", "P2", "", ""),
("MT-22", "Kitchen already a stop", "Add 'kitchen' as a custom stop.", "'Kitchen is already in the walkthrough.' No duplicate.", "P3", "", ""),
("MT-23", "Any", "Type a stop name longer than the field allows.", "Field stops accepting characters at the maximum length.", "P3", "", ""),
("MT-24", "A pending custom stop with no photos", "Press its X.", "Card is removed. Template stops (Entry / foyer) have no X.", "P3", "", ""),
("MT-25", "A pending stop with no photos", "Reload the page.", "The pending stop is gone. The card and the 'added' message already warned that empty stops aren't saved until they get a photo.", "P3", "", ""),
("MT-26", "A pending stop", "Upload photos to it.", "Stop moves onto the route map and the map opens that stop's panel.", "P2", "", ""),
("MT-27", "Stop panel open", "Use 'Add another angle'.", "Photo appended to that stop.", "P2", "", ""),
("MT-28", "Stop with 3 photos", "Move the middle photo earlier, then later.", "'Photo order saved.' Scene numbers update. Back arrow disabled on the first photo, forward arrow on the last.", "P2", "", ""),
("MT-29", "Two stops with photos", "Photo card Move → another stop.", "'Photo moved to X.' Photo now in the other stop; both stops' counts update.", "P2", "", ""),
("MT-38", "Photo recognized as Kitchen, no Kitchen stop yet", "Photo card menu → Create new stop….", "Dialog shows 'This photo is tagged:' with a Kitchen chip and the hint 'Tap Kitchen above, or type a name'. Tapping the chip fills the name (still editable); Create stop and add photo makes the stop with this photo in it. A photo with no room tag shows no chips and the old placeholder.", "P2", "MediaAnalysis", ""),
("MT-30", "Photo on the Tour Map", "Move → Move to extra photos.", "'Photo moved to extra photos. It is no longer part of the walkthrough.' Not in tour count goes up; if it was the first photo, the cover changes.", "P1", "", "S-24"),
("MT-31", "Photo on the Tour Map", "Delete, then Cancel; then Delete and confirm.", "Dialog warns walk-through clips made from it are removed too. Cancel keeps it; confirm shows 'Photo deleted.'", "P1", "", ""),
("MT-32", "Exterior front has one photo, other stops have photos", "Delete the Exterior front photo.", "Required Exterior Front card returns; other stops stay; drone intro section disappears.", "P2", "", ""),
("MT-33", "Read-only (archived) property with no photos", "Open Media.", "'No listing photos have been uploaded.' No upload controls.", "P3", "", ""),
("MT-34", "Photos in tour and not in tour", "Use the Tour map / Not in tour / Videos buttons.", "Each scrolls to its section; Not in tour count is highlighted when unsorted photos exist. URL keeps ?tab=media.", "P3", "", ""),
("MT-35", "Media exists", "Start Again, then Cancel.", "Nothing deleted.", "P2", "", ""),
("MT-36", "Photos, videos, clips and a drone intro exist", "Start Again, confirm.", "'Media reset. Removed N photos and M videos.' Tour Map is empty, videos gone. Property details, neighborhood, story and script, and campaign history remain.", "P1", "", ""),
("MT-37", "No media", "Start Again, confirm.", "'There was no media to reset.'", "P3", "", ""),
])

# ---------------------------------------------------------------- Route map
section("MP", "Media: route map (directions and tour order)",
"The schematic map above the photos. Selecting a stop opens its panel with direction, order, photos, clips and narration.",
r'''
flowchart TD
  A["Select a stop on the map or the route list"] --> B{"Stop 1?"}
  B -->|yes| MP02["MP-02 Where the buyer arrives, no direction picker"]
  B -->|no| C{"Direction set?"}
  C -->|no| MP03["MP-03 Dashed line, assumed ahead"]
  C -->|pick one| MP04["MP-04 Solid line, saved message"]
  MP04 -->|Upstairs or Downstairs| MP04b["MP-04 Stair badge, new level panel"]
  MP04 -->|stop had a finished clip| MP05["MP-05 Clip out of date"]
  A --> D{"Movable stop?"}
  D -->|Exterior front or Entry foyer| MP07["MP-07 No move buttons"]
  D -->|other stop| E["Move earlier or later, or drag"]
  E --> MP06["MP-06 Now stop N in the tour"]
  A --> F{"Clip into or out of this stop ready?"}
  F -->|yes| MP11["MP-11 and MP-12 Play buttons"]
  F -->|no| MP13["MP-13 No play buttons"]
''',
[
("MP-01", "3+ stops with photos", "Click a room on the map, then a card in the route list.", "Panel shows 'Stop X of N', the stop's first photo, direction picker, move buttons, approved photos and narration editor. Selected room is highlighted on both.", "P1", "", ""),
("MP-02", "Any", "Select stop 1.", "'Where the buyer arrives. Every other space is placed from here.' No direction picker, no move buttons.", "P2", "", ""),
("MP-03", "Stop 2 with no direction", "Select it.", "Line into it is dashed; text says '(assumed until you set it)'; route card shows the direction greyed.", "P2", "", ""),
("MP-04", "Stop 3", "Set Left, then Upstairs, then Downstairs.", "Each time: 'Stop is now … on the walkthrough map.' Line becomes solid. Upstairs/Downstairs adds ↑/↓ badge and a new level panel; later stops stay on their own level.", "P1", "", ""),
("MP-05", "Clips requested, a finished clip into stop 3", "Change stop 3's direction.", "Clip badge turns 'Clip out of date'; within about a minute it is requeued and regenerates with the new direction.", "P2", "Higgsfield", ""),
("MP-06", "5 stops, stop 4 movable", "Move earlier; Move later; drag stop 5 above stop 3 on desktop.", "'Kitchen is now stop 3 in the tour.' Photo scene numbers and the map redraw in the new order.", "P1", "", ""),
("MP-07", "Exterior front and Entry / foyer both have photos", "Select each; try to drag them; select stop 3 and press Move earlier.", "Fixed stops have no move buttons and can't be dragged. Stop 3's Move earlier is disabled (can't go above the fixed stops).", "P2", "", ""),
("MP-08", "Phone width", "Reorder using Move earlier/later.", "Works without drag; map scrolls sideways inside its own box, page does not.", "P2", "", ""),
("MP-09", "Keyboard only", "Tab to a room on the map, press Enter.", "Room selects; focus ring visible.", "P3", "", ""),
("MP-10", "Archived property", "Open the map.", "Hint reads 'It is a schematic, not a floor plan.' No direction picker or move buttons.", "P3", "", ""),
("MP-11", "Finished clip into a stop", "Press the ▶ badge on the map line.", "Clip plays in the panel; close button or end of clip stops it.", "P2", "Higgsfield", ""),
("MP-12", "Finished clips around stop 3", "Select stop 3.", "'Walk in from <previous>' and 'Walk on to <next>' buttons appear and play the right clips.", "P2", "Higgsfield", ""),
("MP-13", "No clips", "Select any stop.", "No play buttons or legend entry for clips.", "P3", "", ""),
])

# ---------------------------------------------------------------- Not in tour
section("XP", "Media: Not in tour (unsorted and extra photos)",
"Photos held outside the Tour Map. They never reach clips or campaign videos until placed in a stop.",
r'''
flowchart TD
  A["Photo not on the Tour Map"] --> B{"Where is it?"}
  B -->|no stop, legacy| XP01["XP-01 Unsorted photos section"]
  B -->|Extra photos| C["Extra photos strip"]
  C -->|Place in suggestion| XP04["XP-04 Placed in suggested stop"]
  C -->|Add to stop| XP05["XP-05 Existing or new stop"]
  C -->|tick photos| D["Bulk toolbar"]
  D -->|Move selected| XP08["XP-08 Moved, partial message if some fail"]
  D -->|Fix selected| XP09["XP-09 Fix queued per photo"]
  D -->|Remove clutter with AI| XP13["XP-13 Always fails, see Findings"]
  D -->|Delete selected| XP10["XP-10 Confirm then deleted"]
  A --> XP14["XP-14 Never in clips or campaigns"]
''',
[
("XP-01", "A photo with no stop (older data)", "Open Media.", "'Not in tour: Unsorted photos' section with 'Add to stop' and, when analyzed, 'Place in <suggested stop>'.", "P2", "", ""),
("XP-02", "Any", "Add extra photos (upload).", "'Photo added to extra photos.' Thumbnail appears in the strip; Tour Map count unchanged.", "P1", "", ""),
("XP-03", "No extra photos", "Open the section.", "'No extra photos yet.' and no bulk toolbar.", "P3", "", ""),
("XP-04", "Analyzed extra photo with a suggestion", "Press its suggested-stop button.", "'Photo moved to <stop>.' It leaves Extra photos and joins that stop.", "P2", "MediaAnalysis", ""),
("XP-05", "Extra photo", "Add to stop → Create new stop…, enter 'Guest suite'.", "New stop with that photo on the map. Cancelling the dialog changes nothing (XP-06).", "P2", "", ""),
("XP-06", "Extra photo", "Add to stop → Create new stop…, Cancel.", "Nothing changes.", "P3", "", ""),
("XP-07", "3 extra photos", "Tick one; tick Select all; untick Select all.", "Count reads 1, 3, 0. Bulk buttons disabled at 0.", "P2", "", ""),
("XP-08", "2 ticked", "Move selected → Kitchen.", "'2 photos moved to Kitchen.' Both now in Kitchen.", "P2", "", ""),
("XP-09", "2 ticked", "Fix selected → Fix brightness and color (and separately Straighten and auto-crop).", "'Brightness and color correction requested for 2 photos.' Each thumbnail shows progress, then Edited.", "P2", "", ""),
("XP-10", "2 ticked", "Delete selected, confirm.", "Confirm says permanently removed. '2 photos deleted.'", "P2", "", ""),
("XP-11", "Room photo found in a video", "Look at its thumbnail.", "Shows 'From video at m:ss'.", "P3", "MediaAnalysis", ""),
("XP-12", "Extra photo", "Delete it from its thumbnail.", "Confirm text: removed from extra photos.", "P3", "", ""),
("XP-13", "Clutter removal configured, 2 ticked", "Fix selected → Remove clutter with AI.", "The mark-clutter dialog opens for each photo in turn ('Mark clutter (1 of 2)'). Cancelling one skips it. Ends with 'Cleanup previews requested for N photos. Review each one before it replaces the photo.' plus 'K skipped without marked areas.' when any were cancelled. Each requested photo then shows Review cleanup (PF-08/09). Photos already waiting for review are left out.", "P1", "Photo edit AI", ""),
("XP-14", "Extra photo and unsorted photo exist", "Generate a campaign.", "Neither photo appears in any video, still post or walk-through clip.", "P1", "", "S-24, S-44"),
("XP-15", "Unsorted or extra photo with a room tag", "Add to stop → Create new stop….", "Same dialog as MT-38: the tag is offered as a one-tap name; the photo moves onto the Tour Map in the new stop.", "P2", "MediaAnalysis", ""),
])

# ---------------------------------------------------------------- Photo fixes
section("PF", "Media: photo fixes and Ask AI about a photo",
"The Fix menu on every photo, targeted AI clutter cleanup with review, compare and revert, and the same fixes requested from the Ask AI window.",
r'''
flowchart TD
  A["Fix menu on a photo"] -->|Fix brightness and color| B["Runs locally"]
  A -->|Straighten and auto-crop| B
  B -->|done| PF01["PF-01 or PF-02 Edited badge, re-analyzed"]
  B -->|nothing to straighten| PF03["PF-03 No straightening applied"]
  B -->|error| PF04["PF-04 Photo fix failed"]
  A -->|Remove clutter with AI| C{"Photo edit AI configured?"}
  C -->|no| PF05["PF-05 Item disabled with note"]
  C -->|yes| M["Mark clutter tiles"]
  M -->|0 or over 33 tiles| PF06["PF-06 Create disabled"]
  M -->|Create cleanup preview| P["PF-07 Preview being made, photo unchanged"]
  P --> R["Review AI cleanup"]
  R -->|Use this cleanup| PF08["PF-08 Applied, Edited badge"]
  R -->|Discard cleanup| PF09["PF-09 Photo unchanged"]
  PF01 --> E["Edited photo"]
  PF08 --> E
  E -->|Compare| PF11["PF-11 Side by side, Revert"]
  E -->|Revert to original| PF12["PF-12 Original back"]
  E --> PF13["PF-13 Only clips using it regenerate"]
  E --> PF14["PF-14 Drone intro on that photo goes stale"]
''',
[
("PF-01", "Any photo", "Fix → Fix brightness and color.", "Progress overlay on the photo; 'Fixing brightness and color. The original stays saved…'. When done the photo reloads with an Edited badge and is re-analyzed.", "P1", "", ""),
("PF-02", "Tilted photo", "Fix → Straighten and auto-crop.", "Message says it runs locally; photo comes back level and cropped with Edited badge.", "P1", "", ""),
("PF-03", "Already level photo", "Straighten and auto-crop.", "Amber 'No straightening applied: …' (a warning, not an error). Photo unchanged.", "P2", "", ""),
("PF-04", "Force a fix failure (e.g. corrupt file)", "Run a fix.", "Red 'Photo fix failed: …'.", "P3", "", ""),
("PF-05", "Photo edit AI not configured", "Open the Fix menu.", "Remove clutter with AI is disabled with 'Enable the approved AI photo-edit provider to use this fix.'", "P2", "", ""),
("PF-06", "Photo edit AI configured", "Remove clutter with AI; try Create with 0 tiles, then 34 tiles; Clear; Cancel.", "Create disabled at 0 and above 33 tiles; counter shows 'N selected tiles · maximum 33'. Cancel closes with no change.", "P1", "Photo edit AI", ""),
("PF-07", "PF-06 dialog", "Tick tiles over a lamp, Create cleanup preview.", "'Creating a cleanup preview from only the areas you marked. The current photo stays in use until you approve it.' Then 'Review cleanup' on the photo and a 'Review AI cleanup' button.", "P1", "Photo edit AI", ""),
("PF-08", "Cleanup ready", "Review → Use this cleanup.", "'AI cleanup approved and applied.' Photo shows the cleanup, Edited badge; only marked tiles changed.", "P1", "Photo edit AI", "S-43"),
("PF-09", "Cleanup ready", "Review → Discard cleanup.", "'AI cleanup discarded. The current photo was not changed.'", "P1", "Photo edit AI", "S-43"),
("PF-10", "Cleanup ready", "Open review, close with X.", "Nothing applied; the cleanup still waits for review.", "P3", "Photo edit AI", ""),
("PF-11", "Edited photo", "Press Edited badge → Compare dialog → Revert.", "Side by side shows the fixes applied; Revert restores the original.", "P2", "", ""),
("PF-12", "Edited photo", "Fix → Revert to original, confirm.", "'The original photo is back in place.' Badge gone; videos use the original again.", "P1", "", "S-24"),
("PF-13", "Clips exist on both sides of a photo", "Fix that photo.", "Only the clips into and out of that photo go out of date and regenerate.", "P2", "Higgsfield", ""),
("PF-14", "Approved drone intro", "Fix or revert the Exterior front photo.", "Drone intro shows 'photo or drone motion profile changed…'; new campaigns don't use it (DI-10).", "P1", "Drone AI", ""),
("PF-15", "Fixed photo on the Tour Map", "Generate a campaign.", "Every video uses the fixed version.", "P1", "", "S-24"),
("PF-16", "Any photo", "Press the ✦ Ask AI button on it; ask 'can you brighten this?'; press the proposed action; confirm.", "Ask AI window shows 'Selected photo'. Proposed button 'Fix brightness and color'; confirm dialog; 'Photo fix requested. The Media tab will update when it finishes.'", "P2", "OpenAI", ""),
("PF-17", "Photo edit AI configured", "Ask AI about a photo: 'remove the clutter on the counter'.", "Button 'Mark clutter to remove' opens the tile dialog; after submit 'Cleanup preview requested…'; review happens on the Media tab (PF-08/09).", "P2", "OpenAI, Photo edit AI", ""),
("PF-18", "AI proposed a fix for photo A", "Select photo B, then press the old proposal button.", "Proposal is cleared or acts on nothing; photo B is not changed.", "P3", "OpenAI", ""),
("PF-19", "A fix is running on a photo", "Look at the photo; tick it and use Fix selected.", "Fix button hidden while running. Bulk: 'Every selected photo already has a fix in progress.'", "P3", "", ""),
("PF-20", "Cleanup ready, then the same cleanup is used or discarded in a second browser tab", "Press Use this cleanup (or Discard) in the first tab, then Revert to original on an already-original photo.", "Use/Discard act on the first press with no reload. A cleanup already used or discarded shows 'This cleanup was already used or discarded. The Media tab now shows the latest photos.' Revert on an original says 'This photo is already the original…'. Cleanups blend in with no box edges.", "P2", "Photo edit AI", "S-43"),
])

# ---------------------------------------------------------------- Walk-through clips
section("WC", "Media: AI walk-through clips",
"Higgsfield clips that walk from each Tour Map shot into the next. Hidden unless the provider is configured or finished clips exist.",
r'''
flowchart TD
  A{"Clip provider configured?"} -->|no, no clips| WC01["WC-01 No clip panel"]
  A -->|no, old clips exist| WC13["WC-13 Clips play, no create button"]
  A -->|yes| B["Panel: 0 of N ready, Create N clips"]
  B -->|Create| WC03["WC-03 Queued, generating, ready"]
  WC03 -->|ready| WC04["WC-04 Play on the photo"]
  WC03 -->|failed| WC05["WC-05 Clip failed, Try again"]
  WC04 --> C["Change a photo, order or direction"]
  C -->|clips were requested before| WC07["WC-07 Only changed pairs requeue"]
  C -->|never requested| WC09["WC-09 Nothing auto-created"]
  WC04 --> D{"Campaign walkthrough switch"}
  D -->|on| WC11["WC-11 Finished current clips used"]
  D -->|off| WC11b["WC-11 Stills only"]
''',
[
("WC-01", "Provider not configured, no clips", "Open Media.", "No 'AI walk-through clips' panel; photo cards show no clip status.", "P2", "", ""),
("WC-02", "Provider configured, 4 Tour Map photos", "Open Media.", "Panel '0 of 3 walk-throughs ready' and 'Create 3 walk-through clips'. Each card says 'No clip yet'.", "P1", "Higgsfield", ""),
("WC-03", "WC-02", "Press Create.", "'Creating 3 walk-through clips. Each shot gets a play button when its clip is ready.' Badges go queued → generating → Clip ready without reloading (5 s polling). Notification when the batch ends.", "P1", "Higgsfield", ""),
("WC-04", "Clip ready", "Press play on the photo.", "Clip plays over the photo; close button or clip end returns to the photo.", "P1", "Higgsfield", ""),
("WC-05", "A clip failed", "Read the badge, press Try again.", "'Clip failed' with reason on hover; Try again requeues it.", "P2", "Higgsfield", ""),
("WC-06", "All clips requested", "Look at the panel button.", "Disabled 'All walk-throughs requested'.", "P3", "Higgsfield", ""),
("WC-07", "Clips requested earlier", "Reorder a stop, add a photo, change a direction.", "Panel explains only changed clips are remade. Affected badges show 'Clip out of date', then requeue on their own after about 45 s. Untouched clips stay ready.", "P1", "Higgsfield", ""),
("WC-08", "A stale clip", "Hover its badge.", "'ready, but made before a photo or its walking direction changed; create it again to match'.", "P2", "Higgsfield", ""),
("WC-09", "Clips never requested", "Reorder and change directions.", "No clips are created automatically.", "P2", "Higgsfield", ""),
("WC-10", "Clip from photo A to B", "Delete photo A.", "Its clips are removed; a new pair B-neighbour shows 'No clip yet' or requeues.", "P3", "Higgsfield", ""),
("WC-11", "Finished clips", "Generate with the walkthrough switch on, then off.", "On: clips play between those photos (vertical as its own sped-up shot; YouTube tour at the end of the first photo's scene). Off: stills only.", "P1", "Higgsfield", "S-23"),
("WC-12", "Clip file unreadable at render time", "Generate.", "Campaign still completes using photos only (renderer fallback).", "P3", "Higgsfield", ""),
("WC-13", "Clips exist, provider later turned off", "Open Media.", "Clips still play; no Create button or Try again.", "P3", "Higgsfield", ""),
])

# ---------------------------------------------------------------- Drone intro
section("DI", "Media: AI drone fly-in",
"Optional AI-generated descent that lands on the Exterior front photo. Professional and Enterprise plans only. Agent must acknowledge it is AI, approve the finished clip, then tick it on the Campaign tab.",
r'''
flowchart TD
  A{"Exterior front is stop 1 with a photo?"} -->|no| DI02["DI-02 Section not shown"]
  A -->|yes| B{"Drone provider on, or an intro exists?"}
  B -->|neither| DI01["DI-01 Section hidden"]
  B -->|yes| PL{"Plan includes premium AI?"}
  PL -->|Trial or Basic| DI14["DI-14 Locked with Unlock button"]
  PL -->|Professional or Enterprise| C["Pick 50 ft or 100 ft, tick acknowledgement"]
  C -->|not ticked| DI03["DI-03 Create disabled"]
  C -->|Create drone intro| D["DI-04 Making, Cancel available"]
  D -->|Cancel| DI05["DI-05 Removed"]
  D -->|failed| DI11["DI-11 Error, Try again or Remove"]
  D -->|ready| E["DI-06 Preview, Approve or Make a new one"]
  E -->|Approve| F["DI-07 Approved"]
  E -->|not approved| DI09["DI-09 Campaigns ignore it"]
  F --> SP["DI-15 Flyover speed"]
  F --> DI16["DI-16 Map plays drone then entry clip"]
  F -->|ticked on Campaign tab| DI08["DI-08 Videos open with drone and AI label"]
  F -->|front photo replaced, fixed or reordered| DI10["DI-10 Out of date, not used"]
''',
[
("DI-01", "Drone provider off, no intro", "Open the Exterior front stop panel.", "No 'AI drone fly-in' section.", "P2", "", ""),
("DI-02", "Exterior front has no photo", "Open Media.", "No drone section anywhere.", "P2", "", ""),
("DI-03", "Provider on", "Open the section; choose High aerial · approximately 100 ft; don't tick the box.", "Copy explains everything above the photo is made up by AI. Low 50 ft is selected by default. Create stays disabled until the acknowledgement is ticked.", "P1", "Drone AI", ""),
("DI-04", "DI-03 with box ticked", "Create drone intro.", "'Making a low/high … drone intro…' with progress bar and Cancel. Notification when ready.", "P1", "Drone AI", ""),
("DI-05", "Intro in progress", "Cancel.", "Intro removed; section back to the start state.", "P2", "Drone AI", ""),
("DI-06", "Intro ready, not approved", "Watch the preview.", "Video plays; text asks you to check roof, yard, street and neighbours; buttons Approve for videos, Make a new one, Remove.", "P1", "Drone AI", ""),
("DI-07", "DI-06", "Approve for videos.", "Status becomes approved. The Campaign tab now shows 'Start campaign videos with this approved AI drone flyover' (CX-08) instead of the walkthrough checkbox.", "P1", "Drone AI", "S-35"),
("DI-08", "Approved, current intro, CX-08 ticked", "Generate a campaign.", "Opening sequence: flyover with the burned-in label 'AI-generated aerial · Not actual footage' → Exterior front and front door → entryway walk-through clip when it's finished → rest of the Tour Map.", "P1", "Drone AI", "S-35"),
("DI-09", "Intro ready but not approved", "Generate a campaign.", "Videos open on the front photo with no drone shot.", "P1", "Drone AI", "S-36"),
("DI-10", "Approved intro", "Replace, fix or move the Exterior front photo.", "Warning 'The Exterior front photo or drone motion profile changed since this intro was made, so videos don't use it.' New campaigns skip it until a new one is made.", "P1", "Drone AI", "S-36"),
("DI-11", "Provider returns an error", "Create.", "Red error with Try again and Remove.", "P2", "Drone AI", ""),
("DI-12", "Approved intro", "Make a new one, then Cancel.", "Returns to the approved intro unchanged.", "P3", "Drone AI", ""),
("DI-13", "Approved intro", "On the Campaign tab untick 'Start campaign videos with this approved AI drone flyover'; generate.", "No drone shot, no uploaded video footage and no AI walk-through clips: photos only.", "P2", "Drone AI", "S-37"),
("DI-14", "Trial or Basic plan, Exterior front photo set", "Open the drone section.", "Header 'AI drone fly-in' with 'Professional and Enterprise' in place of the status, an Unlock button to Billing, and every control (height, acknowledgement, Create, Approve) disabled. An intro made before a downgrade shows 'The AI drone intro is included with the Professional and Enterprise plans, so campaign videos on your current plan don't use it.'", "P1", "", ""),
("DI-15", "Intro ready", "Pick Smooth 1×, Brisk 1.5×, Fast 2× under Flyover speed; play the preview.", "Preview plays at the chosen speed; the choice is saved and used in campaign videos. Note says changing speed reuses the clip and makes no new paid video.", "P2", "Drone AI", ""),
("DI-16", "Approved intro and a finished clip from Exterior front into the entry", "On the route map or photo strip press the landing icon on the first stop.", "Plays '1 of 2 · Drone flyover' at the chosen speed, then '2 of 2 · Walk into the entry'. Un-approving the intro mid-play stops it.", "P3", "Drone AI, Higgsfield", ""),
])

# ---------------------------------------------------------------- Room narration
section("RN", "Media: room-by-room narration",
"Narration written per stop in the map panel. The first save asks whether it replaces the AI script.",
r'''
flowchart TD
  A["Stop panel: Add narration"] --> B["Type text, up to 1,000 characters"]
  B -->|Cancel| RN07["RN-07 Nothing saved"]
  B -->|Save| C{"First narration, source not chosen yet?"}
  C -->|no| RN05b["Saved message"]
  C -->|yes| D{"Use your narration in place of the AI script?"}
  D -->|Use my narration| RN04["RN-04 Room narration in use"]
  D -->|Keep the AI script| RN05["RN-05 AI script stays"]
  D -->|Decide later| RN06["RN-06 Asked again next save"]
  RN04 --> E{"Total words"}
  E -->|145 or fewer| RN11["RN-11 Tour videos read it room by room"]
  E -->|over 145| RN10["RN-10 60 s videos fall back, Full narrated tour enabled"]
''',
[
("RN-01", "Stop 1 and stop 3 panels", "Open each.", "Stop 1 button reads 'Add opening narration'; others 'Add narration for this room'.", "P1", "", ""),
("RN-02", "Editor open", "Type 40 words; try to pass 1,000 characters.", "Shows word count and estimated spoken seconds; counter stops at 1,000.", "P2", "", ""),
("RN-03", "No narration saved before", "Save the first narration.", "Dialog 'Use your narration in place of the AI script?' with Use my narration / Keep the AI script / Decide later.", "P1", "", ""),
("RN-04", "RN-03", "Use my narration.", "'Saved the narration for X. Campaign videos will read it as the tour reaches that stop.' Campaign tab Narration dropdown shows room-by-room.", "P1", "", ""),
("RN-05", "RN-03", "Keep the AI script.", "Saved; message says videos use the AI script until you switch on the Campaign tab.", "P1", "", ""),
("RN-06", "RN-03", "Decide later; then save narration on another stop.", "Dialog appears again.", "P2", "", ""),
("RN-07", "Editor open with changes", "Cancel.", "Text not saved.", "P3", "", ""),
("RN-08", "Saved narration", "Edit → Remove.", "'Removed the narration for X.' Route icon gone.", "P2", "", ""),
("RN-09", "Two narrated stops", "Look at the route list.", "Narrated stops show the voice icon.", "P3", "", ""),
("RN-10", "Room narration in use", "Push the total over 145 words.", "Map and Campaign tab warn the 60-second videos fall back to the AI script; Full narrated tour checkbox becomes available.", "P1", "", "S-39"),
("RN-11", "Room narration in use, under 145 words, two rooms narrated", "Generate YouTube tour.", "Each passage starts as that room's photo appears; rooms without narration play without voice.", "P1", "OpenAI or ElevenLabs", "S-38"),
("RN-12", "Room narration in use", "Generate the vertical video with Short voiceover.", "The 30-second hook voiceover is still written from the property story (by design).", "P2", "OpenAI", "S-38"),
("RN-13", "Archived property with narration", "Open a narrated stop and an unnarrated one.", "Narrated stop shows its text read-only; the other shows no narration block.", "P3", "", ""),
])

# ---------------------------------------------------------------- Videos
section("VD", "Media: listing videos and Find room photos",
"Uploaded walkthrough videos: enhancement (stabilize, HDR to bright SDR), labels and order, and pulling room photos into Extra photos.",
r'''
flowchart TD
  A["Add listing videos"] --> B{"File OK?"}
  B -->|MP4 MOV M4V WEBM, up to 500 MB and 5 min| C["VD-01 Queued, Enhancing"]
  B -->|too big, too long, wrong type| VD02["VD-02 Error, Retry upload"]
  B -->|would pass 10 videos| VD03["VD-03 Limit message"]
  C -->|done| D["Enhanced"]
  C -->|failed| VD04["VD-04 Retry enhancement"]
  D -->|label stop, reorder| VD07["VD-07 and VD-08 Saved"]
  D --> E{"Media analysis on?"}
  E -->|no| VD14["VD-14 No Find room photos"]
  E -->|yes| F["Find room photos"]
  F -->|done| VD10["VD-10 Rooms found, photos in Extra photos"]
  F -->|failed| VD13["VD-13 Attempt failed"]
  D --> G{"Campaign walkthrough switch on?"}
  G -->|yes| VD16["VD-16 Footage in videos"]
''',
[
("VD-01", "Any", "Upload a 60 s MP4.", "'Video uploaded. Enhancement is queued; use Refresh to check progress.' Chip Queued → Enhancing → Enhanced; card shows resolution, duration, fps, size, audio and the enhanced size.", "P1", "", ""),
("VD-02", "Any", "Upload a 6-minute video, then a 600 MB file, then an AVI.", "Each fails with a reason and Retry upload.", "P2", "", ""),
("VD-03", "9 videos", "Select 2 more.", "'These files would exceed the 10-video property limit.'", "P3", "", ""),
("VD-04", "Enhancement failed", "Retry enhancement.", "'Attempt N failed: …' then 'Video enhancement queued for retry.'", "P2", "", ""),
("VD-05", "iPhone HDR video", "Upload and play the enhanced version.", "Bright, normal colour (no grey/washed HDR look).", "P1", "", ""),
("VD-06", "Enhanced video", "Download enhanced MP4.", "File downloads.", "P3", "", ""),
("VD-07", "Enhanced video", "Set Walkthrough stop to Kitchen.", "'Walkthrough stop updated. New campaigns will use this verified label.'", "P2", "", ""),
("VD-08", "2 videos", "Move the second up.", "'Walkthrough order updated. New campaigns will follow this route.'", "P2", "", ""),
("VD-09", "Video with room photos already found", "Delete it, confirm.", "Confirm says room photos stay. 'Video deleted.' Room photos remain in Extra photos.", "P2", "", ""),
("VD-10", "Media analysis on", "Find room photos.", "Status steps: finding sharpest shot → recognizing rooms in shot X of Y → adding best shots. Ends 'Room photos: N rooms found, M new photos in extra photos.' Extra photos refresh with 'From video at m:ss'.", "P1", "OpenAI, MediaAnalysis", ""),
("VD-11", "VD-10 done", "Find room photos again.", "Runs again; button disabled while running.", "P3", "OpenAI, MediaAnalysis", ""),
("VD-12", "45 photos already", "Find room photos on a long tour.", "Completion text adds 'K more did not fit under the 50-photo limit.'", "P3", "OpenAI, MediaAnalysis", ""),
("VD-13", "Analysis provider failing", "Find room photos.", "'Room photos attempt N failed: …'.", "P3", "MediaAnalysis", ""),
("VD-14", "Media analysis off", "Look at the video card and Extra photos hint.", "No Find room photos button; hint doesn't mention it.", "P3", "", ""),
("VD-15", "Video enhancing", "Press Refresh.", "Statuses update.", "P3", "", ""),
("VD-16", "Enhanced video", "Generate with walkthrough switch on, then off.", "On: vertical uses the steadiest 2-4 s stretches first; tour formats play video where the planner chose. Off: no video footage.", "P1", "", "S-23"),
("VD-17", "Room photo from a video placed in a stop", "Generate the vertical video.", "That moment plays as moving video in the vertical; tour formats show it as a still.", "P2", "MediaAnalysis", ""),
("VD-18", "Landscape (sideways) video uploaded", "Look at its card.", "Note: 'Filmed sideways: TikTok, Reels and Shorts keep only the middle third of this clip, enlarged. For social videos, hold the phone upright and film in 4K at 30 fps.' Portrait videos show no note.", "P3", "", ""),
])

# ---------------------------------------------------------------- Inputs from other tabs
section("IN", "Inputs from other tabs that change campaigns",
"Not part of Overview → Media → Campaign, but they decide what the campaign can do, so each needs at least one test.",
r'''
flowchart LR
  S["Story and script tab"] -->|accepted script, 145 words or fewer| IN02["IN-02 Short narration"]
  S -->|accepted script, over 145 words| IN03["IN-03 Full narrated tour available"]
  S -->|stored only| IN01["IN-01 Not used"]
  N["Neighborhood tab"] -->|approved places| IN06["IN-06 Highlights in tour formats"]
  P["Profile page"] -->|headshot| IN07["IN-07 Agent presenter options"]
  P -->|Preview presenter| IN09["IN-09 Talking or silent preview"]
  S -->|story names a feature not in the facts| IN08["IN-08 Note names the word, Overview link"]
''',
[
("IN-01", "Story & script tab", "Upload a .docx script (under 2 MB), leave it unaccepted.", "Chip 'Stored only'; campaigns ignore it.", "P2", "", ""),
("IN-02", "Script of 120 words", "Tick 'I confirm this script is accurate…'.", "Chip 'Accepted'; next campaign's tour narration uses it. Full narrated tour stays unavailable.", "P1", "", ""),
("IN-03", "Script of 300 words", "Accept it, open Campaign.", "'Long form · about N min' chip; Full narrated tour checkbox enabled.", "P1", "", "S-02"),
("IN-04", "IN-03 with Full narrated tour ticked", "Un-accept the script, return to Campaign.", "Full narrated tour unticked and greyed.", "P2", "", "S-03"),
("IN-05", "Story tab", "Generate story.", "Voiceover walks the stops in Tour Map order with set directions.", "P2", "OpenAI", ""),
("IN-06", "Neighborhood tab", "Approve 2 places, generate with Neighborhood highlights on.", "Places appear in YouTube tour (and Facebook only if that scene makes the 30 s cut); distances as whole-mile phrases.", "P2", "Google Places", ""),
("IN-07", "Profile page", "Upload a headshot; tick 'include avatar in campaigns'.", "Campaign tab shows the headshot preview and pre-selects AI agent presenter with consent (CA-06).", "P1", "", ""),
("IN-08", "Overview description empty; story would mention a garage", "Story tab → Generate story (and a campaign that reaches the story stage).", "Red note 'Generate story stopped because the story mentions \"garage\"…' with an 'Open the Overview tab' link. On the Campaign tab the failed story stage shows the same note and link. Adding the feature to the description and retrying passes.", "P1", "OpenAI", ""),
("IN-09", "Profile with headshot", "Preview the agent presenter in a campaign video.", "Lip-sync on: a paid, lip-synced short greeting ('Presenter preview is ready.'). Lip-sync off: silent layout preview with 'Lip-sync is not enabled in this environment…'. Trial/Basic: presenter shows the Professional and Enterprise Unlock prompt.", "P2", "Lip-sync", ""),
])

# ---------------------------------------------------------------- Campaign setup
section("CG", "Campaign: deliverables and the Generate gate",
"Which outputs can be picked and the four things that keep Generate disabled.",
r'''
flowchart TD
  A["Campaign tab"] --> B{"Any Tour Map photo?"}
  B -->|no| CG01["CG-01 Info alert, Generate disabled"]
  B -->|1 or 2| CG02["CG-02 Few photos warning"]
  B -->|yes| C{"At least one output ticked?"}
  C -->|no| CG04["CG-04 Choose at least one output"]
  C -->|yes| D{"Template details ready?"}
  D -->|Price drop or Open house incomplete| CT03["CT-03 or CT-04 Disabled"]
  D -->|AI presenter without consent| CA04["CA-04 Disabled"]
  D -->|library on, no video picked| VL06["VL-06 Pick at least one video"]
  D -->|Kling presenter blockers| KP04["KP-04 Before you can generate list"]
  D -->|ready| E["Generate campaign enabled"]
  A --> F{"Long script or long room narration?"}
  F -->|no| CG05["CG-05 Full narrated tour greyed"]
  F -->|yes| CG05b["CG-05 Full narrated tour can be ticked"]
''',
[
("CG-01", "No Tour Map photos (maybe Extra photos only)", "Open Campaign.", "Info: 'Campaigns use only photos placed on the Tour Map…' with a Media tab link. Generate disabled.", "P1", "", "S-44"),
("CG-02", "1 or 2 Tour Map photos", "Open Campaign.", "Warning 'Only N photos are available…at least three…recommended'. Generate still allowed.", "P2", "", "S-28"),
("CG-03", "No campaign yet", "Open Campaign.", "Ticked by default: Vertical video, YouTube property tour, Facebook Feed, Facebook still-image post. Full narrated tour off.", "P1", "", ""),
("CG-04", "Defaults", "Untick all outputs.", "Red 'Choose at least one output before generating the campaign.' Generate disabled.", "P1", "", "S-01"),
("CG-05", "No accepted long script, no long room narration", "Look at Full narrated tour.", "Greyed with 'Accept a narration script, or use room-by-room narration, longer than 145 words…'.", "P1", "", "S-02"),
("CG-06", "Only Facebook still-image post ticked", "Generate.", "Two JPEG posts (square and portrait) with captions. Verify whether any video cards appear (code path not traced end to end).", "P2", "OpenAI", "S-32, Deliverable combos"),
("CG-07", "Archived property", "Open Campaign.", "Every control disabled; no video chat.", "P3", "", "S-31"),
("CG-08", "Campaign generated with non-default choices", "Reload the page.", "Every control comes back as saved.", "P2", "", "S-25"),
("CG-09", "Old campaign that asked for separate TikTok, Reel and Short", "Open Campaign.", "Only the single vertical box is ticked.", "P3", "", "S-26"),
("CG-10", "Any campaign state", "Scroll to the bottom of the Campaign tab.", "No 'Suggest a future feature' box (hidden). The header Comments button still sends suggestions.", "P3", "", ""),
("CG-11", "VerticalVideoLibraryV1 off for the workspace", "Open Campaign.", "Legacy layout: one shared vertical video ('TikTok, Reels and Shorts' with Style and Voiceover), button 'Generate campaign'. No goal picker, format cards or performance panel.", "P1", "", ""),
])

# ---------------------------------------------------------------- Templates
section("CT", "Campaign: marketing templates",
"Seven templates. Picking one resets the on-screen elements and, unless you changed them, music and caption look.",
r'''
flowchart TD
  A["Marketing template"] -->|Balanced showcase| T1["Default elements"]
  A -->|Fast social tour| T2["Hard cuts everywhere"]
  A -->|Feature spotlight| T3["Facts and neighborhood on"]
  A -->|Agent-branded showcase| T4["Agent photo on if headshot"]
  A -->|Just listed| T5["JUST LISTED hook"]
  A -->|Price-drop announcement| T6{"Amount and confirm?"}
  T6 -->|no| CT03["CT-03 Generate disabled"]
  T6 -->|yes| T6b["PRICE REDUCED hook"]
  A -->|Open house| T7{"Day and time and confirm?"}
  T7 -->|no| CT04["CT-04 Generate disabled"]
  T7 -->|yes| T7b["OPEN HOUSE hook"]
  A --> N{"Music or caption look changed by you first?"}
  N -->|no| CT01["CT-01 Template sets them"]
  N -->|yes| CT02["CT-02 Kept your choice"]
''',
[
("CT-01", "Music and caption look untouched", "Pick each of the 7 templates.", "Description alert changes; note under it: '<Template> set the on-screen elements, music (…), caption look (…).' Advanced checkboxes change per template.", "P1", "", "S-09"),
("CT-02", "Change Music, then Caption look", "Pick another template.", "Note ends 'and kept your music' / 'and kept your music and your caption look'.", "P2", "", "S-10"),
("CT-03", "Price-drop announcement", "Leave amount blank; then enter $25,000 without confirming; then confirm.", "Red 'Enter the reduction amount and confirm it…'; Generate disabled until both are set.", "P1", "", "S-04"),
("CT-04", "Open house", "Blank text; 41 characters; valid text without confirm; then confirm.", "Field stops at 40; Generate disabled until text and confirm are set.", "P1", "", "S-05"),
("CT-05", "Price-drop filled in", "Switch to Just listed and back.", "Amount and confirm are cleared.", "P2", "", "S-11"),
("CT-06", "Agent-branded showcase", "Pick it with and without a headshot.", "With headshot: Agent photo on closing card ticked. Without: not ticked, no error.", "P2", "", "S-22"),
("CT-07", "Generated campaign per template", "Watch the vertical video's first second.", "Just listed: JUST LISTED pill; Open house: OPEN HOUSE + time; Price drop: PRICE REDUCED $X; others: the price. Fast social tour: hard cuts in every format.", "P1", "OpenAI", "Pairwise"),
])

# ---------------------------------------------------------------- Sound and voice
section("CS", "Campaign: sound, voiceover, narration source and language",
"Music for all videos, the vertical video's style and voiceover, which script is read, and the four language menus.",
r'''
flowchart TD
  M["Music"] -->|Choose for the home's era| M1{"Year built"}
  M1 -->|1949 or earlier| H["Warm acoustic"]
  M1 -->|1950 to 1989| U["Upbeat pop"]
  M1 -->|1990 or later, or blank| MO["Modern chill"]
  M -->|named track| P["CS-02 Preview player"]
  M -->|None, add in app or No music| CS03["CS-03 No music"]
  V["Vertical voiceover"] -->|Short 30 s| CS05["CS-05 Hook voiceover"]
  V -->|Music and text only| CS06["CS-06 No voice, no captions"]
  V -->|Full tour 60 s| CS07["CS-07 Reuses tour narration"]
  CS06 -->|plus No music| CS06b["CS-06 Silent file, no warning"]
  R{"Room narration exists?"} -->|no| R0["No Narration dropdown"]
  R -->|yes| CS09["CS-09 Room-by-room or AI script"]
''',
[
("CS-01", "Properties built 1925, 1972, 2015 and one with no year", "Music = Choose for the home's era; generate.", "Warm acoustic, Upbeat pop, Modern chill, and Modern chill for no year (sampled-instrument originals).", "P1", "OpenAI", "S-12"),
("CS-02", "Any", "Pick Upbeat pop, Modern chill, Warm acoustic, Luxury cinematic; press play on the preview.", "Preview player appears and plays for the four named tracks; the era option shows the era hint instead; add-in-app shows its hint.", "P2", "", "S-13"),
("CS-03", "Any", "Music = None, I'll add a sound… and separately No music.", "Hint for add-in-app; both files have no music.", "P2", "OpenAI", "S-14"),
("CS-04", "Vertical selected", "Style = Fast vs Cinematic glide.", "Fast: about 1.6 s shots, hard cuts on the beat. Cinematic: about 4 s shots with short crossfades.", "P2", "OpenAI", ""),
("CS-05", "Vertical, Short voiceover, spoken German", "Generate.", "Hook voiceover fits 30 s; if too long it retries with only hook and closing line.", "P1", "OpenAI", "S-15"),
("CS-06", "Vertical, Music and text only", "Generate with music, then with No music.", "No voice and no captions. With No music an amber warning says the vertical video will be silent (no warning for 'add a sound in the app'); Generate still works.", "P2", "OpenAI", "S-16"),
("CS-07", "Vertical + YouTube tour, Full tour voiceover", "Generate.", "Vertical is 60 s and uses the tour narration audio.", "P2", "OpenAI", "S-17"),
("CS-08", "Profile has name and phone", "Toggle 'Show my name and phone on short videos'.", "On: strip along the bottom of the vertical. Off, or both blank: no strip.", "P2", "OpenAI", ""),
("CS-09", "Room narration saved", "Switch Narration between room-by-room and AI script.", "Dropdown only shows when room narration exists. Helper text changes; choice persists and matches the map status.", "P1", "", "S-40"),
("CS-10", "Any", "Spoken Spanish, captions English (plus Language matrix rows).", "Voice Spanish; captions English, timed to the Spanish passages.", "P1", "OpenAI", "S-19 + Language matrix"),
("CS-11", "ElevenLabs key present / absent", "Generate with each accent.", "ElevenLabs voice per accent when configured; otherwise OpenAI voice and the campaign still completes.", "P2", "ElevenLabs", "S-29, S-30"),
("CS-12", "Library on, Wow feature picked", "Music = No music.", "Amber: 'The Wow feature video has no voiceover, so with No music it will be silent…'. Generate still works.", "P2", "", "S-16"),
])

# ---------------------------------------------------------------- Agent presenter
section("CA", "Campaign: agent presenter",
"Headshot on the closing card, or the AI presenter (lip-synced when the provider is on, static photo otherwise).",
r'''
flowchart TD
  A{"Profile headshot?"} -->|no| CA01["CA-01 Add profile headshot button"]
  A -->|no, tick a presenter option| CA03["CA-03 Unticks with error"]
  A -->|yes| B["Agent photo on closing card or AI agent presenter"]
  B -->|AI presenter| PL{"Professional or Enterprise?"}
  PL -->|no| CA08["CA-08 Locked, Unlock"]
  PL -->|yes| C{"Consent ticked?"}
  C -->|no| CA04["CA-04 Generate disabled"]
  C -->|yes| D{"Lip-sync provider on?"}
  D -->|yes| CA05["CA-05 Lip-synced opening and closing"]
  D -->|no or provider fails| CA05b["CA-05 Static portrait fallback"]
  B --> L["CA-07 Localize closing card enabled"]
''',
[
("CA-01", "No headshot", "Open Agent presenter.", "'Add profile headshot' button goes to /profile.", "P2", "", ""),
("CA-02", "Headshot uploaded", "Open Agent presenter.", "Round preview, filename and size, link 'Crop, replace, or AI-fix this headshot'.", "P3", "", ""),
("CA-03", "No headshot", "Tick Agent photo on closing card, then AI agent presenter.", "Each unticks itself; 'Add a profile headshot before adding an agent presenter to the video.'", "P1", "", "S-06"),
("CA-04", "Headshot", "Tick AI agent presenter without consent; then untick presenter.", "Consent checkbox appears; Generate disabled until ticked. Unticking presenter clears consent.", "P1", "", "S-07"),
("CA-05", "Presenter with consent", "Generate with lip-sync on, then off.", "On: lip-synced opening and closing clips. Off or provider down: static portrait while the AI voice narrates; campaign still completes.", "P2", "Lip-sync", "S-41"),
("CA-06", "Profile set to include avatar, headshot exists, no running campaign", "Open Campaign.", "AI agent presenter pre-ticked with consent; Agent photo unticked.", "P2", "", "S-42"),
("CA-07", "Agent photo or presenter on, spoken Spanish", "Toggle 'Use the selected language and accent during the agent closing card'.", "Disabled unless a headshot option is on. Off: closing narration stays English. (Known: with AI presenter only, Localize has no effect.)", "P2", "OpenAI", "S-20, S-21"),
("CA-08", "Trial or Basic plan", "Open Advanced: on-screen elements.", "'AI agent presenter' is greyed with 'Included with Professional and Enterprise.' and an Unlock button. Forcing it (old saved settings) gives the premium AI upgrade message and no campaign.", "P1", "", ""),
])

# ---------------------------------------------------------------- Advanced
section("CX", "Campaign: Advanced on-screen elements",
"The folded Advanced panel. Each checkbox is one test pair (on/off).",
r'''
flowchart LR
  A["Advanced panel"] --> X1["CX-01 Opening title"]
  A --> X2["CX-02 Property facts"]
  A --> X3["CX-03 Captions and caption look"]
  A --> X4["CX-04 Neighborhood highlights"]
  A --> X5["CX-05 Walkthrough video and AI clips"]
  A --> X6["CX-06 Closing call to action"]
  A --> X7["CX-07 Everything off"]
  P["Caption look picker A to H"] --> X9["CX-09"]
  D{"Approved drone intro?"} -->|yes| X8["CX-08 Drone opening checkbox replaces walkthrough box"]
''',
[
("CX-01", "Vertical + YouTube", "Opening title off.", "No hook pills on vertical; no opening title over the first scene on tour formats.", "P2", "OpenAI", "Pairwise"),
("CX-02", "Beds/baths set", "Property facts off.", "No spec line on vertical; no fact callouts on tour formats.", "P2", "OpenAI", "Pairwise"),
("CX-03", "Voiceover on", "Pick each caption look A to H (default F Neon night); then turn captions off.", "Captions burst in the chosen look in every video; none when off.", "P2", "OpenAI", "S-18"),
("CX-04", "Approved neighborhood places", "Neighborhood highlights on/off.", "Neighborhood scenes in YouTube tour only when on.", "P2", "OpenAI, Google Places", "Pairwise"),
("CX-05", "Video and clips exist", "Walkthrough switch off.", "Every video uses still photos only: no video footage, walk-through clips or drone intro.", "P1", "", "S-23, S-37"),
("CX-06", "Any", "Closing call to action off.", "No end card / closing CTA scene.", "P3", "OpenAI", "Pairwise"),
("CX-07", "Music = No music", "Untick name/phone and every Advanced box; Generate.", "Generate becomes disabled and red text reads 'Turn on at least one on-screen element, your name and phone, or music before generating the campaign.' Ticking any box or picking a music track enables it again. No crash, no campaign created.", "P1", "", "S-08"),
("CX-08", "Approved, current drone intro", "Look at the Campaign tab.", "A boxed 'Start campaign videos with this approved AI drone flyover' checkbox with the opening sequence described; the walkthrough checkbox is gone from Advanced. Ticking it also turns on uploaded video and AI walk-through clips.", "P1", "Drone AI", "S-35, S-37"),
("CX-09", "Any", "Open the caption look picker.", "Eight thumbnails A Original, B Bold outline, C Candy pop, D Sunset pills, E Comic, F Neon night (default), G Marker, H Brand bold. Older campaigns with Classic show as Original. Picked look survives a template change (CT-02) and a reload.", "P2", "", ""),
])

# ---------------------------------------------------------------- Saved settings
section("SP", "Campaign: saved settings",
"Named reusable Campaign tab settings (PR #107). Shown above the deliverables when no campaign is running.",
r'''
flowchart TD
  A["Saved settings bar"] -->|Save as| SP01["SP-01 Named settings saved"]
  A -->|Apply saved settings| SP02["SP-02 Every choice filled in"]
  SP02 -->|change something| SP03["SP-03 Update or Undo my changes"]
  A -->|more actions| M{"Action"}
  M -->|Use for new properties| SP04["SP-04 Applied on new properties"]
  M -->|Share with my workspace| SP05{"2 or more members?"}
  SP05 -->|no| SP05a["SP-05 Option hidden"]
  SP05 -->|yes| SP05b["SP-05 Shared"]
  M -->|Rename or Delete| SP06["SP-06"]
  A -->|viewing as a user| SP07["SP-07 Apply only"]
''',
[
("SP-01", "Non-default choices on the Campaign tab", "Save everything below under a name.", "Name appears in 'Apply saved settings'. Prices, open house times and consents are never saved.", "P1", "", ""),
("SP-02", "Saved settings exist", "Open another property's Campaign tab; Apply saved settings.", "Deliverables, template, music, caption look, voice, languages, elements and goal are filled in. Price-drop amount, open house time and presenter consent stay empty for you to confirm.", "P1", "", ""),
("SP-03", "Settings applied", "Change the music.", "'Update \"<name>\"' and 'Undo my changes' appear. Undo puts the saved choices back; Update saves the change.", "P2", "", ""),
("SP-04", "Saved settings marked '· used for new properties'", "Open a new property with no campaign.", "Those settings are applied on load. A property that already has a campaign keeps its own last choices.", "P2", "", ""),
("SP-05", "Workspace with 1 member, then 2", "Open the more actions menu.", "'Share with my workspace' only shows with 2 or more members; shared settings show '· shared with your workspace' for the other member.", "P3", "", ""),
("SP-06", "Saved settings", "Rename, then delete.", "List updates; deleting doesn't change the current form.", "P3", "", ""),
("SP-07", "Admin impersonating a user", "Open the bar.", "'Saved settings can be applied but not changed while you are viewing as this user.'", "P3", "", ""),
])

# ---------------------------------------------------------------- Vertical video library
section("VL", "Campaign: vertical video library (release switch VerticalVideoLibraryV1)",
"When the switch is on for the workspace and the vertical box is ticked, the Campaign tab becomes goal → recommended formats → inputs. Off restores the single shared vertical video (CG-11).",
r'''
flowchart TD
  A{"VerticalVideoLibraryV1 on for workspace and vertical ticked?"} -->|no| CG11["CG-11 Legacy shared vertical"]
  A -->|yes| G["1. What is this campaign for? (5 goals)"]
  G --> F["2. Videos this listing can support"]
  F --> E{"Any format eligible?"}
  E -->|none| VL03["VL-03 One shared vertical instead"]
  E -->|yes| R{"Any score 55 or more?"}
  R -->|no| VL04["VL-04 None recommended, pick one"]
  R -->|yes| VL02["VL-02 Up to 3 recommended"]
  VL02 -->|untick all| VL06["VL-06 Generate disabled"]
  VL02 -->|Swap for| VL05["VL-05 Swapped"]
  F --> U["Unlock more videos"]
  U -->|Story reveal| VL07["VL-07"]
  U -->|Exclusive access| VL08["VL-08"]
  U -->|Hosted footage| VL09["VL-09"]
  VL02 -->|Generate high-potential vertical videos| RES["VL-10 Library results"]
  RES --> VL11["VL-11 Approve or Request changes"]
  RES --> VL12["VL-12 Download ZIP"]
  RES --> VL13["VL-13 Performance numbers"]
''',
[
("VL-01", "Switch on, vertical ticked", "Open Campaign; pick each goal.", "Goals: Maximize reach, Generate buyer interest, Promote an open house, Announce a new listing, Announce a price change. The goal sets the marketing template (Quick tour, Feature spotlight, Open house, Just listed, Price drop) but not which videos are made.", "P1", "", ""),
("VL-02", "Listing with price, city, beds/baths, 4+ good photos", "Read the format cards.", "Each card: name, purpose, seconds, Recommended chip, strength and score out of 100, example hook, 'Opens on' photo, Why and 'Would score higher with' lists. Up to 3 formats at 55+ are recommended and pre-ticked; 'Use the recommended N videos' and 'Make every eligible video (N)' buttons.", "P1", "MediaAnalysis", ""),
("VL-03", "Listing missing what every format needs", "Open the planner.", "'This listing can't support a recommended video yet. The campaign will make one shared vertical video instead.'", "P2", "", ""),
("VL-04", "Eligible formats all under 55", "Open the planner.", "'None of the videos scored high enough to recommend. You can still pick one that's ready.'", "P3", "", ""),
("VL-05", "Format picked", "Swap for… another eligible format.", "The swap replaces it; ineligible cards say 'Not ready yet' and can't be ticked; 'Needs' lists what is missing (price, city and state, beds/baths, 3 or 4 usable photos, host footage, confirmed story or access line).", "P2", "", ""),
("VL-06", "Eligible formats, all unticked", "Look at Generate.", "Warning 'Pick at least one video, or use the recommended set.' Generate disabled. Estimate text otherwise; 'counts as one campaign… however many videos it makes'.", "P1", "", ""),
("VL-07", "Unlock more videos panel", "Story reveal: fill problem and result, confirm, Save; then Withdraw.", "Save disabled until both fields and the confirm box are set. Story reveal becomes eligible; withdrawing removes it. AI text mentioning a renovation is never used unconfirmed.", "P2", "", ""),
("VL-08", "Property video uploaded", "Exclusive access: enter 'Private showings this weekend only', optional end date, confirm, Save.", "Line shows with its end date; Exclusive access format needs it plus 10 s of processed video.", "P2", "", ""),
("VL-09", "Two property videos", "Tick 'shows me hosting' on one.", "Hosted walk-through needs 15 s of tagged host footage and approved narration; with none uploaded the panel links to the Media tab.", "P2", "", ""),
("VL-10", "Library campaign completed", "Open the Vertical video library section.", "One card per video with technical status, preview, post caption, Kling request (admins, Kling runs only). A video that couldn't be made doesn't stop the others.", "P1", "OpenAI", ""),
("VL-11", "Rendered library video", "Approve; then on another, Request changes with and without text.", "Approve records the review for that version. Request changes needs 'What should change?' filled in.", "P2", "OpenAI", ""),
("VL-12", "Library completed", "Download complete library (ZIP).", "ZIP holds every rendered video and its caption.", "P2", "OpenAI", ""),
("VL-13", "Library completed", "How these videos are doing: pick a video, add the post link, numbers, Save; then Import a CSV.", "Numbers save per post; CSV with platform, post_url, captured_at, views… imports rows. Turning the switch off hides the panel but keeps the numbers.", "P3", "", ""),
("VL-14", "Admin, /admin/features", "Turn VerticalVideoLibraryV1 on for one pilot workspace, then off globally.", "Pilot workspace sees the planner; others don't. Global off wins over the pilot list within 15 s, no deploy.", "P1", "", ""),
])

# ---------------------------------------------------------------- Kling pilots
section("KP", "Campaign: Kling 4 pilots (release switches Kling4FullTourV1, Kling4NativePresenterV1)",
"A Tour video strategy picker appears when either Kling switch is on. Pilot mode makes every video 15 s at 720p; live mode makes each its full length at 1080p.",
r'''
flowchart TD
  A{"Kling switch on?"} -->|no| KP01["KP-01 No strategy picker"]
  A -->|yes| S["Tour video strategy"]
  S -->|Established CLS edit| C["Normal campaign"]
  S -->|Kling 4 continuous Tour Map| KP02["KP-02 Kling tour, presenter only lip-synced bookends"]
  S -->|Kling 4 native presenter| N{"Library on and vertical picked?"}
  N -->|no, pilot mode| KP03["KP-03 Needs the vertical library"]
  N -->|yes| SC["KP-05 Script per video"]
  SC --> B{"Headshot, consent, scripts within word limit, no double quotes, Tour Map photo?"}
  B -->|no| KP04["KP-04 Before you can generate list"]
  B -->|yes| KP06["KP-06 Kling-only campaign hides voice and music"]
  KP06 --> KP08["KP-08 One Kling call per video, one at a time"]
  SC -->|admin| KP07["KP-07 Preview Kling request (free)"]
''',
[
("KP-01", "Both Kling switches off", "Open Campaign.", "No 'Tour video strategy' panel; every campaign uses the established edit.", "P1", "", ""),
("KP-02", "Kling4FullTourV1 on", "Pick Kling 4 continuous Tour Map; generate.", "Info: presenter only in the lip-synced opening and closing; longer or non-30 s outputs keep the established edit. Generation audit shows provider and request ID.", "P2", "Higgsfield", ""),
("KP-03", "Native presenter switch on, library off", "Pick Kling 4 native presenter.", "Warning that the pilot only works through the vertical video library (turn on VerticalVideoLibraryV1, pick vertical videos).", "P1", "", ""),
("KP-04", "Native presenter picked", "Leave out the headshot, consent, a script part; then exceed the word limit; then add a double quote.", "'Before you can generate:' lists each missing item (add headshot, tick consent, fill all three parts, shorten to N words, remove double quotes, place a Tour Map photo). Generate stays disabled until the list is empty.", "P1", "", ""),
("KP-05", "Library on, 2 formats picked, native presenter", "Read the presenter scripts.", "One script card per video (Opening, Tour voiceover, Closing) with its own seconds and word counter, and Use suggested script. Videos over 30 s are listed as established CLS edits.", "P1", "", ""),
("KP-06", "Every picked video is Kling, only vertical and still posts ticked", "Look at Creative direction.", "Info: Kling makes the speech and music, so voiceover, music, voice, style and walk-through motion are hidden; section reads 'Captions'.", "P2", "", ""),
("KP-07", "Platform admin, native presenter picked", "Preview Kling request (free).", "Shows ready/not ready, keyframes with photo links and the full Kling 4 request body per video. No paid call.", "P2", "", ""),
("KP-08", "Pilot campaign generated", "Watch progress and the library results.", "Each video gets its own Kling request, submitted after the previous one finishes; busy answers wait. 15 s 720p in pilot mode. Agent appears only in the opening shot, inside the first Tour Map photo's space; no static headshot card.", "P1", "Higgsfield", ""),
("KP-09", "Kling run finished but its download failed", "Retry.", "Paid request ID is kept and reused; no second paid video. Provider error replies are shown in the video's error.", "P2", "Higgsfield", ""),
("KP-10", "Finished native presenter campaign", "Start a new campaign.", "The presenter consent box shows again (consent is per campaign).", "P2", "", ""),
])

# ---------------------------------------------------------------- Run
section("CR", "Campaign: generate, progress, cancel, retry, billing",
"From pressing Generate to a finished, failed or cancelled campaign.",
r'''
flowchart TD
  A["Generate campaign"] --> B{"Same inputs as the last completed campaign?"}
  B -->|yes| CR07["CR-07 Shows that campaign again, no new run"]
  B -->|no| C{"Tester, active plan or trial with allowance?"}
  C -->|no| CR09["CR-09 Billing error message"]
  C -->|yes| D["CR-01 Queued then Running, stage list"]
  D -->|Cancel and start over| CR03["CR-03 Cancelled, outputs say Not made"]
  D -->|a stage fails| CR04["CR-04 Failed, Retry failed stage"]
  CR04 -->|Retry| D
  CR04 -->|Cancel and start over| CR03
  D -->|all stages done| CR06["CR-06 Completed, Generate updated campaign"]
  CR03 --> A
  CR06 --> A
''',
[
("CR-01", "Generate enabled", "Press Generate campaign.", "Status chip Queued then Running, current stage, progress bar, stage list with icons, Generation audit box (strategy, provider, request ID); refreshes on its own. 'Creating: <outputs>' replaces the settings form. 'Cancel and start over' button.", "P1", "OpenAI", ""),
("CR-02", "Campaign running", "Leave the page, come back.", "Progress continues where it is.", "P2", "OpenAI", ""),
("CR-03", "Campaign running or failed", "Cancel and start over.", "Button reads 'Cancelling…' and alert 'Cancellation requested. The current step will stop safely; no further retries will start.' Then status Cancelled; unfinished outputs read 'Not made: the campaign was cancelled.' (no spinner); settings form returns.", "P1", "OpenAI", "S-27"),
("CR-04", "Force a stage failure", "Read the error; Retry failed stage.", "Error alert (friendly text for master plan and narration errors); retry resumes from that stage; 'attempt 2' shown.", "P1", "OpenAI", "S-27"),
("CR-05", "One Tour Map photo with failed analysis", "Generate.", "Warning 'Analysis failed for 1 photo. Creative Listing Studio is retrying…'.", "P2", "OpenAI, MediaAnalysis", "S-28"),
("CR-06", "Campaign completed", "Look at the header button.", "Reads 'Generate updated campaign'; settings form is back.", "P1", "OpenAI", ""),
("CR-07", "Completed campaign, nothing changed since", "Generate updated campaign.", "Returns the same completed campaign; no new run and no allowance used.", "P2", "", "S-48"),
("CR-08", "Completed campaign", "Change one option (or a photo, or a property fact) and generate.", "A new campaign starts.", "P2", "OpenAI", ""),
("CR-09", "Free trial ended / plan inactive / monthly allowance used", "Generate.", "Error naming Billing: 'Your free campaign trial has ended…', 'Your subscription is not currently active…', or 'Your <Plan> plan's N campaigns for this month are used or reserved. Upgrade on Billing to continue.' No campaign created.", "P1", "", ""),
("CR-10", "User in Testers role", "Generate up to and past their monthly limit (default 10).", "Free until the limit; then a message to raise it under Admin › Find users › Testers, and admins get an alert. Video chat and Ask AI caps still apply.", "P2", "", ""),
("CR-11", "Paid plan, property already used a campaign this period", "Generate again for the same property.", "Allowed without using another campaign from the allowance.", "P2", "", ""),
("CR-12", "Campaign finishes while on another page", "Wait.", "Notification bell pops up; clicking opens the property Campaign tab.", "P3", "OpenAI", ""),
("CR-13", "A stage fails once, then succeeds on its own", "Watch the alert while it runs.", "Amber 'Previous attempt: <reason> Retrying automatically.' and 'attempt 2' on the stage; it clears when the stage passes.", "P2", "OpenAI", ""),
("CR-14", "Campaign stopped (failed or cancelled) before every output finished", "Look at the output cards.", "Unfinished cards read 'Not made: the campaign stopped before this was finished.' or '…was cancelled.' instead of spinning.", "P2", "", ""),
("CR-15", "Plan comparison on Billing", "Open /billing.", "Trial, Basic $99, Professional $299, Enterprise $499 (4, 12, 30 campaigns a month; yearly 20% off). Drone fly-in and AI presenter only on Professional and Enterprise. Stripe test checkout only where enabled.", "P2", "Stripe", ""),
])

# ---------------------------------------------------------------- Deliverables
section("CD", "Campaign: deliverables, still posts and captions",
"What the person gets at the end.",
r'''
flowchart LR
  A["Completed campaign"] --> V["Video cards: preview, Download MP4, chat"]
  A --> S["Still-image posts: Download, Copy caption, Share"]
  A --> C["Short and long social captions"]
  S -->|Share on phone| S1["CD-06 Share sheet"]
  S -->|browser can't share files| S2["CD-06 Caption copied"]
''',
[
("CD-01", "Completed campaign with 3 videos", "Look at each card.", "Name, seconds, aspect ratio and dimensions; preview plays; Download MP4 saves the file. Cards show a spinner until rendered.", "P1", "OpenAI", "Pairwise"),
("CD-02", "Vertical video", "Watch it.", "1080×1920, hook pills, beat cuts, spec line, name/phone strip, end card.", "P1", "OpenAI", "Pairwise"),
("CD-03", "YouTube property tour", "Watch it.", "1920×1080, about 60 s, Tour Map order, opening title, facts.", "P1", "OpenAI", "Pairwise"),
("CD-04", "Facebook Feed", "Watch it.", "About 30 s cut of the tour (first, last and best middle scenes).", "P2", "OpenAI", "Pairwise"),
("CD-05", "Full narrated tour", "Watch it.", "Length follows the whole accepted script or room narration.", "P2", "OpenAI", "Deliverable combos"),
("CD-06", "Still posts selected", "Download JPEG; Copy caption; Share on a phone and on desktop.", "Square 1080×1080 and portrait 1080×1350 JPEGs. Copy: 'Caption copied.' Share: phone share sheet, or 'caption was copied… attach the JPEG', or 'Sharing is unavailable…'.", "P1", "OpenAI", "S-33, S-34"),
("CD-07", "Completed campaign", "Scroll to captions.", "Short and long social caption shown.", "P2", "OpenAI", ""),
("CD-08", "Campaign with extra photos present", "Check every output.", "No extra photo appears anywhere.", "P1", "OpenAI", "S-24"),
("CD-09", "Library campaign completed", "Look under the stage list.", "Vertical video library section replaces the single vertical card (see VL-10 to VL-13).", "P2", "OpenAI", ""),
])

# ---------------------------------------------------------------- Video chat
section("VC", "Campaign: per-video chat and Ask AI",
"'Ask about or change this video' under each rendered video, and the header Ask AI window that routes to it.",
r'''
flowchart TD
  A["Ask about or change this video"] --> B{"Question or change?"}
  B -->|question| VC02["VC-02 Explanation only"]
  B -->|not allowed, e.g. change facts| VC04["VC-04 Explains why not"]
  B -->|change| C["VC-03 Proposed edit list"]
  C -->|Render these changes| D{"Free renders left?"}
  D -->|0| VC09["VC-09 Render disabled"]
  D -->|yes| E["Rendering"]
  E -->|ok| F["VC-05 New version"]
  E -->|failed| VC07["VC-07 Video unchanged, Try rendering again"]
  F --> G["VC-06 Did this fix it?"]
  F -->|Undo last change| VC08["VC-08 Previous version"]
''',
[
("VC-01", "Rendered video", "Press 'Ask about or change this video'.", "Panel opens: 'Version 1 · 5 free updates left', intro text and 4 suggestion chips.", "P1", "OpenAI", ""),
("VC-02", "VC-01", "Press 'How was this video made?'.", "Answer explains sources (story, music choice, photos); no Render button.", "P2", "OpenAI", ""),
("VC-03", "VC-01", "Type 'cut the garage shot'.", "Reply with a bulleted proposed change and 'Render these changes'. Nothing re-renders yet.", "P1", "OpenAI", ""),
("VC-04", "VC-01", "Ask to change the price or add a pool.", "Explains it can't change facts or invent content; points to property details.", "P2", "OpenAI", ""),
("VC-05", "Proposal ready", "Render these changes.", "'Rendering…' chip and 'Updating the video…' alert; card plays version 2; '4 free updates left'.", "P1", "OpenAI", "S-45"),
("VC-06", "Rendered change", "Answer No with a note; Change answer; answer Yes.", "Confirmation text updates; answer and note appear on /admin/audit Video chat change requests.", "P1", "OpenAI", "S-45"),
("VC-07", "Render fails", "Render.", "'The last update didn't work, so the video is unchanged.' Button 'Try rendering again'; render not counted.", "P2", "OpenAI", "S-46"),
("VC-08", "Version 2 exists", "Undo last change.", "Version 1 is back on the card.", "P1", "OpenAI", "S-45"),
("VC-09", "5 renders used", "Ask for another change.", "'no free updates left'; Render disabled.", "P2", "OpenAI", "S-46"),
("VC-10", "Old proposal after a newer render", "Look at the old message.", "'The video has changed since. Ask again to apply this.'", "P3", "OpenAI", ""),
("VC-11", "Completed campaign", "Open header Ask AI, ask 'make the TikTok music quieter'.", "Routes to that video's chat (embedded), same rules as VC-03 to VC-09.", "P2", "OpenAI", "S-47"),
("VC-12", "Rendered video", "One request per edit kind: swap shot, move shot, remove shot, remove text, change text, change music, music volume, remove voiceover, caption look, remove captions, change voice.", "Each renders only that change; timeline otherwise unchanged.", "P2", "OpenAI", ""),
])

FINDINGS = [
("Updated 7 Nov for main 58be71a (PRs #107 to #150)", "New areas: saved settings (SP), the vertical video library (VL) and the Kling 4 pilots (KP). Updated: drone fly-in plan gate, speed and opening sequence; caption looks A to H; four new music tracks; plan limits; Cancel and start over; 'Not made' outputs; story check notes; room-tag stop names; AI cleanup fixes."),
("Not merged yet: PR #151, seven TikTok campaign types", "Replaces the goal step with 'What kind of TikTok campaign?' (What $X buys, Guess the price, Wait till you see the room, Speed tour, Agent-hosted tour, Luxury showcase, Living in your town), adds Speed tour and Living in your town formats, and makes announcements an add-on. VL-01 and VL-02 change when it merges."),
("Fixed in PRs #105 and #106 (29 Oct)", "Everything-off crash (CX-07), bulk AI clutter (XP-13), old video names on Next step (OV-16), drone vs walkthrough switch (DI-13), empty stops (MT-20, MT-25), silent vertical warning (CS-06)."),
("Still open", "Localize agent card has no effect with the AI presenter alone (CA-07). Still-post-only campaigns not traced end to end (CG-06). The 29 Oct test matrix doesn't cover SP, VL or KP yet."),
]
