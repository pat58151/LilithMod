# Intent: speak Lilith's LLM replies aloud

Lilith already answers in text via `LlmChatController`. A fine-tuned GPT-SoVITS
model and a local HTTP service now exist, but nothing in the mod calls them, so
she is still silent. Wire the two together: when a reply arrives, synthesize it
and play it, without disturbing the text chat that already works.

The service is an OpenAI-shaped local endpoint: `POST http://127.0.0.1:9880/tts`
returning WAV bytes. Playback should use **NAudio** (managed .NET, off the Unity
thread) rather than Unity `AudioSource` - decoding raw WAV into an IL2CPP
`AudioClip` is far more painful and the other mod reached the same conclusion.

Two behaviours measured today must be designed in, not discovered later:

- **The first synthesis after service start is ~11x realtime** while ROCm compiles
  kernels, settling to ~0.45x after roughly ten calls. Warm the service in the
  background at startup so the first real reply is not the slow one.
- The service must be reached with the tuned parameters, not library defaults:
  `text_split_method: "cut5"`, `fragment_interval: 0.4`, `media_type: "wav"`,
  `streaming_mode: false`, and a `prompt_text` matching the chosen reference clip.
  Defaults insert audible pauses mid-sentence.

Voice is an enhancement and must never be able to break chat: any failure, timeout,
or absent service degrades silently to text-only.

## Definition of done

- A reply that arrives in `HandleChatResult` is spoken aloud, and the text still
  appears exactly as it does today.
- With the TTS service stopped, chat continues to work normally and the log records
  one warning rather than an exception or a hang.
- Voice is configurable: enable/disable, endpoint, reference clip, `prompt_text`,
  and timeout all live in `BepInEx/config/LilithMod.cfg`.
- Synthesis and playback occur off the Unity main thread; no new per-frame work is
  added to `Update()` beyond draining an existing-style queue.
