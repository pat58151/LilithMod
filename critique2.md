# Rewrite request: plan-step2.md FAILED review. Fix these defects.

Produce a corrected, complete plan in the same format and scope. Output only the fixed
plan; do not restate this critique.

## FATAL 1 - drop the DialogueLineDatabase / DialogueLineEntry dependency entirely
Sections 4.2, 6.1 and 3.1 build a DialogueLineEntry per node and inject it into a live
DialogueLineDatabase. That database is NOT RESIDENT. This was proven empirically in step 1:
the shipped dumper logged "No DialogueLineDatabase found in memory - skipping dump" and
reported DialogueLines: 0. It is loaded per-locale on demand through Addressables
(Data/DialogueLine/{en,ja,zh-CN,zh-HK}/DialogueLineDB), not at startup.

Worse, allocating custom nodes a lineId of 9000000+ creates a DANGLING reference. Evidence
from the live dump: of 1346 nodes, the 43 with lineId 0 ALL have empty text, while every
node carrying real text also carries a real lineId. Combined with the per-locale database,
lineId is the localization lookup path and inline `text` is the dev-language copy. A
custom node pointing at a lineId that resolves in no database risks rendering BLANK.

Required changes:
- Remove all DialogueLineEntry creation and all DialogueLineDatabase injection.
- Remove DialogueLineDatabase from the section 3.1 max-ID scan.
- Set custom DialogueNode.lineId = 0 and rely on the inline `text` field.
- Remove lineId from the ID allocation counters (nodes need only `id`).
- State explicitly in the acceptance criteria that whether inline `text` renders is THE
  primary unverified runtime risk, must be confirmed in-game, and that the fallback if it
  renders blank is to load the active locale's DialogueLineDatabase via Addressables and
  inject line entries there. Do not implement that fallback now; just name it.

## FATAL 2 - groupId is a context key, not a per-choice id
Section 4.1.2 allocates "a new unique group id" per choice. That is wrong and breaks
PlayerLineDatabase grouping (`_childrenByGroupId`, `GetDirectChildren(int id)`,
`HasChildren(int id)`, `GetRootEntries()`).

Evidence from the live dump of 255 player line entries: only 42 distinct groupIds exist,
in sets of up to 78 sharing one value. 16 of those 42 groupIds are themselves
PlayerLineEntry ids (nested follow-up choices); others (700190, 700191, 700192, 700201...)
are DialogueNode ids; 0 and 9999 are sentinel buckets. groupId identifies the CONTEXT a
choice belongs to.

Required change: every choice belonging to one node shares ONE groupId, equal to the
owning DialogueNode's assigned id. Since custom node ids are >= 9000000 and unique, this
is automatically collision-free and needs no separate groupId counter. Remove the
per-choice groupId allocation and the groupId counter from section 3.

## MINOR 3 - simplify ID allocation accordingly
After the two fixes above, only three counters remain: DialogueNode.id (>= 9000000),
PlayerLineEntry.id (>= 900000), PlayerLineEntry.LineID (>= 9000000). groupId is derived,
lineId is constant 0. Update section 3 to match.

## Keep unchanged
Everything else in the plan is accepted: the authoring format, choice validation rules,
raw passthrough with collision checking, database selection by databaseName "DialogueNode",
the BuildIndex/RegisterNodeWeight sequence, Il2Cpp collection construction, reuse of the
existing Update()-polling component, logging through LilithModPlugin.Logger, the
TryGetNode self-check, and the error-resilience rules. `conditions` is a single
DialogueCondition (not a list) - your section 5 is correct.
