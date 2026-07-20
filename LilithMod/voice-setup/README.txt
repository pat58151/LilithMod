LILITH VOCAL SYNTHESIS SETUP

No synthetic voice model is included with shared builds.

QUICK SETUP
1. Install GPT-SoVITS in a local folder. The current setup uses its api_v2.py server.
2. Add your GPT .ckpt and SoVITS .pth voice weights.
3. Add a clean 3 to 10 second reference WAV and its exact transcript.
4. Edit voice-config.ini in this folder.
5. Set SpokenLanguage to ja, en, or zh.
6. Set SubtitleLanguage to ja, en, or zh.
7. Fill the matching Profile section with your weights, reference WAV, transcript, and prompt language.
8. Give each changed voice a new CacheIdentity so old cached WAV files are not reused.
9. Restart Lilith through runtime\start-lilith.ps1 or the installed Lilith shortcut.
10. In game settings, select Vocal Synthesis under Voice Language.

CURRENT CONFIGURATION
The included example matches the development setup: Japanese synthetic speech with English text.
Native game dialogue text still follows the game's Game Language setting.

DISABLE SYNTHESIS
Set Enabled = false. Lilith still starts and chat text still works.

FILES ARE LOCAL
API keys remain in the game config. Voice files, model weights, and reference audio remain on this computer.
