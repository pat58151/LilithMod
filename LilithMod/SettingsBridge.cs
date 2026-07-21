using System;
using System.IO;
using TMPro;
using UI.Common;
using UI.TraySetting;
using UnityEngine;
using UnityEngine.UI;

namespace LilithMod
{
    public sealed class SettingsBridge : MonoBehaviour
    {
        public SettingsBridge(IntPtr ptr) : base(ptr) { }

        internal static bool CapturingChatKey { get; private set; }

        private static TraySettingView _pendingView;
        private TraySettingView _view;
        private TMP_InputField _deepSeekKey;
        private TMP_InputField _hotkeyField;
        private Toggle _deepSeekEye;
        private TMP_Text _voiceFolderLabel;
        private TMP_Text _speechFolderLabel;
        private TMP_Text _helpLabel;
        private TMP_Text _deepSeekLabel;
        private TMP_Text _hotkeyLabel;
        private bool _lastChatAvailability = true;
        private static readonly Color DisabledColor = new Color(0.45f, 0.45f, 0.45f, 1f);
        private TMP_Text _pushToTalkLabel;
        private bool _lastSpeechAvailability = true;
        private TMP_InputField _pushToTalkKeyField;
        private Slider _opacity;
        private TMP_Text _opacityLabel;
        private float _nextSync;
        private float _nextOpacityRefresh;
        private float _lastAppliedOpacity = -1f;
        private bool _settingsInteractive;
        private bool _settingsVisible;
        private bool _deepSeekRevealed;
        private bool? _lastSynthesisAvailability;
        private static Sprite _eyeSprite;

        internal static void QueueBuild(TraySettingView view)
        {
            _pendingView = view;
        }

        private void Update()
        {
            if (_pendingView != null && (_view == null || _view.Pointer != _pendingView.Pointer || _deepSeekKey == null))
            {
                var next = _pendingView;
                _pendingView = null;
                try { BuildRows(next); }
                catch (Exception ex) { LilithModPlugin.Logger.LogWarning($"[Settings] Could not add Lilith rows: {ex.Message}"); }
            }

            if (Time.unscaledTime >= _nextOpacityRefresh)
            {
                _nextOpacityRefresh = Time.unscaledTime + 0.5f;
                ApplyLilithOpacity(LilithModPlugin.CfgLilithOpacity.Value);
                RefreshSynthesisAvailability();
                RefreshSpeechAvailability();
                RefreshChatAvailability();
                RefreshLabels();
            }

            bool settingsVisible = _view != null && _view.IsVisible;
            if (_settingsVisible != settingsVisible)
            {
                _settingsVisible = settingsVisible;
                if (settingsVisible)
                {
                    RestoreSavedVoiceSelection();
                    RefreshCurrentTabLayout();
                }
            }
            bool capturingHotkey = settingsVisible && _hotkeyField != null && _hotkeyField.isFocused;
            bool capturingPushToTalk =
                settingsVisible && _pushToTalkKeyField != null && _pushToTalkKeyField.isFocused;
            bool settingsTyping = settingsVisible &&
                ((_deepSeekKey != null && _deepSeekKey.isFocused) ||
                 capturingHotkey || capturingPushToTalk);
            // Both bindings capture the next key press, so both must suppress the live
            // hotkey and push-to-talk polling while focused.
            CapturingChatKey = capturingHotkey || capturingPushToTalk;
            if (CapturingChatKey) CaptureChatKey(capturingPushToTalk);
            if (_settingsInteractive != settingsTyping)
            {
                _settingsInteractive = settingsTyping;
                WindowFocus.SetSettingsInteractive(settingsTyping);
            }
            if (_deepSeekKey == null || Time.unscaledTime < _nextSync) return;
            _nextSync = Time.unscaledTime + 0.25f;
            Sync();
        }

        private void BuildRows(TraySettingView view)
        {
            _view = view;
            Transform inputRow = view.GetRowOf(view._musicDirInputField.transform);
            Transform actionRow = view.GetRowOf(view._openMusicFolderLabel.transform);

            _deepSeekKey = view.CloneInputRow(inputRow, "LilithDeepSeekApiKey", out TMP_Text deepSeekLabel);
            _hotkeyField = view.CloneInputRow(inputRow, "LilithChatKey", out TMP_Text hotkeyLabel);
            Transform deepSeekRow = view.GetRowOf(_deepSeekKey.transform);
            if (deepSeekRow != null) deepSeekRow.gameObject.SetActive(true);
            // Cloned rows inherit the hidden state of the row they came from, so the
            // key bindings have to be activated explicitly or the Controls tab looks
            // like it has no settings at all.
            Transform hotkeyRow = view.GetRowOf(_hotkeyField.transform);
            if (hotkeyRow != null) hotkeyRow.gameObject.SetActive(true);
            _voiceFolderLabel = view.CloneActionRow(
                actionRow, "LilithVoiceFolder",
                Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(
                    new System.Action(OpenVoiceFolder)));
            _speechFolderLabel = view.CloneActionRow(
                actionRow, "LilithSpeechFolder",
                Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(
                    new System.Action(OpenSpeechFolder)));
            _helpLabel = view.CloneActionRow(
                actionRow, "LilithHelp",
                Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(
                    new System.Action(OpenHelp)));
            Transform helpRow = view.GetRowOf(_helpLabel.transform);
            if (helpRow != null) helpRow.gameObject.SetActive(true);
            Transform speechFolderRow = view.GetRowOf(_speechFolderLabel.transform);
            if (speechFolderRow != null) speechFolderRow.gameObject.SetActive(true);
            Transform voiceFolderRow = view.GetRowOf(_voiceFolderLabel.transform);
            if (voiceFolderRow != null) voiceFolderRow.gameObject.SetActive(true);
            _pushToTalkKeyField = view.CloneInputRow(inputRow, "LilithPushToTalkKey",
                out TMP_Text pushToTalkKeyLabel);
            Transform pushToTalkRow = view.GetRowOf(_pushToTalkKeyField.transform);
            if (pushToTalkRow != null)
            {
                pushToTalkRow.gameObject.SetActive(true);
                // Clone order does not match reading order: put the chat key first,
                // since F7 before F8 is what the defaults imply.
                if (hotkeyRow != null && pushToTalkRow.parent == hotkeyRow.parent)
                    pushToTalkRow.SetSiblingIndex(hotkeyRow.GetSiblingIndex() + 1);
            }
            Transform sliderRow = view.GetRowOf(view._voiceVolumeSlider.transform);
            _opacity = TraySettingView.CloneVolumeRow(
                sliderRow, "LilithOpacity", "LilithOpacity", "Lilith opacity", 100);
            if (_opacity != null)
            {
                _opacity.minValue = 0.2f;
                _opacity.maxValue = 1f;
                _opacity.wholeNumbers = false;
                _opacity.value = Mathf.Clamp(LilithModPlugin.CfgLilithOpacity.Value, 0.2f, 1f);
                Transform opacityRow = view.GetRowOf(_opacity.transform);
                _opacityLabel = FindRowLabel(opacityRow);
                TraySettingView.StripLabelLocalizer(_opacityLabel);
                if (opacityRow != null)
                {
                    opacityRow.gameObject.SetActive(true);
                    Transform realmScheduleRow = view.GetRowOf(view._adjustFantasyScheduleSlider.transform);
                    if (realmScheduleRow != null && opacityRow.parent == realmScheduleRow.parent)
                        opacityRow.SetSiblingIndex(realmScheduleRow.GetSiblingIndex());
                }
            }

            _deepSeekLabel = deepSeekLabel;
            _hotkeyLabel = hotkeyLabel;
            _pushToTalkLabel = pushToTalkKeyLabel;
            // Underlined so it reads as something to click rather than a setting
            // name. Rich text is off by default on cloned rows.
            if (_helpLabel != null)
            {
                _helpLabel.richText = true;
                // Left in English deliberately: "Help" reads as itself in every
                // language this game ships, and the file it opens is English anyway.
                SetWrappedLabel(_helpLabel, "<u>Help</u>");
            }
            // Force a re-apply: the rows were just rebuilt, so whatever language was
            // applied to the previous set does not describe these.
            _labelLanguage = null;
            RefreshLabels();

            _deepSeekKey.text = LilithModPlugin.CfgApiKey.Value ?? string.Empty;
            _hotkeyField.text = LilithModPlugin.CfgHotkey.Value ?? "F7";
            if (_hotkeyField.placeholder is TMP_Text hotkeyPlaceholder)
            {
                hotkeyPlaceholder.text = "F1-F12, A-Z, or 0-9";
                hotkeyPlaceholder.overflowMode = TextOverflowModes.Overflow;
            }
            ConfigureApiKeyField(_deepSeekKey, out _deepSeekEye);
            UpdateApiKeyFields();
            _pushToTalkKeyField.text = LilithModPlugin.CfgPushToTalkKey.Value ?? "F8";
            if (_pushToTalkKeyField.placeholder is TMP_Text pushToTalkPlaceholder)
            {
                pushToTalkPlaceholder.text = "F1-F12, A-Z, or 0-9";
                pushToTalkPlaceholder.overflowMode = TextOverflowModes.Overflow;
            }

            view.MapRow(_deepSeekKey, TraySettingView.TabMe);
            // Order is decided by who claims last place LAST, so these two blocks
            // read bottom-up: Me ends up API key, Open Speech Input Folder, Help.
            if (_speechFolderLabel != null)
            {
                view.MapRow(_speechFolderLabel, TraySettingView.TabMe);
                Transform row = view.GetRowOf(_speechFolderLabel.transform);
                if (row != null) row.SetAsLastSibling();
            }
            if (_helpLabel != null)
            {
                view.MapRow(_helpLabel, TraySettingView.TabMe);
                // Last of all: Help sits at the very bottom of the tab.
                Transform row = view.GetRowOf(_helpLabel.transform);
                if (row != null) row.SetAsLastSibling();
            }
            view.MapRow(_hotkeyField, TraySettingView.TabControls);
            if (_voiceFolderLabel != null)
            {
                view.MapRow(_voiceFolderLabel, TraySettingView.TabSound);
                // Last sibling overall, which puts it at the bottom of its own tab.
                Transform row = view.GetRowOf(_voiceFolderLabel.transform);
                if (row != null) row.SetAsLastSibling();
            }
            view.MapRow(_pushToTalkKeyField, TraySettingView.TabControls);
            if (_opacity != null) view.MapRow(_opacity, TraySettingView.TabLilith);

            ConfigureNativeVoiceSelector(view);

            // New rows are added after the native tab pass. Re-select the active tab
            // to hide foreign rows, then rebuild so the new rows do not stack.
            view.SelectTab(view._currentTab);
            if (view._rowsContainer != null)
                UIHelper.ForceRebuildLayoutImmediate(view._rowsContainer, 3);

            ApplyLilithOpacity(_opacity != null ? _opacity.value : LilithModPlugin.CfgLilithOpacity.Value);
            LilithModPlugin.Logger.LogInfo("[Settings] API, voice, memory, and display rows ready.");
        }

        private void Sync()
        {
            string deepSeek = _deepSeekKey.text?.Trim() ?? string.Empty;
            if (deepSeek != LilithModPlugin.CfgApiKey.Value) LilithModPlugin.CfgApiKey.Value = deepSeek;
            string hotkey = _hotkeyField?.text?.Trim() ?? string.Empty;
            if (WindowFocus.VirtualKeyFromName(hotkey) > 0 &&
                !string.Equals(hotkey, LilithModPlugin.CfgHotkey.Value, StringComparison.OrdinalIgnoreCase))
                LilithModPlugin.CfgHotkey.Value = hotkey.ToUpperInvariant();
            if (_deepSeekEye != null) _deepSeekRevealed = _deepSeekEye.isOn;
            UpdateApiKeyFields();
            string pushToTalkKey = _pushToTalkKeyField?.text?.Trim() ?? string.Empty;
            // Reject a binding that collides with the open-chat key rather than leaving
            // two actions on one key; the field reverts so the rejection is visible.
            if (WindowFocus.VirtualKeyFromName(pushToTalkKey) > 0 &&
                !string.Equals(pushToTalkKey, LilithModPlugin.CfgPushToTalkKey.Value,
                    StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(pushToTalkKey, LilithModPlugin.CfgHotkey.Value,
                        StringComparison.OrdinalIgnoreCase))
                {
                    _pushToTalkKeyField.text = LilithModPlugin.CfgPushToTalkKey.Value;
                    LilithModPlugin.Logger.LogWarning(
                        $"[Settings] '{pushToTalkKey}' is already the open-chat key; "
                        + "push-to-talk binding unchanged.");
                }
                else
                {
                    LilithModPlugin.CfgPushToTalkKey.Value = pushToTalkKey.ToUpperInvariant();
                }
            }

            if (_opacity != null)
            {
                float opacity = Mathf.Clamp(_opacity.value, 0.2f, 1f);
                if (Math.Abs(opacity - LilithModPlugin.CfgLilithOpacity.Value) > 0.001f)
                    LilithModPlugin.CfgLilithOpacity.Value = opacity;
                ApplyLilithOpacity(opacity);
            }
        }

        private void CaptureChatKey(bool forPushToTalk)
        {
            for (int number = 1; number <= 12; number++)
            {
                int virtualKey = 0x70 + number - 1;
                if (WindowFocus.IsKeyDown(virtualKey))
                {
                    SetCapturedChatKey("F" + number, forPushToTalk);
                    return;
                }
            }
            for (char letter = 'A'; letter <= 'Z'; letter++)
            {
                if (WindowFocus.IsKeyDown(letter))
                {
                    SetCapturedChatKey(letter.ToString(), forPushToTalk);
                    return;
                }
            }
            for (char digit = '0'; digit <= '9'; digit++)
            {
                if (WindowFocus.IsKeyDown(digit))
                {
                    SetCapturedChatKey(digit.ToString(), forPushToTalk);
                    return;
                }
            }
        }

        private void SetCapturedChatKey(string name, bool forPushToTalk)
        {
            // The two bindings are polled independently, so the same key on both would
            // open the chat box every time the microphone is keyed. Refuse the capture.
            string other = forPushToTalk
                ? LilithModPlugin.CfgHotkey.Value
                : LilithModPlugin.CfgPushToTalkKey.Value;
            if (string.Equals(name, other, StringComparison.OrdinalIgnoreCase))
            {
                LilithModPlugin.Logger.LogWarning(
                    $"[Settings] '{name}' is already in use by the other binding; ignored.");
                return;
            }

            TMP_InputField field = forPushToTalk ? _pushToTalkKeyField : _hotkeyField;
            field.text = name;
            field.caretPosition = name.Length;
            if (forPushToTalk)
                LilithModPlugin.CfgPushToTalkKey.Value = name;
            else
                LilithModPlugin.CfgHotkey.Value = name;
            LilithModPlugin.SaveConfig();
            LilithModPlugin.Logger.LogInfo(
                $"[Settings] {(forPushToTalk ? "Push-to-talk" : "Open chat")} key changed to {name}.");
        }

        private void RefreshCurrentTabLayout()
        {
            if (_view == null) return;
            _view.SelectTab(_view._currentTab);
            if (_view._rowsContainer != null)
                UIHelper.ForceRebuildLayoutImmediate(_view._rowsContainer, 3);
        }

        private void UpdateApiKeyFields()
        {
            UpdateApiKeyField(_deepSeekKey, _deepSeekEye, _deepSeekRevealed);
        }

        private static void UpdateApiKeyField(TMP_InputField field, Toggle eye, bool revealed)
        {
            if (field == null) return;
            bool hasKey = !string.IsNullOrWhiteSpace(field.text);
            TMP_InputField.ContentType type = revealed
                ? TMP_InputField.ContentType.Standard
                : TMP_InputField.ContentType.Password;
            if (field.contentType != type)
            {
                field.contentType = type;
                field.ForceLabelUpdate();
            }
            if (eye != null)
                eye.gameObject.SetActive(hasKey);
        }

        private static void ConfigureApiKeyField(TMP_InputField field, out Toggle eye)
        {
            eye = null;
            if (field == null) return;

            // Reserve room at the right of the field for the reveal toggle, so the
            // key text stops before it instead of running underneath.
            const float eyeInset = 6f;
            const float eyeWidth = 28f;
            const float textInset = eyeInset + eyeWidth + 4f;

            if (field.placeholder is TMP_Text placeholder)
            {
                placeholder.text = "Place your API Key";
                placeholder.enableWordWrapping = false;
                placeholder.overflowMode = TextOverflowModes.Overflow;
                Vector4 placeholderMargin = placeholder.margin;
                placeholder.margin = new Vector4(placeholderMargin.x, placeholderMargin.y,
                    textInset, placeholderMargin.w);
            }
            if (field.textComponent != null)
            {
                Vector4 textMargin = field.textComponent.margin;
                field.textComponent.margin = new Vector4(textMargin.x, textMargin.y,
                    textInset, textMargin.w);
            }

            var eyeObject = new GameObject("ApiKeyEye");
            eyeObject.transform.SetParent(field.transform, false);
            var rect = eyeObject.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(1f, 0.5f);
            rect.anchorMax = new Vector2(1f, 0.5f);
            rect.pivot = new Vector2(1f, 0.5f);
            rect.sizeDelta = new Vector2(eyeWidth, 20f);
            rect.anchoredPosition = new Vector2(-eyeInset, 0f);

            var image = eyeObject.AddComponent<Image>();
            image.sprite = GetEyeSprite();
            image.preserveAspect = true;
            image.color = Color.white;

            eye = eyeObject.AddComponent<Toggle>();
            eye.targetGraphic = image;
            eye.graphic = null;
            eye.isOn = false;
        }

        private static Sprite GetEyeSprite()
        {
            if (_eyeSprite != null) return _eyeSprite;

            const int width = 32;
            const int height = 20;
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            var pixels = new Color32[width * height];
            Color32 clear = new Color32(0, 0, 0, 0);
            Color32 white = new Color32(255, 255, 255, 235);
            for (int i = 0; i < pixels.Length; i++) pixels[i] = clear;

            float cx = (width - 1) * 0.5f;
            float cy = (height - 1) * 0.5f;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float dx = (x - cx) / 14f;
                    float dy = (y - cy) / 7f;
                    float ellipse = dx * dx + dy * dy;
                    float pupil = (x - cx) * (x - cx) + (y - cy) * (y - cy);
                    if ((ellipse >= 0.72f && ellipse <= 1.18f) || pupil <= 8f)
                        pixels[y * width + x] = white;
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply();
            _eyeSprite = Sprite.Create(texture, new Rect(0, 0, width, height), new Vector2(0.5f, 0.5f), 100f);
            return _eyeSprite;
        }

        private static void SetWrappedLabel(TMP_Text label, string text)
        {
            if (label == null) return;
            label.text = text;
            label.enableWordWrapping = true;
            label.enableAutoSizing = false;
            label.overflowMode = TextOverflowModes.Overflow;
        }

        private static void SetSingleLineLabel(TMP_Text label, string text)
        {
            if (label == null) return;
            label.text = text;
            label.enableWordWrapping = false;
            label.enableAutoSizing = false;
            label.overflowMode = TextOverflowModes.Overflow;
        }

        private static TMP_Text FindRowLabel(Transform row)
        {
            if (row == null) return null;
            var labels = row.GetComponentsInChildren<TMP_Text>(true);
            for (int i = 0; i < labels.Length; i++)
            {
                TMP_Text label = labels[i];
                if (label != null && !label.transform.IsChildOf(row.GetComponentInChildren<Slider>().transform))
                    return label;
            }
            return labels.Length > 0 ? labels[0] : null;
        }

        private static void ConfigureNativeVoiceSelector(TraySettingView view)
        {
            var buttons = view?._gameVoiceToggleButtons;
            if (buttons == null) return;

            if (buttons._znch != null)
                buttons._znch.gameObject.SetActive(true);
            if (buttons._en != null)
                buttons._en.gameObject.SetActive(false);
            if (buttons._jp != null)
            {
                buttons._jp.gameObject.SetActive(true);
                RenameSynthesisButton(buttons._jp);
                if (buttons._znch != null && buttons._jp.transform.parent == buttons._znch.transform.parent)
                    buttons._jp.transform.SetSiblingIndex(buttons._znch.transform.GetSiblingIndex() + 1);
            }

            SetVoiceSelectionWithoutNotify(buttons);
        }

        private void RestoreSavedVoiceSelection()
        {
            try
            {
                var buttons = _view?._gameVoiceToggleButtons;
                if (buttons != null)
                    SetVoiceSelectionWithoutNotify(buttons);
            }
            catch { }
        }

        private static void SetVoiceSelectionWithoutNotify(TraySettingGameVoiceToggleButtons buttons)
        {
            RenameSynthesisButton(buttons._jp);
            buttons.SetVoiceWithoutNotify(LilithModPlugin.CfgReplaceGameVoice.Value
                ? TraySettingChanged.GameLocalizationVoiceType.Japanese
                : TraySettingChanged.GameLocalizationVoiceType.ChineseSimplified);
        }

        internal static void SetVoiceLanguageFromNative(TraySettingChanged.GameLocalizationVoiceType voiceType)
        {
            bool synthesis = voiceType == TraySettingChanged.GameLocalizationVoiceType.Japanese;
            if (synthesis && !VoiceServiceMonitor.IsAvailable) return;
            if (LilithModPlugin.CfgVoiceSynthesisPreferred.Value == synthesis &&
                LilithModPlugin.CfgReplaceGameVoice.Value == synthesis) return;
            LilithModPlugin.CfgVoiceSynthesisPreferred.Value = synthesis;
            LilithModPlugin.CfgReplaceGameVoice.Value = synthesis && VoiceServiceMonitor.IsAvailable;
            LilithModPlugin.SaveConfig();
            LilithModPlugin.Logger.LogInfo(
                synthesis ? "[Voice] Vocal synthesis selected." : "[Voice] Native Chinese voice selected.");
        }

        /// <summary>
        /// Greys the push-to-talk row while its listener is not running, matching
        /// how Vocal Synthesis behaves when its server is down. The saved preference
        /// is left untouched, so it comes back on by itself once the listener
        /// returns rather than needing to be re-enabled by hand.
        /// </summary>
        /// <summary>Chat is useless without a key, so the binding reflects that.</summary>
        private static bool HasApiKey =>
            !string.IsNullOrWhiteSpace(LilithModPlugin.CfgApiKey.Value);

        private void RefreshChatAvailability()
        {
            if (_hotkeyField == null) return;
            bool available = HasApiKey;
            if (_lastChatAvailability == available) return;
            _lastChatAvailability = available;

            Color color = available ? Color.white : DisabledColor;
            _hotkeyField.interactable = available;
            if (_hotkeyField.textComponent != null) _hotkeyField.textComponent.color = color;
            if (_hotkeyLabel != null) _hotkeyLabel.color = color;
        }

        private void RefreshSpeechAvailability()
        {
            if (_pushToTalkKeyField == null) return;
            // Both are required: the listener turns speech into text, and the key
            // turns that text into a reply. Either missing makes the binding a lie.
            bool available = SpeechInputService.IsAvailable && HasApiKey;
            if (_lastSpeechAvailability == available) return;
            _lastSpeechAvailability = available;

            Color color = available ? Color.white : DisabledColor;
            _pushToTalkKeyField.interactable = available;
            if (_pushToTalkKeyField.textComponent != null)
                _pushToTalkKeyField.textComponent.color = color;
            if (_pushToTalkLabel != null) _pushToTalkLabel.color = color;
        }

        private void RefreshSynthesisAvailability()
        {
            var buttons = _view?._gameVoiceToggleButtons;
            if (buttons?._jp == null) return;
            bool available = VoiceServiceMonitor.IsAvailable;
            if (_lastSynthesisAvailability == available) return;
            _lastSynthesisAvailability = available;

            buttons._jp.enabled = available;
            buttons._jp._allowClickWhenDisabled = false;
            Color color = available ? Color.white : DisabledColor;
            if (buttons._jp._targetImage != null) buttons._jp._targetImage.color = color;
            foreach (TMP_Text label in buttons._jp.GetComponentsInChildren<TMP_Text>(true))
                if (label != null) label.color = color;
            RenameSynthesisButton(buttons._jp);
            SetVoiceSelectionWithoutNotify(buttons);
        }

        /// <summary>
        /// Held so the label can be re-applied when the game language changes. Its
        /// localiser is stripped, so nothing else will ever retranslate it.
        /// </summary>
        private static TMP_Text[] _synthesisLabels;

        private static void RenameSynthesisButton(Component button)
        {
            if (button == null) return;
            _synthesisLabels = button.GetComponentsInChildren<TMP_Text>(true);
            for (int i = 0; i < _synthesisLabels.Length; i++)
            {
                if (_synthesisLabels[i] == null) continue;
                TraySettingView.StripLabelLocalizer(_synthesisLabels[i]);
                _synthesisLabels[i].enableWordWrapping = false;
                _synthesisLabels[i].enableAutoSizing = false;
                _synthesisLabels[i].overflowMode = TextOverflowModes.Overflow;
            }
            ApplySynthesisLabel(UiLanguage());
        }

        private static void ApplySynthesisLabel(string language)
        {
            if (_synthesisLabels == null) return;
            string text =
                language == "ja" ? "音声合成" :
                language == "zh" ? "语音合成" :
                "Vocal Synthesis";
            for (int i = 0; i < _synthesisLabels.Length; i++)
            {
                if (_synthesisLabels[i] != null) _synthesisLabels[i].text = text;
            }
        }

        /// <summary>
        /// Opens the speech input instructions. Deliberately never greyed out: it is
        /// how you find out why speech is unavailable, so it has to work when
        /// everything else is disabled.
        /// </summary>
        private static void OpenSpeechFolder()
        {
            try
            {
                string root = Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".";
                string folder = Path.Combine(root, "speech-setup");
                Directory.CreateDirectory(folder);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = folder,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                LilithModPlugin.Logger.LogWarning("[Settings] Could not open speech input folder: " + ex.Message);
            }
        }

        /// <summary>
        /// Labels on the rows this mod adds follow the game's display language, and
        /// are re-checked on the refresh tick so switching language applies without
        /// a restart - the same contract the speech recogniser uses.
        ///
        /// The native rows are localised by the game itself. These are clones with
        /// their localiser stripped, so nothing else will ever set their text.
        /// Help is excluded on purpose; see where it is assigned.
        /// </summary>
        private void RefreshLabels()
        {
            string language = UiLanguage();
            if (language == _labelLanguage) return;
            _labelLanguage = language;

            bool ja = language == "ja";
            bool zh = language == "zh";

            // Left in English like Help: it is a product name plus "API Key", which
            // is what the service itself calls it in every language.
            SetWrappedLabel(_deepSeekLabel, "DeepSeek\nAPI Key");
            SetWrappedLabel(_hotkeyLabel,
                ja ? "チャットを開く" : zh ? "打开聊天" : "Open chat");
            SetWrappedLabel(_pushToTalkLabel,
                ja ? "音声入力キー" : zh ? "语音输入键" : "Push-to-talk");
            // Two lines on the folder buttons: the row is narrow, so this sits
            // better than one long label.
            SetWrappedLabel(_speechFolderLabel,
                ja ? "音声入力\nフォルダを開く" : zh ? "打开语音\n输入文件夹" : "Open Speech\nInput Folder");
            SetWrappedLabel(_voiceFolderLabel,
                ja ? "音声合成\nフォルダを開く" : zh ? "打开合成\n语音文件夹" : "Open Synth\nVoice Folder");
            SetWrappedLabel(_opacityLabel,
                ja ? "不透明度" : zh ? "不透明度" : "Opacity");
            // Native row, relabelled by this mod and localiser-stripped, so it needs
            // the same treatment as the cloned ones.
            ApplySynthesisLabel(language);
        }

        /// <summary>
        /// The game's own UI language - "en", "ja" or "zh", never null so it is safe
        /// to compare.
        ///
        /// Deliberately NOT PersonaPrompt.CurrentDisplayLanguage(). That returns her
        /// *subtitle* language, which voice-config.ini pins independently of the game
        /// so she can speak Japanese under English subtitles. Using it here meant the
        /// settings labels never moved when the game language changed.
        /// </summary>
        private static string UiLanguage()
        {
            try
            {
                string language = TextVariableResolver.CurrentLanguage() ?? "en";
                if (language.StartsWith("ja", StringComparison.OrdinalIgnoreCase)) return "ja";
                if (language.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) return "zh";
                return "en";
            }
            catch
            {
                return "en";
            }
        }

        private string _labelLanguage;

        /// <summary>
        /// Opens the mod overview in whatever handles .txt. Like the folder buttons
        /// this is never greyed out - it is where the answer to "why is this
        /// greyed out" lives, so it has to work when nothing else does.
        ///
        /// Prefers a translation matching the game language and falls back to
        /// English, so OVERVIEW.ja.txt or OVERVIEW.zh.txt can be added by dropping
        /// the file in - no code change.
        /// </summary>
        private static void OpenHelp()
        {
            try
            {
                string root = Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".";
                string helpFolder = Path.Combine(root, "help");
                string file = Path.Combine(helpFolder, $"OVERVIEW.{UiLanguage()}.txt");
                if (!File.Exists(file)) file = Path.Combine(helpFolder, "OVERVIEW.txt");
                if (!File.Exists(file))
                {
                    // Opening the folder still beats doing nothing visible.
                    string folder = Path.GetDirectoryName(file);
                    Directory.CreateDirectory(folder);
                    LilithModPlugin.Logger.LogWarning(
                        "[Settings] help\\OVERVIEW.txt is missing; opening the folder instead.");
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = folder,
                        UseShellExecute = true
                    });
                    return;
                }
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = file,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                LilithModPlugin.Logger.LogWarning("[Settings] Could not open help: " + ex.Message);
            }
        }

        private static void OpenVoiceFolder()
        {
            try
            {
                VoiceSetup.EnsureFiles();
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = VoiceSetup.FolderPath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                LilithModPlugin.Logger.LogWarning("[Settings] Could not open vocal synthesis folder: " + ex.Message);
            }
        }

        private void ApplyLilithOpacity(float opacity)
        {
            try
            {
                var character = CharacterController.s_activeInstance;
                var skeleton = character?._skeletonAnimation?.Skeleton;
                if (skeleton == null) return;

                float target = Mathf.Clamp(opacity, 0.2f, 1f);
                bool opacityChanged = Math.Abs(_lastAppliedOpacity - target) > 0.005f;
                if (Math.Abs(skeleton.A - target) > 0.005f)
                    skeleton.A = target;
                if (opacityChanged)
                    ModIntegrations.ApplyDialogueOpacity(target);
                if (_lastAppliedOpacity < 0f ||
                    (opacityChanged && LilithModPlugin.CfgLogDiagnostics != null && LilithModPlugin.CfgLogDiagnostics.Value))
                    LilithModPlugin.Logger.LogInfo($"[Settings] Lilith renderer opacity set to {target:0.0}.");
                _lastAppliedOpacity = target;
            }
            catch (Exception ex)
            {
                LilithModPlugin.Logger.LogWarning($"[Settings] Lilith opacity failed: {ex.Message}");
            }
        }

        private void OnDestroy()
        {
            CapturingChatKey = false;
            if (_settingsInteractive)
                WindowFocus.SetSettingsInteractive(false);
        }
    }
}
