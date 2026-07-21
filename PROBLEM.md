# Problem: GPT-SoVITS synthesis was ~6x slower than expected

Status: **fixed 2026-07-21**. Every request now sends `parallel_infer: false`,
and synthesis is back to ~1.5-2.5 s warm. The *mechanism* is confirmed; the
*root cause* is not fully settled - see the caveat below.

## Resolution

Stage timing from GPT-SoVITS showed the regression was in audio synthesis, not
text preprocessing or semantic-token generation. A representative warm request
spent 1.5-3.8 s on semantic generation and 10-16 s in synthesis. The MIOpen
workspace warnings occurred during that synthesis stage.

Using identical text and seed isolated the request flag:

| request mode | time |
|---|---:|
| parallel, `cut5` | 6,600 ms |
| serial, `cut5` | 1,541 ms |
| parallel, `cut0` | 6,655 ms |
| serial, `cut0` | 1,653 ms |

After restarting the service with its original environment, serial inference
measured 23,433 ms cold, then **1,629 ms and 1,543 ms warm**. This shows the
speedup comes from `parallel_infer: false`, not from clearing `HIP_PATH` or the
allocator experiment.

Applied to `TtsClient`, `VoiceModelSwitcher`, the launcher warm-up, and
`precache-game-voice.py`. Serial mode still preserves the silent separators used
to split bulk-cache batches.

### Caveat: parallel vs. concurrency is not fully separated

A later A/B on the **idle** service, with the original environment (`HIP_PATH`
still ROCm 6.2, no `MIOPEN_*` set), found both modes fast and indistinguishable:

| | run 1 | run 2 | run 3 |
|---|---|---|---|
| `parallel_infer=false` | 3,343 ms | 2,531 ms | 2,024 ms |
| `parallel_infer=true` | 1,765 ms | 2,797 ms | 2,388 ms |

The difference from the table above is **load**. The original 19-30 s
measurements were taken while the game was running and driving native-dialogue
replacement - 5 voice-cache files were written during that window, and the log
showed `Holding line … until ja audio is ready` and `synth started for next
sentence while current one plays`. So the service was fielding overlapping
requests throughout.

Two readings remain open:

1. `parallel_infer` is slow on this MIOpen stack, full stop.
2. Parallel inference degrades badly **under concurrent requests**, and serial
   mode avoids the contention. Unloaded, both are fine.

Reading 2 would additionally explain why the 3.4 s/file precache baseline looked
so much faster: that run had no game attached, so the "6x regression" below
compared a loaded service against an unloaded one and was never apples to apples.

**The test that would settle it:** A/B parallel vs serial *with the game
running*. If parallel only collapses under load, the mechanism is contention,
which predicts where else it bites - the precacher, or two overlapping sentences
in a single reply.

Either way `parallel_infer: false` is the right default: no slower idle,
clearly better loaded.

## Symptom, as originally measured

Synthesis of one short Japanese sentence took ~19-30 s. Same request repeated
(19 chars, `cut5`, `fragment_interval` 0.4, ~4.7 s of audio) - **note the game
was running throughout these**:

| condition | run 1 | run 2 | run 3 |
|---|---|---|---|
| as found | 24,785 ms | 19,507 ms | 20,237 ms |
| speech listener killed (2.8 GB VRAM freed) | 19,135 ms | 19,092 ms | 19,385 ms |
| TTS service restarted | 53,954 ms | 23,254 ms | 29,814 ms |

A 3-char utterance took ~2,093 ms, so cost scaled with length rather than being
fixed overhead.

Baseline for comparison: the `precache-game-voice.py` bulk run on 2026-07-20
21:40 wrote 1610 files in 5,400 s = **3.4 s/file**. HANDOFF §5 separately records
~2.2 s warm. See the caveat above about this comparison being unloaded.

## Ruled out (each tested, not assumed)

- **Warm-up / kernel compilation per sequence length** (HANDOFF gotcha 8).
  Repeated *identical* requests stayed flat at ~19-20 s; a warm-up curve decays.
- **VRAM exhaustion.** The card is 16 GB (15.9 GB visible to torch); total
  dedicated usage was 7,663 MB. Killing the speech listener freed 2.8 GB and
  timings did not move at all.
- **Degraded long-lived service process.** Restarting TTS made it *worse*.
- **Display driver change.** Driver 32.0.31021.5001 is dated 2026-06-28, which
  predates the fast 07-20 run.
- **CPU fallback.** Model on GPU: `device: cuda`, `is_half: True`.

## Red herrings - do not re-investigate these

- **MIOpen workspace warnings.** `tts-server-error.log` floods with
  `[IsEnoughWorkspace] Solver <GemmFwdRest>, workspace required: 26132480,
  provided … 4841472`. Real, and it does occur during the slow synthesis stage,
  but it is not the lever - the timings change with `parallel_infer` while these
  warnings persist.
- **ROCm 6.2 vs HIP 7.15 mismatch.** `HIP_PATH` points at ROCm 6.2 while torch
  is `2.14.0a0+rocm7.15`. Looks alarming, means nothing here: process module maps
  show MIOpen, rocBLAS, hipBLASLt and `amdhip64_7.dll` all loading from torch's
  bundled `_rocm_sdk_*` directories. Clearing `HIP_PATH`, enabling expandable
  allocator segments, and setting `MIOPEN_FIND_MODE=NORMAL` left warm requests at
  16.7-20.3 s while parallel inference was still on.

## How to time a request

`Invoke-WebRequest` needs UTF-8 bytes explicitly - passing a string sends the
wrong encoding and the service replies `Please enter valid text` (HANDOFF gotcha
7). It also needs `-UseBasicParsing`, or it tries to prompt and fails.

```powershell
$ProgressPreference='SilentlyContinue'
$ref="D:\SteamLibrary\steamapps\common\The NOexistenceN of Lilith\BepInEx\plugins\LilithMod\voice\jp\calm-reference.wav"
$body=@{ text="今日はいい天気ですね、少し眠いけれど。"; text_lang="ja"; ref_audio_path=$ref;
         prompt_lang="ja"; text_split_method="cut5"; fragment_interval=0.4;
         media_type="wav"; streaming_mode=$false; parallel_infer=$false } | ConvertTo-Json -Compress
$bytes=[System.Text.Encoding]::UTF8.GetBytes($body)
foreach($i in 1..3){
  $sw=[Diagnostics.Stopwatch]::StartNew()
  $r=Invoke-WebRequest -Uri "http://127.0.0.1:9880/tts" -Method Post -Body $bytes `
       -ContentType "application/json; charset=utf-8" -TimeoutSec 180 -UseBasicParsing
  $sw.Stop(); "run {0}: {1,6:N0} ms  {2:N0} bytes" -f $i,$sw.ElapsedMilliseconds,$r.RawContentLength
}
```

Change one variable at a time, three runs minimum, and **record whether the game
was running** - single measurements and unrecorded load each produced a wrong
conclusion during this investigation.
