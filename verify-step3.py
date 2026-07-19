import os, subprocess, sys

need = ["LilithMod/Message.cs", "LilithMod/ChatResult.cs", "LilithMod/LlmChatController.cs",
        "LilithMod/WindowFocus.cs"]
missing = [f for f in need if not os.path.exists(f)]
if missing:
    print("VERIFY FAIL - required files not created yet: " + ", ".join(missing))
    sys.exit(1)

src = ""
for f in need:
    src += open(f, encoding="utf-8", errors="ignore").read()

marks = {
    # Unity input is dead in this game's window; the hotkey MUST go through Win32.
    "GetAsyncKeyState (global hotkey, not Unity input)": "GetAsyncKeyState",
    "window focus toggle for typing": "EnableTyping",
    "window style restored": "RestoreWindow",
    "ConcurrentQueue (thread handoff)": "ConcurrentQueue",
    "textComponent wiring": "textComponent",
    "textViewport wiring": "textViewport",
    "chat/completions endpoint": "chat/completions",
    "reserved node 9500000": "9500000",
}
absent = [k for k, v in marks.items() if v not in src]
if absent:
    print("VERIFY FAIL - required elements missing: " + "; ".join(absent))
    sys.exit(1)

r = subprocess.run([r"C:\Program Files\dotnet\dotnet.exe", "build",
                    r"D:\Lilith\LilithMod\LilithMod.csproj", "-c", "Release"],
                   capture_output=True, text=True)
errs = [l for l in (r.stdout + r.stderr).splitlines() if "error " in l]
if r.returncode != 0 or errs:
    print("VERIFY FAIL - build errors:")
    for l in errs[:15]:
        print(l)
    sys.exit(1)

print("VERIFY PASS - files present, required elements found, build clean")
