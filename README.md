# LilithMod

An unofficial expansion for *The NOexistenceN of Lilith*.

It turns Lilith into a persistent companion who can listen, speak, remember
past conversations, react to your actions, and occasionally reach out on her
own.

This is more than a chat window. The mod connects conversation, voice, memory,
game state, music, desktop awareness, weather, web search, and the in-game
inbox into one system.

---

## Talk naturally

- **Free chat.** Press F7 and say anything. Lilith responds in character, with
  subtitles matching your game language.
- **Local voice recognition.** Press F8 and speak instead of typing. Speech
  recognition runs entirely on your computer. Your microphone audio is not
  uploaded.
- **Hands-free wake word.** Say Lilith's name and she begins listening. No
  hotkey is required.
- **Multilingual conversations.** Talk in English, Japanese, or Chinese. You
  can switch languages without losing the context of the conversation.

![the chat bar](image/f7.png)

![listening](image/f8.png)

Typing while F8 listens wins: what you type is used and the transcript
discarded. Escape closes the bar. Both keys rebind under Settings / Controls,
and cannot be bound to the same key.

---

## Give your Lilith a voice

- **Spoken dialogue.** Lilith can speak her responses aloud while subtitles
  appear in-game.
- **A voice chosen by you.** No voice model is bundled with the mod. Choose or
  train the voice she uses, making your installation personal.
- **Original voice support.** The game's original voice remains available and
  is not replaced.

Voice output is optional. The complete text-chat experience works without it.

---

## A memory that continues across sessions

- **Conversation history.** Lilith remembers recent discussions instead of
  treating every message as a new encounter.
- **Long-term memories.** Important moments can be preserved and recalled much
  later.
- **Cross-language recall.** A memory created in one supported language can
  still be recognised when you mention it in another.
- **Contextual recall.** Familiar names, interests, events, and subjects can
  naturally return in later conversations.

Her memory is stored locally on your machine.

---

## Aware of her world

Lilith's responses are influenced by what is happening around her.

- The current time of day
- Whether she is active, resting, or asleep
- Your interactions and touches
- The game or application currently in the foreground
- The music you chose to play

She can recognise the name of the active game or application, but she does not
read window contents, documents, messages, or browser pages.

---

## More than reactive dialogue

- **Spontaneous conversations.** Lilith can decide to speak first, in her own
  words, fitted to the current moment rather than selected from a fixed list.
- **Handwritten notes.** After enough meaningful interaction, she may leave a
  personal note in the in-game inbox.
- **Music interaction.** Play a track from the music folder and Lilith knows
  that you selected it. The mod also adds a separate music-volume control.
- **Weather information.** Ask about the weather and she can retrieve current
  information.
- **Web search.** She can search for up-to-date information when a
  conversation requires it.

---

## Privacy

- Voice recognition runs locally. Microphone audio is not uploaded.
- Memories, notes, and custom voice files remain on your computer.
- Desktop awareness reads only the active application name, not its contents.

Information leaves your machine only while the related feature is being used.

| what | where | why |
|---|---|---|
| what you type or say | the DeepSeek API | so she can answer |
| asking about weather | `ip-api.com`, then `open-meteo.com` | rough location, then forecast |
| asking her to search | a public SearXNG instance | the query, then she reads the results |
| foreground awareness | the DeepSeek API | the active game or program name; never a window, channel, message, tab, or document title |

---

## Requirements

- *The NOexistenceN of Lilith* v1.0.1
- Windows, 64-bit
- A DeepSeek API key you supply. DeepSeek is prepaid and generally inexpensive
  for normal use.

Chat asks for almost nothing beyond the game. The local voice and speech
features are what want hardware, and both fall back to the CPU when there is
no GPU.

The models stay resident for the whole session, not only while she speaks or
listens. On the machine she was built on, an i5-14400F with 32 GB of RAM and a
Radeon RX 9060 XT 16 GB, the full stack (game, voice server, and speech
listener running whisper-large-v3-turbo) holds roughly 7 to 8 GB of VRAM and
about 8 GB of RAM the whole time she is up. Budget an 8 GB card and 16 GB of
system RAM for everything on the GPU; smaller setups run one feature on the
GPU and the rest on the CPU. NVIDIA works through CUDA, AMD through a ROCm
build of PyTorch, and with neither, everything falls back to the CPU.

Voice recognition, wake-word detection, and spoken responses are optional.
Without them, Lilith still supports full text conversations, memory,
awareness, notes, and the other integrated features.

---

## Install

Download the latest release and run `LilithMod-Setup-<version>.exe`. It finds
the game through Steam, installs BepInEx, and sets the one Doorstop flag that
Steam otherwise silently breaks.

Each release names the game version it was built against, currently
*The NOexistenceN of Lilith* v1.0.1. A game update may break the mod until a
release catches up.

Then, in game: **Settings / Me / DeepSeek API Key**. Paste a key. Without one,
F7 and F8 do nothing by design.

![Settings / Me](image/ui1.png)

The key lives in `BepInEx\config\LilithMod.cfg` on your machine and goes
nowhere except the API you configured. It is never logged and never committed.

**First launch is slow.** BepInEx generates its interop assemblies from the
game. Do not force-quit it; that can break the next launch too.

**If the game looks unmodded**, fully exit both the game and Steam, restart
Steam, and launch again. Starting `Lilith.exe` directly while Steam is closed
poisons the environment for every later launch.

Her voice and speaking to her are separate installs, and neither is bundled.
**[SETUP.md](SETUP.md)** covers those, where to get an API key, and how to
start everything by itself.

---

Interested in how Lilith works? Read the [design techniques](TECHNIQUES.md)
behind her memory, voice, awareness, and behavior.

*LilithMod is an unofficial fan-made project and is not affiliated with the
original developer or publisher of The NOexistenceN of Lilith. See
[DISCLAIMER.md](DISCLAIMER.md).*

## Development

Build, validation, and repository setup notes live in **[DEVELOPMENT.md](DEVELOPMENT.md)**.
