# Plan gate: FAIL. Rewrite addressing every point below.

The structure is sound - separate client, player, queue thread, and a single
enqueue call from `HandleChatResult` is the right shape. These are correctness
defects, not style preferences.

## 1. The HTTP contract in §3.2 is invented and wrong

The plan sends `"audio": "<Base64 reference audio>"`. GPT-SoVITS `api_v2.py`
accepts no such field, and two REQUIRED fields are missing entirely, so every
request would fail with HTTP 400.

This is the verified contract - it was exercised against the running service today:

```json
{
  "text":                "<the reply>",
  "text_lang":           "ja",
  "ref_audio_path":      "D:\\...\\voice\\jp\\calm-reference.wav",
  "prompt_text":         "<transcript of that clip>",
  "prompt_lang":         "ja",
  "media_type":          "wav",
  "streaming_mode":      false,
  "text_split_method":   "cut5",
  "fragment_interval":   0.4
}
```

`ref_audio_path` is a **filesystem path the service reads itself** - the audio is
never uploaded. `text_lang` and `prompt_lang` are mandatory. Response is raw WAV
bytes with `Content-Type: audio/wav`; errors return JSON `{"message":..., "Exception":...}`
with HTTP 400. Drop the Base64 machinery and the closing note speculating about
path-vs-Base64: there is no ambiguity.

Config must therefore expose `TextLang` and `PromptLang` (default `ja`), which the
plan omits.

## 2. Warm-up with `"WARM"` warms almost nothing

Kernel compilation is **per sequence length** - measured today, each new input
length pays its own compile cost, and the settling curve took roughly ten calls of
*similar* length. A 4-character dummy does not prepare the model for a 20-character
reply. Warm-up text must resemble real replies in length; use two or three fixed
sentences of ~15-30 characters, matching her actual line lengths.

## 3. §3.4 step 4 contradicts the intent

"Real `EnqueueSpeak` calls before the flag is set are still processed" means the
first real reply can hit a cold service - the exact failure the intent said to
design out. Either hold real requests until warm-up completes (bounded by a
timeout, then proceed anyway), or state plainly that the first reply may be slow
and drop the warm-up feature. Do not claim both.

## 4. `UnityEngine.Thread.CurrentThread.IsMainThread` does not exist

§7.5 asserts against an API that exists in neither UnityEngine nor .NET. It would
not compile. If you want a main-thread guard, capture
`Thread.CurrentThread.ManagedThreadId` during `Awake()` and compare against it.

## 5. Use Newtonsoft.Json, not System.Text.Json

§8.2 introduces `System.Text.Json`. The project already references
Newtonsoft.Json 13.0.3 and uses it throughout (`DumpDatabaseBehaviour`). Adding a
second serializer to a netstandard2.1 IL2CPP plugin adds interop risk for nothing.

## 6. §3.3 and the acceptance criteria contradict each other

§3.3 makes playback "fire-and-forget" that "does not track when playback finishes",
while acceptance requires "a currently playing sentence is never cut off by a new
one". Sequencing requires knowing when playback ends. Block the queue thread on
playback completion (`WaveOutEvent.PlaybackStopped` or a poll on `PlaybackState`),
which is safe because that thread is not Unity's.

## 7. Missing: the csproj change

NAudio is never added to `LilithMod/LilithMod.csproj`. Without a
`<PackageReference Include="NAudio" .../>` the build fails. State the edit
explicitly and confirm `CopyLocalLockFileAssemblies` already copies it to the
plugin folder, as it does for Newtonsoft.

## 8. No verify command

The plan must name one runnable command that checks its own acceptance criteria.
Follow the existing pattern in `verify-step3.py`: assert the required files exist,
grep for the load-bearing elements (`ref_audio_path`, `text_lang`, `NAudio`,
`fragment_interval`), then build. A bare `dotnet build` is explicitly insufficient
here - it passes on code that was never written.

## 9. Minor

- §5 "8 KB body limit - accept that": replies are capped at one or two short
  sentences by the persona prompt. Not a real case; drop it.
- Default `Enabled=false` plus `ReferenceAudioPath=""` means the feature is off in
  two independent ways. Default the reference path to the installed
  `jp/calm-reference.wav` and its known transcript so enabling one flag is enough.
