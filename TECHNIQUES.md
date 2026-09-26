# Design techniques

The methods behind each system. Tuning values are left out because they change
between builds.

| System | Techniques |
|---|---|
| **Conversation and persona** | Structured persona prompt, live game state in the prompt, per-language style rules, and a checked reply-and-action format. |
| **Memory storage** | Separate rolling memory for conversations and interactions, correcting and forgetting by asking in chat, atomic local saves, backup recovery, and migration of older memory files. |
| **Episodic memory** | The AI periodically turns meaningful stretches of conversation into episodes with sources, plus facts that can be replaced. Importance, emotion, confidence, age, and past recalls decide what is kept. |
| **Memory search** | Local feature vectors built from words, character fragments, topic and person matches, and a small multilingual synonym list. No hosted vector database or embedding API. |
| **Speech input** | Local Whisper transcription, voice activity detection, room noise calibration, trimming to the voiced part, and filtering of common false transcripts from silence. |
| **Voice output** | Local GPT-SoVITS, sentence splitting, audio caching, a background queue, and subtitle-to-audio sync. One coordinator makes sure the game voice and the generated voice never play at once. |
| **Awareness and speaking first** | Time, posture, sleep state, recent interactions, and memory are combined into the prompt. Checks on the current situation, plus shared control of who is speaking, stop her remarks from interrupting other moments. |
| **Foreground app** | Detects the foreground process, looks up the name in local Steam manifests, falls back to the executable name, and ignores brief switches. Window titles and app contents are never read. |
| **Live information** | Weather, next-day forecast, and web search run only when the message asks for them. Uses your SearXNG server first with public fallbacks, extracts readable page text, caches results, and passes them to the AI marked as untrusted. An incomplete forecast is dropped instead of reported for the wrong day. |
