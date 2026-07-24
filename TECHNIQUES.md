# Design techniques

Lilith is built from small systems that each preserve the feeling of one
continuous companion. These are the methods behind them, without the tuning
values that belong to a particular build.

| function | techniques used |
|---|---|
| **Conversation and persona** | Structured persona prompting, live game-state context, multilingual style guidance, and a validated reply-and-action format. |
| **Memory management** | Separate rolling memory for conversations and interactions, conversational correction and forgetting, local atomic persistence, backup recovery, and migration of older memory files. |
| **Episodic memory** | Periodic LLM consolidation turns meaningful conversation stretches into sourced episodes and replaceable semantic facts. Importance, emotion, confidence, recency, and recall history guide what remains. |
| **Memory retrieval** | Local feature vectors combine words, character fragments, topic and person matches, and a small multilingual synonym map. Relevant memories are retrieved without a hosted vector database or embedding API. |
| **Speech input** | Local Whisper transcription, voice activity detection, room-noise calibration, voiced-region trimming, and rejection of common silence hallucinations. |
| **Voice synthesis** | Local GPT-SoVITS synthesis, sentence chunking, reusable audio caching, background queueing, and subtitle-to-audio synchronization. A shared coordinator keeps native and synthetic voices exclusive. |
| **Awareness and initiative** | Time, posture, sleep state, recent interactions, and memory are composed into dynamic context. Context gates and shared speech arbitration keep spontaneous remarks from interrupting other moments. |
| **Foreground awareness** | Foreground process detection, local Steam manifest resolution, executable-name fallback, and stability filtering. Window titles and application contents are never read. |
| **Live information** | Intent-gated weather and web retrieval, metasearch, readable-page extraction, caching, and isolated untrusted context passed to the reply model. |
