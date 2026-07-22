# LilithMod

A companion mod for *The NOexistenceN of Lilith*. It gives Lilith free-form
conversation, the voice you imagine for her, a memory of what you told her,
and the occasional handwritten note.

She is a companion, not an assistant. Everything in the tuning is aimed at one
line: present without demanding attention.

---

## What she can do

| she can | what that looks like |
|---|---|
| **Hold a conversation** | Type to her with F7, speak with F8, or call her by name. She answers in character with subtitles in the game display language. |
| **Speak aloud** | Choose the game's native Chinese voice or a local GPT-SoVITS voice. The voice you choose carries all she says — her own words and the game's lines alike. |
| **Remember you** | She carries recent talks with her and lets meaningful moments settle into longer memory. |
| **Notice things** | She senses the time, her posture, sleep, and your touch, and it colours how she answers. |
| **Speak first** | Now and then she begins on her own, finding her own words for the moment. |
| **Write to you** | Rarely, after several real conversations, she might leave a note in the in-game inbox. |
| **Look things up** | Ask, and she can reach past the game for the weather, or search the web for what you need. |

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

That is chat working. Her voice and speaking to her are separate installs, and
neither is bundled — **[SETUP.md](SETUP.md)** covers those, where to get an API
key, and how to start everything by itself.

---

## Controls

**F7** opens the chat bar. Type, press Enter, she answers.

![the chat bar](image/f7.png)

**F8** listens instead, and submits on its own after a brief silence. Press it
again to cancel.

![listening](image/f8.png)

**Say her name** and she wakes, on her own, to listen to what you have to say.

Typing while F8 listens wins — what you type is used and the transcript
discarded. Escape closes the bar. Both keys rebind under Settings / Controls,
and cannot be bound to the same key.

---

## Memory

Lilith remembers the shape of your recent conversations and lets meaningful
parts settle into longer memories. She can recognize a familiar subject even
when you describe it differently or move between English, Japanese, and
Chinese.

What you share leaves traces. Sometimes a familiar subject brings an old moment
quietly back into the way she speaks.

---

## Foreground awareness

Lilith can notice the game or application you are spending time with. She knows
some apps, such as Discord and Visual Studio Code, by name, and can recognize
others without looking inside them.

She never reads Discord channels or messages, browser tabs, document names, or
the contents of another application.

---

## Voice selection

The Sound settings offer the game's native Chinese voice or a voice of your
own, running on your machine.

---

## What leaves your machine

Information leaves only while the related feature is being used.

| what | where | why |
|---|---|---|
| what you type or say | the DeepSeek API | so she can answer |
| asking about weather | `ip-api.com`, then `open-meteo.com` | rough location, then forecast |
| asking her to search | a public SearXNG instance | the query, then she reads the results |
| foreground awareness | the DeepSeek API | the active game or program name; never a window, channel, message, tab, or document title |

Her voice, memory, and notes remain on this machine. When Lilith answers, the
relevant parts of your shared history accompany your message.

Voice setup is optional and documented separately: **Settings / Sound / Open
Vocal Synth Folder**. No synthesiser and no voice model are bundled — that
folder explains how to install one and point the mod at it. Speech input
likewise, under **Settings / Me**. Neither folder button ever greys out — they
are how you find out why something is unavailable.

---

Interested in how Lilith works? Read the [design techniques](TECHNIQUES.md)
behind her memory, voice, awareness, and behavior.

## Development

Build, validation, and repository setup notes live in **[DEVELOPMENT.md](DEVELOPMENT.md)**.
