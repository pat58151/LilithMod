# Rewrite request: the previous plan FAILED review. Fix these defects.

The attached plan.md was reviewed against intent.md and rejected. Produce a corrected,
complete plan (same format, same scope). Do not restate the critique; output the fixed plan.

## FATAL 1 - wrong singleton accessor (would not compile)
The plan uses `DialogueManager.Instance`. That member does not exist.
Verified from the decompiled assembly:

    public class ACSingletonBehaviour<T> : MonoBehaviour where T : MonoBehaviour
    {
        public static T s_instance { get; set; }
    }

`DialogueManager : ACSingletonBehaviour<DialogueManager>`, so the ONLY accessor is
`DialogueManager.s_instance`. There is no `Instance`, `instance`, or `Inst`.
Use `DialogueManager.s_instance` everywhere and null-check it.

## FATAL 2 - managed MonoBehaviour is not usable in IL2CPP without registration
The plan defines `DumpDatabaseBehaviour : MonoBehaviour` and attaches it with
`AddComponent<DumpDatabaseBehaviour>()`. Under BepInEx 6 IL2CPP that crashes at runtime.
Any managed type inheriting an Il2Cpp type MUST be registered before use:

    Il2CppInterop.Runtime.Injection.ClassInjector.RegisterTypeInIl2Cpp<DumpDatabaseBehaviour>();

called in `Load()` BEFORE the GameObject/AddComponent, and the class MUST also declare the
IL2CPP marshalling constructor:

    public DumpDatabaseBehaviour(System.IntPtr ptr) : base(ptr) { }

State both explicitly in the plan. (`ClassInjector` lives in `Il2CppInterop.Runtime.dll`,
namespace `Il2CppInterop.Runtime.Injection` - confirmed present in be.785.)

## RISK 3 - drop the coroutine, poll in Update()
`StartCoroutine` with a managed `IEnumerator` does not marshal reliably through
Il2CppInterop. Replace `WaitAndDump()` entirely with `Update()`-based frame polling:
keep an elapsed-time float and a `_done` bool, check `DialogueManager.s_instance` each
frame, run the dump once when it appears, give up after a timeout. This removes the
entire coroutine failure class and is simpler.

## RISK 4 - typed lookup for DialogueLineDatabase
`Resources.FindObjectsOfTypeAll<T>()` is unreliable for Il2Cpp types. Use the
Il2CppType overload and cast:

    Resources.FindObjectsOfTypeAll(Il2CppType.Of<DialogueLineDatabase>())

Also: DialogueLineDatabase is loaded per-locale through Addressables
(`Data/DialogueLine/{en,ja,zh-CN,zh-HK}/DialogueLineDB`) and may legitimately not be
resident in memory at dump time. Treat "not found" as an expected INFO/WARN outcome,
not an error, and still succeed overall.

## GAP 5 - be explicit about what verify actually proves
The verify command `dotnet build -c Release` only proves done-bullet 1 (it compiles).
Done-bullets 2-4 are runtime behaviour confirmed by launching the game and reading
BepInEx\LogOutput.log. Say this plainly in the acceptance criteria so a green build is
not mistaken for a finished feature.

## MINOR 6 - fix the inconsistent output layout
Plan says the DLL goes to `plugins\LilithDatabaseDumper.dll` but the dump goes to
`plugins\LilithMod\dump\`. Pick one consistent layout and use it throughout.
Preferred: DLL at `plugins\LilithMod\LilithMod.dll`, dumps at `plugins\LilithMod\dump\`.
