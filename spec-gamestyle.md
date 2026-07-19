Write `LilithMod/GameStyle.cs` for the LilithMod BepInEx IL2CPP plugin.
Output only the complete C# file contents, no prose and no markdown fences.

## Goal
The mod builds its own chat box from bare GameObjects, so it looks nothing like the game.
Make it borrow the game's own look: copy the background sprite and the text styling from a
real input field that the game itself ships, so the mod's box visually matches the app.

The game contains several TMP_InputFields (a player-name field, a music-directory field and
a gift-key field). They may be INACTIVE at the time we look, because their settings windows
have not been opened - so `FindObjectOfType` is not enough and
`Resources.FindObjectsOfTypeAll` must be used, which also returns inactive objects.

## What to write
`public static class GameStyle` in namespace `LilithMod`. A plain static helper, NOT a
MonoBehaviour, so no IntPtr constructor. Log through `LilithModPlugin.Logger`. No exception
may escape - catch, log once, and leave the caller's UI untouched.

Because these are Il2Cpp types, enumerate with
`Resources.FindObjectsOfTypeAll(Il2CppType.Of<T>())` and convert each element with
`.TryCast<T>()`, skipping nulls. Do NOT use LINQ - the returned Il2CppArrayBase does not
carry System.Linq extension methods.

Required member:

`public static void Apply(Image background, TMP_InputField ownField,
                         TextMeshProUGUI inputText, TextMeshProUGUI placeholderText,
                         Transform ownRoot)`

Behaviour:
1. Find a donor `TMP_InputField` from the game: enumerate all of them, skip any whose
   transform is a descendant of `ownRoot` (use `Transform.IsChildOf`) and skip `ownField`
   itself, so the mod never copies from its own UI. Prefer a donor that has a non-null
   `textComponent` with a non-null `font`. If none is found, log an INFO line saying the
   game's styling could not be found and return without changing anything.

2. From the donor's own background `Image` (look for an Image on the donor's GameObject,
   else on its parent), copy onto `background`: `sprite`, `color`, `material`, `type`, and
   `pixelsPerUnitMultiplier` if present. Skip any that are null on the donor. If the donor
   has no Image at all, leave `background` alone.

3. From the donor's `textComponent`, copy onto BOTH `inputText` and `placeholderText`:
   `font`, `fontSize`, `fontStyle`, `characterSpacing`, and `colorGradient`/`color`.
   Copy the donor's `placeholder` colour onto `placeholderText` instead if the donor has a
   placeholder that is a TextMeshProUGUI, so the hint text stays visually secondary.

4. Do NOT change alignment, wrapping, overflow, or any RectTransform on the caller's
   objects - the caller manages layout and scrolling itself and those settings must survive.

5. Log one INFO line naming the donor object used and the font that was adopted, so it is
   clear in the log which game element the look came from.
