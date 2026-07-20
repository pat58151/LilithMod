"""
verify-bilingual.py - checks the bilingual voice feature: Japanese audio with
English subtitles, advanced per sentence, with synthesis of the next sentence
overlapping playback of the current one.

Read-only. Builds the mod, then asserts the load-bearing pieces are present.
Follows the pattern of verify-voice.py.
"""

import os
import subprocess
import sys

ROOT = os.path.dirname(os.path.abspath(__file__))
MOD_DIR = os.path.join(ROOT, "LilithMod")
DOTNET = r"C:\Program Files\dotnet\dotnet.exe"

failures = []


def check(condition, message):
    if not condition:
        failures.append(message)


# -- 1. Build -----------------------------------------------------------------
proj = os.path.join(MOD_DIR, "LilithMod.csproj")
r = subprocess.run(
    [DOTNET, "build", proj, "-c", "Release"],
    capture_output=True, text=True, cwd=MOD_DIR,
)
if r.returncode != 0:
    print("VERIFY FAIL - dotnet build returned", r.returncode)
    for line in (r.stdout + r.stderr).splitlines():
        if ": error" in line:
            print("  ", line.strip())
    sys.exit(1)
print("[verify-bilingual] Build succeeded")


def read(*parts):
    path = os.path.join(*parts)
    if not os.path.exists(path):
        return ""
    with open(path, "r", encoding="utf-8", errors="ignore") as f:
        return f.read()


utterance = read(MOD_DIR, "Utterance.cs")
speech = read(MOD_DIR, "SpeechQueueProcessor.cs")
chat = read(MOD_DIR, "LlmChatController.cs")
plugin = read(MOD_DIR, "LilithModPlugin.cs")

# -- 2. The sentence pair type ------------------------------------------------
check(utterance, "LilithMod/Utterance.cs is missing")
check("JaText" in utterance and "EnText" in utterance,
      "Utterance must carry both JaText and EnText")

# -- 3. Per-sentence queue and cross-thread hand-off ---------------------------
check("ConcurrentQueue<Utterance>" in speech,
      "SpeechQueueProcessor must queue Utterance, not raw strings")
check("SubtitleQueue" in speech and "SubtitleQueue" in chat,
      "A SubtitleQueue must carry subtitles from the voice thread to the main thread")
check("VoiceFailureQueue" in speech and "VoiceFailureQueue" in chat,
      "Synthesis failure must be signalled on its own queue, not as a sentinel string "
      "inside the subtitle queue")
check("CancelCurrent" in speech and "CancelCurrent" in chat,
      "A new reply must abandon the previous reply's queued sentences")

# -- 4. The overlap, which is the whole point of the change -------------------
# Synthesis of the next sentence must be started BEFORE PlaySync blocks on the
# current one; if it is started after, there is no overlap at all.
check("Task.Run" in speech,
      "The next sentence must be synthesised on a task so it overlaps playback")
if "Task.Run" in speech and "PlaySync" in speech:
    check(speech.index("Task.Run") < speech.rindex("PlaySync"),
          "Synthesis of the next sentence must start BEFORE PlaySync, otherwise "
          "nothing overlaps")

# -- 5. Bubble refresh uses the path the game actually reacts to --------------
check("StartDialogue(9500000)" in chat,
      "Subtitles must be shown via StartDialogue; assigning node.text alone does "
      "not refresh a dialogue already on screen")

# -- 6. Reply parsing and its fallbacks ---------------------------------------
check("ParseUtterances" in chat, "A tolerant reply parser is required")
check("MaxUtterancesPerReply" in chat, "The sentence count must be capped")
check("response_format" in chat,
      "The request should ask for JSON mode to suppress markdown fences")

# -- 7. Prompt: bilingual contract, without losing her measured voice ---------
check('BilingualSystemPrompt' in plugin,
      "The prompt must bind under a new config key, or an existing cfg keeps the "
      "old prompt and the feature silently never happens")
for marker, why in [
    ('\\"ja\\"', "prompt must define the ja field"),
    ('\\"en\\"', "prompt must define the en field"),
    ("tulpamancy", "lore: created through tulpamancy"),
    ("awareness", "lore: she resides in the player's awareness"),
    ("STAGE DIRECTIONS", "no asterisk actions"),
    ("ellipses", "ellipsis-heavy voice"),
    ("リリス", "third-person self-reference"),
]:
    check(marker in plugin, f"Prompt lost a load-bearing rule - {why}")

check("desktop" in plugin and "screen" in plugin,
      "Prompt must still forbid desktop-pet / on-screen framing")

# -- 8. Output artifacts ------------------------------------------------------
out_dir = (r"D:\SteamLibrary\steamapps\common\The NOexistenceN of Lilith"
           r"\BepInEx\plugins\LilithMod")
check(os.path.exists(os.path.join(out_dir, "LilithMod.dll")),
      "LilithMod.dll not found after build")
check(any("NAudio" in n for n in (os.listdir(out_dir) if os.path.isdir(out_dir) else [])),
      "No NAudio assembly beside the plugin - playback would fail at runtime")

# -- Result -------------------------------------------------------------------
if failures:
    print("VERIFY FAIL")
    for f in failures:
        print("  -", f)
    sys.exit(1)

print("[verify-bilingual] All checks passed")
print("VERIFY PASS")
