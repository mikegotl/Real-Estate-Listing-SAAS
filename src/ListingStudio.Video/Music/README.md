# Campaign music library

Built-in music for campaign videos (Campaign tab, Sound section). The renderer copies a track next to the render, loops
it and trims it to the video's length. Every track starts on beat one so short-form beat cuts land on the drums; the
tempos must match `CampaignCreativeDirector.BeatsPerMinute`.

| File | Campaign tab name | Asset id | Tempo |
| --- | --- | --- | --- |
| `upbeat-pop.mp3` | Upbeat pop | `listing-studio:upbeat-lifestyle:v1` | 118 BPM |
| `modern-chill.mp3` | Modern chill | `listing-studio:modern-ambient:v1` | 96 BPM |
| `warm-acoustic.mp3` | Warm acoustic | `listing-studio:heritage-warm:v1` | 84 BPM |
| `luxury-cinematic.mp3` | Luxury cinematic | `listing-studio:luxury-cinematic:v1` | 72 BPM |

The asset ids predate these recordings (they named the earlier synthesized tracks) and are kept so saved campaigns
still resolve.

## How they were made and licensing

All four are original compositions written for Creative Listing Studio in `tools/campaign-music/compose.py`:

- Melodic parts (piano, guitar, electric piano, bass, strings) are MIDI rendered with FluidSynth through the
  MuseScore General soundfont (MIT; its piano is public domain and its strings are CC0 VSCO samples).
- Drums and percussion are one-shot samples from the Sonic Pi sample pack (CC0, from freesound.org, or MIT).

No third-party recordings or compositions are included, so the files can be used in customer videos and kept in this
public repository. To regenerate, install `fluidsynth musescore-general-soundfont sonic-pi-samples` (Ubuntu) and
`pip install pretty_midi numpy scipy soundfile`, then run `python3 tools/campaign-music/compose.py out/`.
