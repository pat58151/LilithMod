# LilithMod

An unofficial expansion for *The NOexistenceN of Lilith*.

It turns Lilith into a persistent companion who can listen, speak, remember past
conversations, react to your actions, and occasionally reach out on her own.

This is more than a chat window. The mod connects conversation, voice, memory,
game state, music, desktop awareness, weather, web search, and the in-game inbox
into one system.

![Lilith in game](image/ui1.png)

> Unofficial and fan-made. Not affiliated with or endorsed by the game's
> developers or publishers. See the [Disclaimer](DISCLAIMER.md).

## Talk naturally

| | |
|---|---|
| **Free chat** | Press F7 and say anything. Lilith responds in character, with subtitles matching your game language. |
| **Local voice recognition** | Press F8 and speak instead of typing. Speech recognition runs entirely on your computer. Your microphone audio is not uploaded. |
| **Hands-free wake word** | Say Lilith's name and she begins listening. No hotkey is required. |
| **Multilingual conversations** | Talk in English, Japanese, or Chinese. Switch languages without losing the context of the conversation. |

![Chatting with F7](image/f7.png)
![Speaking with F8](image/f8.png)

## Give your Lilith a voice

| | |
|---|---|
| **Spoken dialogue** | Lilith can speak her responses aloud while subtitles appear in-game. |
| **A voice chosen by you** | No generated voice is bundled with the mod. Choose or train the voice she uses, making your installation personal. |
| **Original voice support** | The game's original voice remains available and is not replaced. |

Voice output is optional. The complete text-chat experience works without it.

## A memory that continues across sessions

| | |
|---|---|
| **Conversation history** | Lilith remembers recent discussions instead of treating every message as a new encounter. |
| **Long-term memories** | Important moments can be preserved and recalled much later. |
| **Cross-language recall** | A memory created in one supported language can still be recognised when you mention it in another. |
| **Contextual recall** | Familiar names, interests, events, and subjects can naturally return in later conversations. |

Her memory is stored locally on your machine.

## Aware of her world

Lilith's responses are influenced by what is happening around her:

- The current time of day
- Whether she is active, resting, or asleep
- Your interactions and touches
- The game or application currently in the foreground
- The music you chose to play

She can recognise the name of the active game or application, but she does not
read window contents, documents, messages, or browser pages.

## More than reactive dialogue

| | |
|---|---|
| **Spontaneous conversations** | Lilith can decide to speak first. What she says is created for the current moment rather than selected from a fixed list. |
| **Handwritten notes** | After enough meaningful interaction, she may leave a personal note in the in-game inbox. |
| **Music interaction** | Play a track from the music folder and Lilith knows that you selected it. The mod also adds a separate music-volume control. |
| **Weather information** | Ask about the weather and she can retrieve current information. |
| **Web search** | She can look up recent information when a conversation requires it. |

## Quality of life

| | |
|---|---|
| **Opacity adjustment** | Set Lilith's opacity to your preferred level so she does not obstruct your game or work. |
| **Music volume adjustment** | Adjust the volume of music opened through *Put on some music*. |

## Privacy

- Voice recognition runs locally.
- Microphone audio is not uploaded.
- Memories remain on your computer.
- Generated notes remain on your computer.
- Custom voice files remain on your computer.
- Desktop awareness reads only the active application name, not its contents.

Text you send in chat is processed through an AI provider using your own API key,
or through your own local AI. Point her at a local AI and no part of the
conversation leaves your machine.

## Requirements

- *The NOexistenceN of Lilith* v1.0.1
- Windows
- An AI key for generated conversations

Voice recognition, wake-word detection, and spoken responses are optional. Without
them, Lilith still supports full text conversations, memory, awareness, notes, and
the other integrated features.

## Download and setup

The mod is free and distributed through a single installer.

1. Download the latest `LilithMod-Setup-<version>.exe` from the
   [Releases](https://github.com/pat58151/LilithMod/releases) page.
2. Run it and point it at your game folder. It installs the mod and, if you tick
   the boxes, the local voice and speech components.
3. Launch the game through Steam.

The mod only adds its own files and reads the game's. The game returns to normal
when you uninstall.

The **[Setup guide](SETUP.md)** covers connecting an AI (hosted or local), her
voice, speaking to her, and troubleshooting, in the order most people want them.

## Documentation

- **[Setup](SETUP.md)** get everything running.
- **[Design techniques](TECHNIQUES.md)** the systems behind her, in more depth.
- **[Disclaimer](DISCLAIMER.md)** what this is and is not.
- **[License](LICENSE)** MIT, code only. Game assets and any voice model you add
  are not covered.

---

*LilithMod is an unofficial fan-made project and is not affiliated with the
original developer or publisher of The NOexistenceN of Lilith.*
