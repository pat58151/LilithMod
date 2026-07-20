**Implementation Plan: GPT‑SoVITS Text‑to‑Speech Integration for Lilith**

---

### 1. Overview
Augment the existing `LlmChatController` so that every non‑empty LLM reply is spoken aloud using a local GPT‑SoVITS HTTP service.  Synthesis and playback run **completely off the Unity main thread**; no new per‑frame work is added to `Update()` beyond any existing queuing mechanism.  Any failure (service down, timeout, malformed response) degrades silently to text‑only while logging at most one warning per episode.

---

### 2. Configuration (`BepInEx/config/LilithMod.cfg`)

Add a `[TTS]` section with the following keys (all optional with sensible defaults):

| Key                | Type   | Default                         | Description |
|--------------------|--------|----------------------------------|-------------|
| `Enabled`          | bool   | `false`                          | Master switch. |
| `Endpoint`         | string | `http://127.0.0.1:9880/tts`      | URL of the GPT‑SoVITS service. |
| `ReferenceAudioPath` | string | `""`                           | Path to the reference WAV file (relative to mod directory or absolute). |
| `PromptText`       | string | `""`                            | Exact transcript of the reference clip. |
| `TimeoutSeconds`   | int    | `30`                             | HTTP request timeout. |
| `WarmUpCount`      | int    | `10`                             | Number of dummy synthesis calls sent at startup. |
| `TextSplitMethod`  | string | `"cut5"`                        | Passed as `text_split_method`. |
| `FragmentInterval` | float  | `0.4`                           | Passed as `fragment_interval`. |
| `MediaType`        | string | `"wav"`                          | Fixed; must be `wav`. |
| `StreamingMode`    | bool   | `false`                         | Fixed. |

If `Enabled` is `false` or `ReferenceAudioPath` is missing/invalid, no TTS component is started and the mod behaves as if TTS were absent.

---

### 3. Components & Responsibilities

#### 3.1 `TtsConfiguration`
Immutable struct populated from BepInEx config in `Awake()`. Includes a pre‑computed Base64 string of the reference audio (read once and cached).

#### 3.2 `TtsServiceClient`
- Wraps a single `HttpClient` with the configured base address and timeout.
- **Method:** `Task<byte[]> SynthesizeAsync(string text)`  
  - Builds JSON body exactly:
    ```json
    {
      "text": "<the reply>",
      "prompt_text": "<ConfiguredPromptText>",
      "audio": "<Base64 reference audio>",
      "text_split_method": "<config>",
      "fragment_interval": <config>,
      "media_type": "wav",
      "streaming_mode": false
    }
    ```
  - `POST`s to `/tts`; accepts `200 OK` with `Content-Type: audio/wav`.
  - On non‑200, throws `TtsRequestException`.
  - On timeout, throws `TaskCanceledException` (wrapped in a custom exception).
  - The returned task is *never* awaited on the Unity main thread.

#### 3.3 `AudioPlayer`
- Uses **NAudio** (`WaveOutEvent` or `WasapiOut`).
- **Method:** `void PlayWav(byte[] wavBytes)` – must be called from a worker thread.
  - Decodes the WAV header via `WaveFileReader(new MemoryStream(wavBytes))`.
  - Passes the PCM provider to the output device and calls `Play()` / `Stop()` as needed.
  - Ensures all disposable NAudio objects are properly released.
  - **Playback is fire‑and‑forget** – the module does not track when playback finishes (acceptable for voice enhancement).
- Must be initialised once (at startup) and disposed when the mod is destroyed.

#### 3.4 `TtsManager` (dedicated background thread)
- Owns a `BlockingCollection<TtsRequest>` that carries the text to speak.
- **Thread loop:**
  1. Take a `TtsRequest` from the collection (blocks).
  2. Call `TtsServiceClient.SynthesizeAsync(request.Text)`.
  3. If successful, call `AudioPlayer.PlayWav(result)`.
  4. If any exception occurs (service unreachable, timeout, invalid WAV), log **one** warning per failure episode (cooldown of ~5 seconds) and discard the request.
- **Public API:**
  - `void EnqueueSpeak(string text)` – enqueue a real reply. If `Enabled` and warmed up, add to collection; otherwise ignore (but still log a warning if permanent failure).
  - `void EnqueueWarmup(string text)` – used only during startup.
- **Warm‑up protocol:**
  - During `Awake()`, if `Enabled`:
    1. Start the `TtsManager` thread.
    2. Spawn a background `Task` that sends `WarmUpCount` POSTs with dummy text (`"WARM"`) **sequentially** (small delay between them) but **without blocking** the main thread. Discard responses.
    3. Set an `_isWarmedUp` flag after all are sent (or after the first successful response, whichever comes first) – we optimistically assume warm‑up completes fast enough.
    4. Real `EnqueueSpeak` calls before the flag is set are **still processed** (the server is already receiving warm‑up requests concurrently, so no extra delay).

#### 3.5 Integration with `LlmChatController`
- In the existing method that finalises a reply (e.g., `HandleChatResult`), *after* the text has been added to the UI queue, call:
  ```csharp
  if (config.Enabled) TtsManager.EnqueueSpeak(replyText);
  ```
- The TTS queue is completely separate; no changes are made to the existing text display pipeline.

---

### 4. Startup & Shutdown Sequence

**Startup (`Awake`):**
1. Read `[TTS]` config and build `TtsConfiguration`.
2. If disabled or invalid, return.
3. Read the reference audio file from disk into a byte array; convert to Base64. Log error and disable TTS if file missing/unreadable.
4. Create `TtsServiceClient` and `AudioPlayer`.
5. Start `TtsManager` thread (which immediately starts its `BlockingCollection` loop).
6. Kick off the warm‑up task (fire‑and‑forget).

**Shutdown (`OnDestroy` / mod disable):**
1. Signal the `CancellationToken` for the background thread.
2. `BlockingCollection.CompleteAdding()`.
3. Wait for the thread to join (with timeout).
4. Dispose `HttpClient`, NAudio outputs, and the `AudioPlayer`.

---

### 5. Error Handling & Degrade Path

| Scenario                                      | Behaviour |
|-----------------------------------------------|-----------|
| TTS endpoint unreachable (first request)      | Log a warning: `"TTS service not available; voice disabled"`. All subsequent `EnqueueSpeak` calls are silently ignored (no further warnings). |
| Timeout or non‑200 during synthesis            | Log one warning per unique error type, then suppress repeats for a few seconds. Chat text unaffected. |
| Invalid WAV bytes returned                    | Log warning, discard. |
| `ReferenceAudioPath` missing or file missing   | Disable TTS at startup; no runtime attempts. |
| Very long reply text                          | Sent as‑is; server handles splitting via `cut5`. If the HTTP body exceeds a limit (e.g., 8 KB), the request may fail – accept that; the server is local and limits are generous. |
| Empty reply                                   | `EnqueueSpeak` returns immediately (no synth). |
| NAudio device error (e.g., no default device) | Log warning and disable playback permanently (flag set). |

---

### 6. Thread Safety & Performance Constraints

- **All HTTP calls** happen on the `TtsManager` thread or via `Task.Run` – `async/await` is never used on the Unity main thread.
- **NAudio playback** is invoked from the same thread; `WaveOutEvent` internally starts its own background thread, so it does not block.
- The `BlockingCollection` uses a bounded capacity (e.g., 5) to avoid unbounded memory growth if the LLM responds faster than synthesis.
- The warm‑up requests use a separate `HttpClient` or the same one with `Task.WhenAll`? Sequential is safer to avoid overwhelming the local service – we'll send one per 100 ms.
- No new `Update()` logic. The existing text UI queue drain in `Update` is untouched.

---

### 7. Acceptance Criteria

1. **Normal operation**
   - Given the TTS service is running and configured correctly, when a chat reply is generated:
     - The text appears in the chat UI as before.
     - Audio of the reply plays through the default Windows audio device, with the correct voice (matching the reference clip).
     - Synthesis and playback do **not** cause any visible frame hitch in the Unity Editor / game (≤ 1 ms main‑thread spike on average).

2. **Graceful degradation – service absent**
   - With the TTS service process killed, or `Endpoint` pointing to an unreachable port:
     - Chat works normally.
     - **Exactly one** warning is logged (e.g., `"TTS service not available; voice disabled."`).
     - No exception dialogs, no hangs, no memory leaks.

3. **Warm‑up effectiveness**
   - After mod startup, before any user chat, the service receives `WarmUpCount` POSTs (default 10) with dummy text.
   - The **first real reply synthesis** takes ≤ 1.2× real‑time (measured by sampling `Stopwatch` around `SynthesizeAsync`), provided the service was fully initialised before the warm‑up started. (Acceptable if the first real call is still slightly slow due to remaining compilation steps, but noticeably faster than the ~11× without warm‑up.)

4. **Configuration fidelity**
   - Changing `Enabled` to `false` and restarting the mod results in zero TTS activity.
   - Changing `PromptText`, `ReferenceAudioPath`, or `TimeoutSeconds` is respected on the next synthesis (requires a restart or a runtime re‑read – acceptance for now: only read at startup).
   - Default values are applied when keys are absent from the config file.

5. **Off‑main‑thread enforcement**
   - Use a C# profiler or `Debug.Assert(!UnityEngine.Thread.CurrentThread.IsMainThread)` checks inside `SynthesizeAsync`, `PlayWav`, and the `TtsManager` thread loop to confirm no main‑thread involvement during synthesis/playback.

6. **Edge cases**
   - Empty string reply → no synthesis attempt, no log spam.
   - Reply containing only whitespace → same as empty.
   - Service returns a non‑200 status → log warning, skip playback, chat continues.
   - Timeout of 30 seconds → synthesis aborted, warning logged, no hang.
   - NAudio fails to initialise (e.g., no sound device) → TTS permanently disabled, warning logged once.

---

### 8. Implementation Order (Suggested)

1. Add configuration parser and `TtsConfiguration` struct.
2. Implement `TtsServiceClient` with a simple `HttpClient` and JSON builder (using `System.Text.Json`).
3. Implement `AudioPlayer` with NAudio (test standalone with a WAV file loaded from disk).
4. Build `TtsManager` thread and queue; hard‑code a test enqueue from a keyboard input.
5. Implement warm‑up logic.
6. Hook into `LlmChatController` and test end‑to‑end.
7. Add all error‑handling paths and logging cooldowns.
8. Profile and verify main‑thread impact.

---

**Note:** The plan assumes that the GPT‑SoVITS service is already running locally and accepts the described JSON payload. Reference audio is sent inline as Base64; if the service expects a file path instead, the configuration and client need a trivial adjustment (still read the file to verify existence, but send the path instead of Base64). The acceptance criteria remain unchanged.