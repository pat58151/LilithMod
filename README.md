# LilithMod

A companion mod for *The NOexistenceN of Lilith*. It gives Lilith free-form
conversation, a local Japanese voice, a memory of what you told her, and the
occasional handwritten note.

She is a companion, not an assistant. Everything in the tuning is aimed at one
line: present without demanding attention.

---

## What it does

- **Talk to her in free text** — F7 to type, F8 to speak. She answers in
  character, in English subtitles over Japanese speech.
- **Local voice** — a GPT-SoVITS model on your own machine speaks her replies,
  and optionally replaces the game's own dialogue audio too.
- **Memory** — she reads back recent conversations before replying, so she can
  refer to what you said earlier.
- **Ambient life** — she comments unprompted now and then, reacts to being
  touched, and knows the time of day and whether she is awake.
- **Notes and letters** — rarely, after several real conversations, she leaves
  something in the in-game inbox.
- **Live information** — weather and web search, when asked.

Chat needs an API key you supply. Voice and speech input are optional; without
them she still works, silently and typed-only, and the settings rows that need
them grey out rather than failing.

---

## Install

Download the latest release and run `LilithMod-Setup-<version>.exe`. It finds
the game through Steam, installs BepInEx, and sets the one Doorstop flag that
Steam otherwise silently breaks.

Then, in game: **Settings / Me / DeepSeek API Key**. Paste a key. Without one,
F7 and F8 do nothing by design.

![Settings / Me](image/ui1.png)

The key lives in `BepInEx\config\LilithMod.cfg` on your machine and goes
nowhere except the API you configured. It is never logged and never committed.

**First launch is slow** — BepInEx generates its interop assemblies from the
game. Do not force-quit it; that can break the next launch too.

**If the game looks unmodded**, fully exit both the game and Steam, restart
Steam, and launch again. Starting `Lilith.exe` directly while Steam is closed
poisons the environment for every later launch.

---

## Controls

**F7** opens the chat bar. Type, press Enter, she answers.

![the chat bar](image/f7.png)

**F8** listens instead, and submits on its own after about 2.5 seconds of
silence. Press it again to cancel.

![listening](image/f8.png)

Typing while F8 listens wins — what you type is used and the transcript
discarded. Escape closes the bar. Both keys rebind under Settings / Controls,
and cannot be bound to the same key.

---

## What leaves your machine

Only three things, and only when you cause them.

| what | where | why |
|---|---|---|
| what you type or say | the DeepSeek API | so she can answer |
| asking about weather | `ip-api.com`, then `open-meteo.com` | rough location, then forecast |
| asking her to search | a public SearXNG instance | the query, then she reads the results |

Her voice, her memory and her notes never leave this machine.

`ip-api.com` is contacted over plain HTTP. To skip that lookup entirely, set
your own coordinates in `LilithMod.cfg`:

```ini
[Weather]
Latitude = 51.5074
Longitude = -0.1278
LocationName = London
```

With both set it is never contacted at all. This is also the fix when a VPN
gives her the wrong country's weather.

Voice setup is optional and documented separately: **Settings / Sound / Open
Synth Voice Folder**. Speech input likewise, under **Settings / Me**. Neither
folder button ever greys out — they are how you find out why something is
unavailable.

---

## Building

Requires the .NET SDK and a copy of the game for its interop assemblies.

```powershell
powershell -ExecutionPolicy Bypass -File reapply-mod.ps1     # build + deploy to the live plugin folder
python verify-bilingual.py                                    # the assertion suite
powershell -ExecutionPolicy Bypass -File runtime\package-mod.ps1   # release zip into dist\
powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1
```

`reapply-mod.ps1` writes into the installed plugin folder, so **close the game
first**. `verify-bilingual.py` builds to `build-test\` instead and can run
while the game is up.

Release builds pass `-p:IncludeDialogueCatalog=false`. A release DLL is ~210 KB
and a local one ~420 KB — the quickest check that the game's own script did not
ship. Without the catalogue the mod does not touch native dialogue at all; her
own replies are unaffected.

---

## Before changing behaviour

Two rules are load-bearing, and the code defers to them in more places than is
obvious:

- **Nothing interrupts a line she is already speaking.** Four separate paths
  wait on one predicate for this, and the verify suite asserts that at least
  four call sites do.
- **She never rejects affection.** Not deflected, not joked away, not
  qualified. This is in the persona prompt in deliberately absolute language.

Almost every timing constant in the mod was arrived at by watching real
sessions and being wrong first. The comments in the code carry that reasoning;
they are worth reading before changing a number.

The longer development notes — timing measurements, the portability audit, the
installer record, solved problems — are kept in `document\`, which is local
only and not published.

---

## Content boundary

The game's dialogue, its audio, and the voice model fine-tuned from that audio
are the developers' content. They are gitignored, excluded from the release
package, and are for local use only. Do not commit or redistribute them.

The mod ships no game assets. It reads what is already installed.
