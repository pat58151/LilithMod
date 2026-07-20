# Where the dialogue lives

Both games ship Unity IL2CPP builds, so no dialogue is sitting in a readable file
on disk. This is a map of where the text and voice actually are, and which route
gets at them. Counts are from the current install.

---

## Game 1 - The NOexistenceN of Lilith (the desktop pet, the one being modded)

`D:\SteamLibrary\steamapps\common\The NOexistenceN of Lilith`

### The dialogue itself: in the IL2CPP runtime, not on disk

Dialogue is held as `DialogueNode` objects in databases reachable from
`DialogueManager.s_instance`. There is no JSON or table on disk to open - it has
to be read from a running game.

A `DialogueNode` carries:

```
id, lineId, speaker, text, emotion, duration,
baseWeight, triggerTypes[], actionType,
nextStateType, nextStateDuration, soundId,
conditions { timeRangeStart, timeRangeEnd, ... }
```

Class layout is documented in `reference/game-api.txt` (dumped signatures for
`DialogueManager`, `DialogueNode`, `DialogueBubbleUI`, and friends).

### Route in: this mod's dumper

`LilithMod/DumpDatabaseBehaviour.cs` walks those databases on startup and writes
them to:

```
BepInEx\plugins\LilithMod\dump\
    dialogue_nodes_<Database>.json     25 files
    player_lines.json
```

Last run: **1347 nodes across 25 databases, 255 player lines.**

The big one is `dialogue_nodes_DialogueNode.json` (1111 nodes). The rest are
scene- or event-scoped and named accordingly - `NightHeartTalk` (62),
`莉莉丝的一天` (95), `MorningWake`, `SleepNoisy`, `TouchMore`, `Alarm`,
`NeglectComeback`, `PlayerDisappearComeback`, and so on. The database name is
the best clue to when a line fires.

`text` in these dumps is **Chinese**. Other languages arrive through the
localization tables below, keyed by `lineId`.

### Localized display text: Addressable bundles

```
Lilith_Data\StreamingAssets\aa\catalog.json
Lilith_Data\StreamingAssets\aa\StandaloneWindows64\
    localization-string-tables-chinese(simplified)(zh-cn)_assets_all.bundle
    localization-string-tables-chinese(traditional,hongkongsarchina)(zh-hk)_assets_all.bundle
    localization-string-tables-english(en)_assets_all.bundle
    localization-string-tables-japanese(japan)(ja-jp)_assets_all.bundle
    localization-locales_assets_all.bundle
    localization-assets-shared_assets_all.bundle
```

Unity Localization string tables. Four locales: zh-CN, zh-HK, en, ja-JP.

### Ready-made text inventories (from the other mod)

The other Lilith mod (`LilithTextInjector`) had already extracted the lines into
TSVs. Copies are preserved here, and they are by far the fastest way to read the
script without running anything:

```
D:\Lilith\backup-preinstall-20260720-1508\
    dialogue-lines-detected-ja-1.tsv      1808 lines   line_id, sound_id, text   (Japanese)
    dialogue-lines-zh-CN.tsv              1731 lines   line_id, sound_id, text   (Simplified)
    dialogue-lines-zh-HK.tsv              1812 lines   line_id, sound_id, text   (Traditional)
    native-dialogue-inventory.tsv         1342 rows    node_id, line_id, speaker, emotion,
                                                       action, sound_id, voice_status, text (English)
    native-dialogue-missing-voice.tsv                  lines with no voice clip
    unvoiced-native-lines.tsv                5 rows    node_id, line_id, speaker, emotion, action, text
```

`sound_id` is how a line maps to audio, and its shape differs by language:
`ja/LillthSayHello_2` for Japanese, `100002_cn` for Chinese.

### Voice audio

Packed in the standard Unity asset files - there is no loose audio folder:

```
Lilith_Data\sharedassets0.assets        100.6 MB
Lilith_Data\sharedassets0.assets.resS   271.7 MB   <- the bulk of the voice payload
Lilith_Data\resources.assets             11.6 MB
Lilith_Data\resources.resource           39.1 MB
```

Reference clips already pulled out for voice cloning:

```
BepInEx\plugins\LilithMod\voice\        calm / excited / sleepy / wronged
BepInEx\plugins\LilithMod\voice\jp\     the Japanese set, used by the mod
BepInEx\plugins\LilithMod\voice\reactions\
```

Transcripts for those: `D:\Lilith\voice-data\reference-transcripts.json`.

### Other text worth knowing about

```
Lilith_Data\StreamingAssets\Credits\credits.{en,ja,zh-CN,zh-HK}.json
Lilith_Data\StreamingAssets\Credits\backers.json
Lilith_Data\StreamingAssets\Notes\
Lilith_Data\StreamingAssets\Music\
```

**`Data/SenWords`** has been referred to as a word filter but a recursive search
of the game folder finds nothing by that name. It is either inside an asset file
or the name is wrong. Unresolved.

---

## Game 2 - The NOexistenceN of you AND me (the visual novel)

`D:\SteamLibrary\steamapps\common\The NOexistenceN of you AND me`

Used only as a **voice source** for fine-tuning. Its dialogue text has never been
extracted as such - see the caveat below.

### Layout

```
TheNOexistenceNofyouANDme_Data\data.unity3d        418.4 MB   scenes + assets, incl. text
TheNOexistenceNofyouANDme_Data\resources.resource  2142   MB   the audio payload
DLC\Lilith Cursor\                                            cursor DLC, no dialogue
```

No `StreamingAssets`. Everything is inside `data.unity3d` and
`resources.resource`, so UnityPy (or an equivalent) is the only way in.

### Voice, and the useful quirk

Voice clips were extracted with UnityPy (FMOD FSB5 inside AudioClip assets).

**The AudioClip asset names contain the Japanese dialogue line, truncated.** That
is where `manifest.tsv`'s `name_truncated` column comes from, and it is the
closest thing to a script dump this game has offered so far. Full text came from
transcribing the audio, not from the game.

Extraction output:

```
D:\Lilith\voice-data\ja\
    clip_00000.wav … clip_00751.wav     752 clips, ~61 min
    manifest.tsv          752 rows   file, seconds, name_truncated  (from the asset name)
    lilith.list           748 rows   path|speaker|lang|text          (GPT-SoVITS format)
    lilith-clean2.list    716 rows   after dropping noisy clips - this is what trained the model
    lilith-review.tsv                 review pass
```

`lilith.list` text is **Faster-Whisper output**, not game data. It is good enough
for voice training and should not be treated as an authoritative script.

### Caveat

Game 2's dialogue text has **not** been extracted. What exists is (a) truncated
asset names and (b) machine transcription. If a real script is ever needed, the
route is `data.unity3d` via UnityPy, which nobody has done yet.

---

## Licensing

Everything above is the developers' content. `voice-data/`, `training/` and the
`backup*/` folders are gitignored for that reason: extracted audio, the
fine-tuned model derived from it, and the TSV script inventories must not be
committed or redistributed. Local use only.
