# Lilith: behaviour spec

Written 2026-07-21. What she does, when, and why - as a brief for tuning it.

This is the *behaviour* layer only. Installation is `INSTALLER-HANDOFF.md`,
portability is `PORTABILITY.md`, open faults are `PROBLEM.md`.

Every number here is a real constant in the code. Where a value was chosen by
measurement rather than taste, the measurement is given, because that is what
makes it safe to change.

---

## 0. The design intent, in one paragraph

She is a companion, not an assistant. The whole tuning problem is that a
companion who speaks too often is noise, and one who speaks too rarely is
furniture. Almost every constant below exists to hold that line. When changing
one, the question is not "is this responsive?" but "does this feel like someone
who is present without demanding attention?"

Two rules have never been negotiable:

- **She never rejects affection.** Not deflected, not joked away, not qualified.
  This is in the persona prompt in deliberately absolute language.
- **She never cuts herself off.** Nothing interrupts a line she is already
  speaking. Four separate code paths defer for this.

---

## 1. What can make her speak

Four routes. They compete, and the ordering between them is deliberate.

| route | trigger | gated by |
|---|---|---|
| **Chat** | player types (F7) or speaks (F8) | nothing - always answered |
| **Interaction** | player touches, drags, pets | chance + cooldowns |
| **Ambient** | idle timer | interval + cooldowns |
| **Native** | the game's own scripted dialogue | voice replacement only |

Chat is the only one that always produces a reply. The other three are
suppressible by design.

### Update order (one frame)

```
DrainInteractions()
TrySendQueuedUserMessage()     <- player's words win ties
TryInteractionReply()
TrySubmitSpeechCommand()
TryAmbientRemark()             <- lowest priority
```

If a queued chat message and an ambient remark both become eligible on the same
frame, the player's message goes first and blocks the remark. That ordering is
load-bearing, not incidental.

---

## 2. The speech coordination rules

**Nothing interrupts her.** All four routes wait on one predicate:

```csharp
SpeechStillFinishing =>
    _replyPlaybackActive || (now - _speechEndedAt) < InteractionAfterSpeechSeconds
```

| constant | value | why |
|---|---|---|
| `InteractionAfterSpeechSeconds` | 1 s | beat after her voice stops, so two utterances do not run together |
| `QueuedMessageMaxWaitSeconds` | 20 s | a held chat message is sent regardless after this. A stuck playback flag swallowing what the player typed is worse than talking over her |

A held chat message is **replaced** by a newer one, not queued behind it - the
newest is what the player still means. A held interaction is a **single slot**;
touching five times while she talks produces one reply, not five.

**This was the single largest source of bugs.** Three separate paths each
cancelled her mid-sentence and each had to be fixed independently. Any new route
that can start a reply must wait on `SpeechStillFinishing`, and
`verify-bilingual.py` asserts that at least four call sites do.

---

## 3. Interaction replies

Touching her produces the game's own reaction always, and a generated reply
sometimes.

| constant | value |
|---|---|
| delay after the touch | 3 s |
| `NativeDialogueQuietSeconds` | 8 s after the game's own line |
| `InteractionReplyChance` | 0.7 awake |
| `SleepingInteractionReplyChance` | 0.4 asleep |
| `SpontaneousGapSeconds` | 180 s between any two unprompted replies |

The roll happens **once, at the moment of firing**, and a miss discards the
pending interaction. Rolling every frame while it waits would come up true
eventually and make the chance meaningless.

### Known rough edge

Rapid clicking locks her out. Each touch produces a native line, which re-arms
the 8 s quiet window, so persistent clicking means only the game talks and she
never answers. Three candidate fixes, none chosen:

1. shorten the 8 s quiet window for the interaction path
2. do not re-arm the window on a repeat of the same node
3. suppress repeated native touch lines outright

Option 2 targets the actual loop without changing single-touch behaviour, and is
the recommendation - but it has not been implemented or tested.

---

## 4. Ambient remarks

| constant | value |
|---|---|
| `AmbientMinMinutes` | 12 |
| `AmbientMaxMinutes` | 25 |
| `SleepingAmbientMultiplier` | 1.5 on **both** bounds while asleep (18-37.5 min) |

Both bounds scale so the whole window moves out rather than merely widening - a
sleeping remark should be rarer, not more erratic. The multiplier is sampled
when the next remark is **scheduled**, not when it fires, so waking mid-wait does
not shorten it.

Talking to her **reschedules** the next remark. Without that she would answer the
player and then, one second later, say something unprompted - which reads as
talking to herself rather than as company.

Held rather than rescheduled when she is mid-speech: pushing a due remark out by
another full interval is a heavy penalty for a few seconds of overlap.

---

## 5. Notes and letters

Rare on purpose. A note arriving should feel like something happened.

| constant | value |
|---|---|
| `MinConversationsPerNote` | 10 qualifying messages |
| `MinMessageLength` | 18 characters |
| `WindowHours` | 4 - they must fall in one stretch |
| `CooldownHours` | 36 |
| `Chance` | 0.2 |

**Errands do not count.** Setting a timer, asking the forecast, or sending her to
search are using her, not talking to her, and are excluded from the count.

**The chance re-rolls per qualifying message, not once per stretch.** This is the
easiest thing to get wrong here. At 0.4 a note was near-certain within five
messages of eligibility:

| chance | note has fired within 5 messages |
|---|---|
| 0.4 | 92% |
| 0.2 (current) | 67% |
| 0.15 | 56% |

If a note ever feels too frequent again, the effective levers are the count and
the chance. **Narrowing `WindowHours` does not work** - messages sent minutes
apart fall inside any window, so it only penalises someone who chats across an
afternoon.

A note whose conversations were personal may come out as a love letter, gated
separately on at least two personal exchanges.

---

## 6. Voice

Japanese speech under English subtitles is the intended default. The two are
independent settings.

| constant | value | note |
|---|---|---|
| `NativeSuppressedAfterModSeconds` | 4 s | game held off after she speaks |
| `StartupVoiceGraceSeconds` | 15 s | services warm since login |
| `ColdStartVoiceGraceSeconds` | 30 s | this process started them |

Measured latency, on the development machine:

| stage | time |
|---|---|
| DeepSeek call | 0.8-1.5 s, insensitive to prompt size |
| synthesis, short line | 1.2-1.9 s idle, 3.0-4.6 s with the game running |
| **total to first word** | **~4-6 s** |

Synthesis is on GPU at ~20% utilisation, so it is not GPU-compute-bound and a
faster card would not help much. The service serialises internally: four
concurrent requests cost four times one.

### The biggest available tuning decision

The subtitle is deliberately withheld until its audio is ready, so text and voice
land together. That is why the wait is ~5 s rather than the ~1.2 s the language
model actually takes.

Showing the text on arrival would make her feel roughly **four times more
responsive**, at the cost of text-then-voice instead of both at once. This is a
taste decision, not a bug - `verify-bilingual.py` currently asserts the existing
behaviour. A middle option exists: show text immediately only when synthesis
exceeds a threshold.

**If one thing is optimised, this is the one with the largest perceived effect.**

---

## 7. Speech input

| constant | value |
|---|---|
| trailing silence that ends an utterance | 1.5 s |
| give up if nothing was said | 2.5 s |

Typing while F8 listens wins: what is typed is used and the transcript discarded.

---

## 8. Open faults, honestly

- **Empty API content** on roughly half of replies, costing an extra round trip.
  `finish=stop`, `completion_tokens` 12-52, `reasoning_chars=0` - so it is not
  reasoning consuming the budget. Unexplained.
- **The wrong-language correction** is legitimate: the model really does put
  Japanese in the English `shown` field sometimes. Costs another round trip when
  it happens. A bad exchange is four calls and ~4-5 s.
- **Rapid-click lockout**, section 3.
- Everything in `PROBLEM.md`.

## 9. If you change one thing

Ranked by effect on how she feels:

1. **Subtitle timing** (section 6) - the only change worth multiples, not percentages
2. **Rapid-click lockout** (section 3) - makes her feel present under handling
3. **The empty-content retry** (section 8) - pure latency, no behaviour change
4. Note and ambient constants - already tuned against real sessions; change on
   taste, but read the re-roll note in section 5 first

Everything in sections 2 and 5 was arrived at by watching real sessions and being
wrong first. The comments in the code carry the reasoning; they are worth reading
before changing a number.
