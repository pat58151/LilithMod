using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
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
        private bool? _lastWakeWordAvailability;
        private TMP_InputField _pushToTalkKeyField;
        private Slider _opacity;
        private TMP_Text _opacityLabel;
        private Slider _musicVolume;
        private TMP_Text _musicVolumeLabel;
        private ButtonToggle _allowOpenAppsToggle;
        private TMP_Text _allowOpenAppsLabel;
        private ButtonToggle _wakeWordToggle;
        private TMP_Text _wakeWordLabel;
        private float _nextSync;
        private float _nextOpacityRefresh;
        private float _lastAppliedOpacity = -1f;
        private int _pendingOpacityDialogueDirection;
        private float _opacityDialogueAt;
        private const float OpacityDialogueDelay = 0.45f;
        private bool _settingsInteractive;
        private bool _settingsVisible;
        private bool _deepSeekRevealed;
        private bool? _lastSynthesisAvailability;
        private static TMP_Text _synthesisStatusText;
        private static Sprite _eyeSprite;
        private static Sprite _hintSprite;
        private RectTransform _wakeWordHintRect;
        private Image _wakeWordHintImage;
        private TMP_Text _wakeWordHintText;
        private RectTransform _wakeWordTipRect;
        private TMP_Text _wakeWordTipText;
        private TMP_Text _chatStatusText;
        private TMP_Text _pushToTalkStatusText;
        private TMP_Text _wakeWordStatusText;
        private TMP_Text _deepSeekStatusText;
        private enum ApiKeyValidationState { Missing, Checking, Valid, Invalid, Unavailable }
        private ApiKeyValidationState _apiKeyValidation = ApiKeyValidationState.Missing;
        private string _apiKeyValidationInput;
        private int _apiKeyValidationGeneration;
        private static readonly HttpClient ApiKeyValidationClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(8)
        };

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
                RefreshWakeWordAvailability();
                RefreshChatAvailability();
                RefreshLabels();
            }

            // Tooltips follow the cursor every frame.
            RefreshWakeWordTooltip();
            TryShowOpacityDialogue();
            ApplyMusicVolumeScale();

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
            // Suppress live hotkeys while either binding captures input.
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
            // Activate cloned rows that inherited a hidden state.
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
                // Keep chat before speech in reading order.
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

            // Music volume: scales the game's BGM source, which also carries
            // tracks played from the music folder. Sits directly under the native
            // voice volume slider it was cloned from.
            _musicVolume = TraySettingView.CloneVolumeRow(
                sliderRow, "LilithMusicVolume", "LilithMusicVolume", "Music volume", 1);
            if (_musicVolume != null)
            {
                _musicVolume.minValue = 0f;
                _musicVolume.maxValue = 1f;
                _musicVolume.wholeNumbers = false;
                _musicVolume.value = Mathf.Clamp01(LilithModPlugin.CfgMusicVolume.Value);
                Transform musicVolumeRow = view.GetRowOf(_musicVolume.transform);
                _musicVolumeLabel = FindRowLabel(musicVolumeRow);
                TraySettingView.StripLabelLocalizer(_musicVolumeLabel);
                if (musicVolumeRow != null) musicVolumeRow.gameObject.SetActive(true);
            }

            BuildAppRows(view);

            _deepSeekLabel = deepSeekLabel;
            _hotkeyLabel = hotkeyLabel;
            _pushToTalkLabel = pushToTalkKeyLabel;
            _deepSeekStatusText = BuildInlineStatus(
                _deepSeekLabel, _deepSeekKey.transform, "LilithDeepSeekStatus", 0.6f);
            _chatStatusText = BuildInlineStatus(
                _hotkeyLabel, _hotkeyField.transform, "LilithChatStatus", 0.6f);
            _pushToTalkStatusText = BuildInlineStatus(
                _pushToTalkLabel, _pushToTalkKeyField.transform,
                "LilithPushToTalkStatus", 0.6f);
            // Underline action rows to distinguish them from settings.
            if (_helpLabel != null)
            {
                _helpLabel.richText = true;
                // Left in English deliberately: "Help" reads as itself in every
                // language this game ships, and the file it opens is English anyway.
                SetWrappedLabel(_helpLabel, "<u>Help</u>");
            }
            // Reapply localization to rebuilt rows.
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
            // Speech setup belongs beside its push-to-talk control.
            if (_speechFolderLabel != null)
            {
                view.MapRow(_speechFolderLabel, TraySettingView.TabControls);
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
            if (_musicVolume != null) view.MapRow(_musicVolume, TraySettingView.TabSound);

            ConfigureNativeVoiceSelector(view);
            OrderMeRows(view);
            OrderControlsRows(view);

            // New rows are added after the native tab pass. Re-select the active tab
            // to hide foreign rows, then rebuild so the new rows do not stack.
            view.SelectTab(view._currentTab);
            if (view._rowsContainer != null)
                UIHelper.ForceRebuildLayoutImmediate(view._rowsContainer, 3);

            ApplyLilithOpacity(_opacity != null ? _opacity.value : LilithModPlugin.CfgLilithOpacity.Value);
            LilithModPlugin.Logger.LogInfo("[Settings] API, voice, memory, and display rows ready.");
        }

        /// <summary>Places native and added Me rows in one stable reading order.</summary>
        private void OrderMeRows(TraySettingView view)
        {
            Transform nameRow = view._yourNameInputField == null
                ? null : view.GetRowOf(view._yourNameInputField.transform);
            Transform birthdayRow = view._calendarView == null
                ? null : view.GetRowOf(view._calendarView.transform);
            Transform apiRow = _deepSeekKey == null
                ? null : view.GetRowOf(_deepSeekKey.transform);
            Transform notificationRow = view._noteNotificationToggle == null
                ? null : view.GetRowOf(view._noteNotificationToggle.transform);
            Transform notesRow = FindRowByText(view,
                "view notes", "ノートを見る", "查看便签", "查看笔记");
            Transform helpRow = _helpLabel == null
                ? null : view.GetRowOf(_helpLabel.transform);

            Transform previous = null;
            foreach (Transform row in new[]
            {
                nameRow, birthdayRow, apiRow, notificationRow,
                notesRow, helpRow
            })
            {
                if (row == null) continue;
                if (previous != null && row.parent == previous.parent)
                    row.SetSiblingIndex(previous.GetSiblingIndex() + 1);
                previous = row;
            }
        }

        private void OrderControlsRows(TraySettingView view)
        {
            Transform chatRow = _hotkeyField == null
                ? null : view.GetRowOf(_hotkeyField.transform);
            Transform pushToTalkRow = _pushToTalkKeyField == null
                ? null : view.GetRowOf(_pushToTalkKeyField.transform);
            Transform speechFolderRow = _speechFolderLabel == null
                ? null : view.GetRowOf(_speechFolderLabel.transform);

            if (chatRow != null && pushToTalkRow != null &&
                chatRow.parent == pushToTalkRow.parent)
                pushToTalkRow.SetSiblingIndex(chatRow.GetSiblingIndex() + 1);
            if (pushToTalkRow != null && speechFolderRow != null &&
                pushToTalkRow.parent == speechFolderRow.parent)
                speechFolderRow.SetSiblingIndex(pushToTalkRow.GetSiblingIndex() + 1);
        }

        private static Transform FindRowByText(TraySettingView view, params string[] labels)
        {
            foreach (TMP_Text text in view.GetComponentsInChildren<TMP_Text>(true))
            {
                if (text == null || string.IsNullOrWhiteSpace(text.text)) continue;
                string value = text.text.Replace("\n", " ").Trim().ToLowerInvariant();
                for (int i = 0; i < labels.Length; i++)
                {
                    if (value != labels[i]) continue;
                    Transform row = view.GetRowOf(text.transform);
                    if (row != null) return row;
                }
            }
            return null;
        }

        /// <summary>Builds the app-launch and wake-word settings rows.</summary>
        private void BuildAppRows(TraySettingView view)
        {
            // --- Toggle row (Allow open apps) ---
            Transform lockRow = view.GetRowOf(view._closeMovementToggle.transform);
            if (lockRow == null)
            {
                LilithModPlugin.Logger.LogWarning("[Settings] Could not find the Lock Move row to clone the apps toggle.");
                return;
            }
            GameObject toggleRowObj = UnityEngine.Object.Instantiate(lockRow.gameObject, lockRow.parent);
            toggleRowObj.name = "LilithAllowOpenApps";

            // Cloned ButtonToggle rows arrive without the native event subscription.
            _allowOpenAppsToggle = toggleRowObj.GetComponentInChildren<ButtonToggle>(true);
            if (_allowOpenAppsToggle != null)
            {
                _allowOpenAppsToggle.SetValue(LilithModPlugin.CfgAllowOpenApps.Value, false);
            }
            else
            {
                LilithModPlugin.Logger.LogWarning("[Settings] Cloned apps toggle row has no ButtonToggle component.");
            }

            // The label is the row's first text that is NOT part of the Toggle control.
            _allowOpenAppsLabel = null;
            var toggleTexts = toggleRowObj.GetComponentsInChildren<TMP_Text>(true);
            for (int i = 0; i < toggleTexts.Length; i++)
            {
                TMP_Text candidate = toggleTexts[i];
                if (candidate == null) continue;
                if (_allowOpenAppsToggle != null &&
                    candidate.transform.IsChildOf(_allowOpenAppsToggle.transform)) continue;
                _allowOpenAppsLabel = candidate;
                break;
            }
            if (_allowOpenAppsLabel == null && toggleTexts.Length > 0)
                _allowOpenAppsLabel = toggleTexts[0];
            if (_allowOpenAppsLabel != null)
            {
                TraySettingView.StripLabelLocalizer(_allowOpenAppsLabel);
                SetWrappedLabel(_allowOpenAppsLabel, "Allow Lilith\nto open Apps");
            }

            toggleRowObj.SetActive(true);
            if (_allowOpenAppsToggle != null)
                view.MapRow(_allowOpenAppsToggle, TraySettingView.TabLilith);

            // --- Toggle row (Wake word) ---
            // Same clone-and-poll shape as the apps toggle above.
            GameObject wakeRowObj = UnityEngine.Object.Instantiate(lockRow.gameObject, lockRow.parent);
            wakeRowObj.name = "LilithWakeWord";
            _wakeWordToggle = wakeRowObj.GetComponentInChildren<ButtonToggle>(true);
            if (_wakeWordToggle != null)
            {
                _wakeWordToggle.SetValue(LilithModPlugin.CfgWakeWord.Value, false);
            }
            else
            {
                LilithModPlugin.Logger.LogWarning("[Settings] Cloned wake word row has no ButtonToggle component.");
            }
            _wakeWordLabel = null;
            var wakeTexts = wakeRowObj.GetComponentsInChildren<TMP_Text>(true);
            for (int i = 0; i < wakeTexts.Length; i++)
            {
                TMP_Text candidate = wakeTexts[i];
                if (candidate == null) continue;
                if (_wakeWordToggle != null &&
                    candidate.transform.IsChildOf(_wakeWordToggle.transform)) continue;
                _wakeWordLabel = candidate;
                break;
            }
            if (_wakeWordLabel == null && wakeTexts.Length > 0)
                _wakeWordLabel = wakeTexts[0];
            if (_wakeWordLabel != null)
            {
                TraySettingView.StripLabelLocalizer(_wakeWordLabel);
                SetWrappedLabel(_wakeWordLabel, "Wake word");
                if (_wakeWordToggle != null)
                    BuildWakeWordHint(_wakeWordToggle, _wakeWordLabel);
                _wakeWordStatusText = BuildInlineStatus(
                    _wakeWordLabel, _wakeWordToggle.transform,
                    "LilithWakeWordStatus", 0.5f, 30f);
            }
            wakeRowObj.SetActive(true);
            if (_wakeWordToggle != null)
                view.MapRow(_wakeWordToggle, TraySettingView.TabLilith);

            // --- Action row (Lilith's list) ---
            // Clone the native action template to avoid inherited click handlers.
            Transform actionRow = view.GetRowOf(view._openMusicFolderLabel.transform);
            Transform listRow = null;
            if (actionRow != null)
            {
                TMP_Text listLabel = view.CloneActionRow(
                    actionRow, "LilithAllowedAppsList",
                    Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(
                        new System.Action(AppLauncher.OpenAllowedList)));
                if (listLabel != null)
                {
                    listLabel.richText = true;
                    // Keep the editable app list reachable in every state.
                    SetWrappedLabel(listLabel, "<u>Lilith's list</u>");
                    listRow = view.GetRowOf(listLabel.transform);
                    if (listRow != null)
                    {
                        listRow.gameObject.SetActive(true);
                        view.MapRow(listLabel, TraySettingView.TabLilith);
                    }
                }
            }

            // Order, below the realm schedule slider: wake word, then the apps
            // toggle, then its list row.
            Transform toggleRow = toggleRowObj.transform;
            Transform wakeRow = wakeRowObj.transform;
            Transform realmScheduleRow = view._adjustFantasyScheduleSlider != null
                ? view.GetRowOf(view._adjustFantasyScheduleSlider.transform) : null;
            if (realmScheduleRow != null && wakeRow.parent == realmScheduleRow.parent)
                wakeRow.SetSiblingIndex(realmScheduleRow.GetSiblingIndex() + 1);
            if (toggleRow.parent == wakeRow.parent)
                toggleRow.SetSiblingIndex(wakeRow.GetSiblingIndex() + 1);
            if (listRow != null && listRow.parent == toggleRow.parent)
                listRow.SetSiblingIndex(toggleRow.GetSiblingIndex() + 1);

            // Fresh clones arrive default-coloured; style for current availability
            // now instead of waiting for an availability flip that may never come.
            StyleToggleRow(_allowOpenAppsToggle, _allowOpenAppsLabel, HasValidApiKey);
            StyleToggleRow(_wakeWordToggle, _wakeWordLabel, WakeWordAvailable);
        }

        /// <summary>
        /// Greys a cloned toggle row the way Vocal Synthesis greys its button. The
        /// saved preference is left alone, so the toggle recovers by itself when
        /// whatever it needs comes back.
        /// </summary>
        private static void StyleToggleRow(ButtonToggle toggle, TMP_Text label, bool available)
        {
            if (toggle == null) return;
            toggle.enabled = available;
            if (toggle._button != null) toggle._button.interactable = available;
            Color color = available ? Color.white : DisabledColor;
            if (toggle._buttonImage != null) toggle._buttonImage.color = color;
            foreach (TMP_Text text in toggle.GetComponentsInChildren<TMP_Text>(true))
                if (text != null) text.color = color;
            if (label != null) label.color = color;
        }

        private void Sync()
        {
            string deepSeek = _deepSeekKey.text?.Trim() ?? string.Empty;
            if (deepSeek != LilithModPlugin.CfgApiKey.Value) LilithModPlugin.CfgApiKey.Value = deepSeek;
            if (!string.Equals(deepSeek, _apiKeyValidationInput, StringComparison.Ordinal))
                ScheduleApiKeyValidation(deepSeek);
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
                float previous = LilithModPlugin.CfgLilithOpacity.Value;
                if (Math.Abs(opacity - previous) > 0.001f)
                {
                    LilithModPlugin.CfgLilithOpacity.Value = opacity;
                    _pendingOpacityDialogueDirection = opacity < previous ? -1 : 1;
                    _opacityDialogueAt = Time.unscaledTime + OpacityDialogueDelay;
                }
                ApplyLilithOpacity(opacity);
            }

            if (_musicVolume != null)
            {
                float musicVolume = Mathf.Clamp01(_musicVolume.value);
                if (Math.Abs(musicVolume - LilithModPlugin.CfgMusicVolume.Value) > 0.001f)
                    LilithModPlugin.CfgMusicVolume.Value = musicVolume;
            }

            // Polled rather than listener-driven: the native UnityEvent was severed on
            // the clone, so this is what carries a click through to the config.
            if (_allowOpenAppsToggle != null &&
                _allowOpenAppsToggle.IsOn != LilithModPlugin.CfgAllowOpenApps.Value)
            {
                LilithModPlugin.CfgAllowOpenApps.Value = _allowOpenAppsToggle.IsOn;
                LilithModPlugin.SaveConfig();
                LilithModPlugin.Logger.LogInfo(
                    "[Settings] Allow Lilith to open apps set to " + _allowOpenAppsToggle.IsOn + ".");
            }
            if (_wakeWordToggle != null &&
                _wakeWordToggle.IsOn != LilithModPlugin.CfgWakeWord.Value)
            {
                LilithModPlugin.CfgWakeWord.Value = _wakeWordToggle.IsOn;
                LilithModPlugin.SaveConfig();
                SpeechInputService.SyncWakeWordFlag();
                LilithModPlugin.Logger.LogInfo(
                    "[Settings] Wake word set to " + _wakeWordToggle.IsOn + ".");
            }
        }

        /// <summary>
        /// Scales the game's BGM source by the configured music volume. A value the
        /// mod did not write is a fresh intent from the game (track start, fade
        /// step) and becomes the new base; the source is then held at base x knob,
        /// so the slider works in both directions and survives track changes. At
        /// 1.0 the base is restored once and the source is left alone.
        /// </summary>
        private void ApplyMusicVolumeScale()
        {
            try
            {
                float knob = Mathf.Clamp01(LilithModPlugin.CfgMusicVolume.Value);
                var audio = AudioManager.instance;
                var bgm = audio != null ? audio.source_BGM : null;
                if (bgm == null) return;

                float current = bgm.volume;
                if (knob >= 0.999f)
                {
                    // Hand the source back to the game at its own level.
                    if (_musicVolumeWritten >= 0f &&
                        Math.Abs(current - _musicVolumeWritten) <= 0.0001f)
                        bgm.volume = _musicVolumeBase;
                    _musicVolumeWritten = -1f;
                    return;
                }

                if (_musicVolumeWritten < 0f ||
                    Math.Abs(current - _musicVolumeWritten) > 0.0001f)
                    _musicVolumeBase = current;

                float target = _musicVolumeBase * knob;
                if (Math.Abs(current - target) > 0.0001f) bgm.volume = target;
                _musicVolumeWritten = bgm.volume;
            }
            catch { }
        }

        private float _musicVolumeWritten = -1f;
        private float _musicVolumeBase = 1f;

        private async void ScheduleApiKeyValidation(string key)
        {
            _apiKeyValidationInput = key ?? string.Empty;
            int generation = ++_apiKeyValidationGeneration;
            if (string.IsNullOrWhiteSpace(key))
            {
                _apiKeyValidation = ApiKeyValidationState.Missing;
                return;
            }

            // Custom OpenAI-compatible services may not expose DeepSeek's models
            // endpoint. Keep their existing non-empty-key behaviour unchanged.
            string baseUrl = LilithModPlugin.CfgBaseUrl.Value ?? string.Empty;
            if (baseUrl.IndexOf("api.deepseek.com", StringComparison.OrdinalIgnoreCase) < 0)
            {
                _apiKeyValidation = ApiKeyValidationState.Valid;
                return;
            }

            _apiKeyValidation = ApiKeyValidationState.Checking;
            await Task.Delay(700);
            if (generation != _apiKeyValidationGeneration) return;

            ApiKeyValidationState result = await ValidateDeepSeekApiKeyAsync(key, baseUrl);
            if (generation == _apiKeyValidationGeneration &&
                string.Equals(key, _apiKeyValidationInput, StringComparison.Ordinal))
                _apiKeyValidation = result;
        }

        private static async Task<ApiKeyValidationState> ValidateDeepSeekApiKeyAsync(
            string key, string baseUrl)
        {
            try
            {
                var configured = new Uri(baseUrl);
                var endpoint = new UriBuilder(configured.Scheme, configured.Host, configured.Port, "models").Uri;
                using (var request = new HttpRequestMessage(HttpMethod.Get, endpoint))
                {
                    request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
                    using (HttpResponseMessage response = await ApiKeyValidationClient.SendAsync(request))
                    {
                        if (response.IsSuccessStatusCode) return ApiKeyValidationState.Valid;
                        if (response.StatusCode == HttpStatusCode.Unauthorized ||
                            response.StatusCode == HttpStatusCode.Forbidden)
                            return ApiKeyValidationState.Invalid;
                        return ApiKeyValidationState.Unavailable;
                    }
                }
            }
            catch
            {
                return ApiKeyValidationState.Unavailable;
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

        /// <summary>
        /// A "?" disc trailing the toggle, and the panel it reveals on hover. Anchored
        /// to the toggle's right edge rather than placed at a fixed offset, so it
        /// follows whatever width the cloned row settles on.
        /// </summary>
        private void BuildWakeWordHint(ButtonToggle toggle, TMP_Text label)
        {
            BuildWakeWordTooltip(label);

            var hint = new GameObject("LilithWakeWordHint");
            hint.transform.SetParent(toggle.transform, false);
            _wakeWordHintRect = hint.AddComponent<RectTransform>();
            _wakeWordHintRect.anchorMin = new Vector2(1f, 0.5f);
            _wakeWordHintRect.anchorMax = new Vector2(1f, 0.5f);
            _wakeWordHintRect.pivot = new Vector2(0f, 0.5f);
            _wakeWordHintRect.sizeDelta = new Vector2(HintSize, HintSize);
            _wakeWordHintRect.anchoredPosition = new Vector2(8f, 0f);

            _wakeWordHintImage = hint.AddComponent<Image>();
            _wakeWordHintImage.sprite = GetHintSprite();
            _wakeWordHintImage.preserveAspect = true;
            _wakeWordHintImage.raycastTarget = false;

            var markObject = UnityEngine.Object.Instantiate(label.gameObject, hint.transform);
            markObject.name = "QuestionMark";
            _wakeWordHintText = markObject.GetComponent<TMP_Text>();
            TraySettingView.StripLabelLocalizer(_wakeWordHintText);
            RectTransform markRect = _wakeWordHintText.rectTransform;
            markRect.anchorMin = Vector2.zero;
            markRect.anchorMax = Vector2.one;
            markRect.pivot = new Vector2(0.5f, 0.5f);
            markRect.offsetMin = Vector2.zero;
            markRect.offsetMax = Vector2.zero;
            _wakeWordHintText.text = "?";
            _wakeWordHintText.alignment = TextAlignmentOptions.Center;
            _wakeWordHintText.enableAutoSizing = false;
            _wakeWordHintText.enableWordWrapping = false;
            _wakeWordHintText.fontSize = 17f;
            _wakeWordHintText.color = Color.white;
            _wakeWordHintText.raycastTarget = false;
        }

        private void BuildWakeWordTooltip(TMP_Text sourceLabel)
        {
            // Parented to the view root and drawn last: a row-level parent would let
            // the next row's background cover it.
            var panel = new GameObject("LilithWakeWordTip");
            panel.transform.SetParent(_view.transform, false);
            _wakeWordTipRect = panel.AddComponent<RectTransform>();
            // Grows left and up from the mark, which sits at the row's right edge -
            // the other way would push it off the panel.
            _wakeWordTipRect.pivot = new Vector2(1f, 0f);
            _wakeWordTipRect.sizeDelta = new Vector2(236f, 52f);

            var background = panel.AddComponent<Image>();
            background.color = new Color(0.05f, 0.05f, 0.07f, 0.94f);
            background.raycastTarget = false;

            // Cloned from the row's own label so the tooltip inherits a font that can
            // render at all - the game's is not one of TMP's defaults.
            var textObject = UnityEngine.Object.Instantiate(sourceLabel.gameObject, panel.transform);
            textObject.name = "Text";
            var text = textObject.GetComponent<TMP_Text>();
            TraySettingView.StripLabelLocalizer(text);
            var textRect = text.rectTransform;
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.pivot = new Vector2(0.5f, 0.5f);
            textRect.offsetMin = new Vector2(10f, 6f);
            textRect.offsetMax = new Vector2(-10f, -6f);
            text.alignment = TextAlignmentOptions.Center;
            text.enableAutoSizing = false;
            text.enableWordWrapping = true;
            text.fontSize = Mathf.Max(10f, sourceLabel.fontSize * 0.85f);
            text.color = Color.white;
            text.raycastTarget = false;
            _wakeWordTipText = text;

            panel.transform.SetAsLastSibling();
            panel.SetActive(false);
        }

        /// <summary>
        /// Hover is polled rather than handled: this window runs WS_EX_NOACTIVATE, so
        /// Unity's pointer events never fire and the cursor has to come from Win32.
        /// </summary>
        private void RefreshWakeWordTooltip()
        {
            if (_wakeWordTipRect == null || _wakeWordHintRect == null) return;

            bool hovering = false;
            if (_settingsVisible && _wakeWordHintImage != null && _wakeWordHintImage.isActiveAndEnabled &&
                PointerFocus.TryGetUnityScreenPoint(out Vector2 point))
                hovering = RectTransformUtility.RectangleContainsScreenPoint(_wakeWordHintRect, point, null);

            if (hovering == _wakeWordTipRect.gameObject.activeSelf) return;
            if (hovering)
            {
                _wakeWordTipRect.position = _wakeWordHintRect.position;
                _wakeWordTipRect.anchoredPosition += new Vector2(HintSize, HintSize * 0.5f);
            }
            _wakeWordTipRect.gameObject.SetActive(hovering);
        }

        private const float HintSize = 26f;

        /// <summary>
        /// A ring with a "?" cut through it, drawn once and reused. Rendered at 64px
        /// for a mark displayed at 26, so the curve stays clean when the UI scales up.
        /// </summary>
        private static Sprite GetHintSprite()
        {
            if (_hintSprite != null) return _hintSprite;

            const int size = 64;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var pixels = new Color32[size * size];
            Color32 clear = new Color32(0, 0, 0, 0);
            Color32 white = new Color32(255, 255, 255, 235);
            for (int i = 0; i < pixels.Length; i++) pixels[i] = clear;

            float centre = (size - 1) * 0.5f;
            const float outer = 30f;
            const float inner = 24f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x - centre;
                    float dy = y - centre;
                    float distance = Mathf.Sqrt(dx * dx + dy * dy);
                    if (distance > outer || distance < inner) continue;
                    // Feathered by the half-pixel the edge falls short of a full one,
                    // so the ring does not read as a staircase.
                    float edge = Mathf.Min(outer - distance, distance - inner);
                    byte alpha = (byte)(white.a * Mathf.Clamp01(edge));
                    pixels[y * size + x] = new Color32(white.r, white.g, white.b, alpha);
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply();
            _hintSprite = Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
            return _hintSprite;
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

        /// <summary>Adds a small centred reason beneath an unavailable setting.</summary>
        private static TMP_Text BuildInlineStatus(
            TMP_Text source, Transform control, string objectName,
            float fontScale = 0.5f, float height = 16f)
        {
            if (source == null || control == null) return null;

            var statusObject = UnityEngine.Object.Instantiate(source.gameObject, control);
            statusObject.name = objectName;
            TMP_Text status = statusObject.GetComponent<TMP_Text>();
            TraySettingView.StripLabelLocalizer(status);
            RectTransform rect = status.rectTransform;
            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(1f, 0f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(0f, -2f);
            rect.sizeDelta = new Vector2(0f, height);
            status.alignment = TextAlignmentOptions.Center;
            status.enableAutoSizing = false;
            status.enableWordWrapping = false;
            status.overflowMode = TextOverflowModes.Overflow;
            status.fontSize = Mathf.Max(8f, source.fontSize * fontScale);
            status.color = DisabledColor;
            status.raycastTarget = false;
            status.gameObject.SetActive(false);
            return status;
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
            buttons.SetVoiceWithoutNotify(LilithModPlugin.CfgVoiceSynthesisPreferred.Value
                ? TraySettingChanged.GameLocalizationVoiceType.Japanese
                : TraySettingChanged.GameLocalizationVoiceType.ChineseSimplified);
        }

        internal static void SetVoiceLanguageFromNative(TraySettingChanged.GameLocalizationVoiceType voiceType)
        {
            bool synthesis = voiceType == TraySettingChanged.GameLocalizationVoiceType.Japanese;
            if (LilithModPlugin.CfgVoiceSynthesisPreferred.Value == synthesis &&
                LilithModPlugin.CfgReplaceGameVoice.Value ==
                    (synthesis && VoiceServiceMonitor.IsAvailable)) return;
            LilithModPlugin.CfgVoiceSynthesisPreferred.Value = synthesis;
            LilithModPlugin.CfgReplaceGameVoice.Value = synthesis && VoiceServiceMonitor.IsAvailable;
            if (!synthesis) LlmChatController.StopSynthPlaybackForNativeVoice();
            LilithModPlugin.SaveConfig();
            LilithModPlugin.Logger.LogInfo(
                synthesis ? "[Voice] Vocal synthesis selected." : "[Voice] Native Chinese voice selected.");
        }

        /// <summary>Chat is useless without a key, so the binding reflects that.</summary>
        private static bool HasApiKey =>
            !string.IsNullOrWhiteSpace(LilithModPlugin.CfgApiKey.Value);

        private bool HasValidApiKey =>
            HasApiKey && _apiKeyValidation == ApiKeyValidationState.Valid;

        private string ApiKeyRequirementText =>
            _apiKeyValidation == ApiKeyValidationState.Invalid
                ? "Valid API key required"
                : _apiKeyValidation == ApiKeyValidationState.Checking
                    ? "Checking API key"
                    : _apiKeyValidation == ApiKeyValidationState.Unavailable
                        ? "API key validation unavailable"
                        : "DeepSeek API key required";

        /// <summary>
        /// All three are load-bearing: the listener carries the audio, the model
        /// recognises her name in it, and the key turns what follows into a reply.
        /// </summary>
        private bool WakeWordAvailable =>
            SpeechInputService.IsAvailable && SpeechInputService.WakeWordModelAvailable &&
            HasValidApiKey;

        private void RefreshChatAvailability()
        {
            if (_hotkeyField == null) return;
            if (_deepSeekStatusText != null)
            {
                _deepSeekStatusText.text = "DeepSeek API key invalid";
                _deepSeekStatusText.gameObject.SetActive(
                    _apiKeyValidation == ApiKeyValidationState.Invalid);
            }
            bool available = HasValidApiKey;
            if (_chatStatusText != null)
            {
                _chatStatusText.text = ApiKeyRequirementText;
                _chatStatusText.gameObject.SetActive(!available);
            }
            if (_lastChatAvailability == available) return;
            _lastChatAvailability = available;

            Color color = available ? Color.white : DisabledColor;
            _hotkeyField.interactable = available;
            if (_hotkeyField.textComponent != null) _hotkeyField.textComponent.color = color;
            if (_hotkeyLabel != null) _hotkeyLabel.color = color;
            // Everything she does with a reply needs the key too.
            StyleToggleRow(_allowOpenAppsToggle, _allowOpenAppsLabel, available);
        }

        /// <summary>
        /// Greys the push-to-talk row while its listener is not running, as Vocal
        /// Synthesis does when its server is down. The saved preference is left
        /// alone, so it returns by itself rather than needing re-enabling by hand.
        /// </summary>
        private void RefreshSpeechAvailability()
        {
            if (_pushToTalkKeyField == null) return;
            // Both are required: the listener turns speech into text, and the key
            // turns that text into a reply. Either missing makes the binding a lie.
            bool available = SpeechInputService.IsAvailable && HasValidApiKey;
            if (_pushToTalkStatusText != null)
            {
                _pushToTalkStatusText.text = !HasValidApiKey && !SpeechInputService.IsAvailable
                    ? _apiKeyValidation == ApiKeyValidationState.Invalid
                        ? "Valid API Key and speech service required"
                        : "API key and speech service required"
                    : !HasValidApiKey
                        ? ApiKeyRequirementText
                        : "Speech service unavailable";
                _pushToTalkStatusText.gameObject.SetActive(!available);
            }
            if (_lastSpeechAvailability == available) return;
            _lastSpeechAvailability = available;

            Color color = available ? Color.white : DisabledColor;
            _pushToTalkKeyField.interactable = available;
            if (_pushToTalkKeyField.textComponent != null)
                _pushToTalkKeyField.textComponent.color = color;
            if (_pushToTalkLabel != null) _pushToTalkLabel.color = color;
        }

        /// <summary>
        /// Tracked apart from the push-to-talk and chat rows because it depends on a
        /// third thing they do not: the model file, which an install run can drop in
        /// while the game is already up. Sharing their cached state would swallow
        /// that arrival whenever the listener and key had not also changed.
        /// </summary>
        private void RefreshWakeWordAvailability()
        {
            if (_wakeWordToggle == null) return;
            bool available = WakeWordAvailable;
            if (_wakeWordStatusText != null)
            {
                _wakeWordStatusText.text = !HasValidApiKey
                    ? ApiKeyRequirementText
                    : !SpeechInputService.IsAvailable
                        ? "Speech service\nunavailable"
                        : "Wake-word model unavailable";
                _wakeWordStatusText.gameObject.SetActive(!available);
            }
            if (_lastWakeWordAvailability == available) return;
            _lastWakeWordAvailability = available;
            StyleToggleRow(_wakeWordToggle, _wakeWordLabel, available);
            // The hint greys with the row, but keeps answering on hover: knowing what
            // the setting does is most useful while it is out of reach.
            if (_wakeWordHintImage != null)
                _wakeWordHintImage.color = available ? Color.white : DisabledColor;
            if (_wakeWordHintText != null)
                _wakeWordHintText.color = available ? Color.white : DisabledColor;
        }

        private void RefreshSynthesisAvailability()
        {
            var buttons = _view?._gameVoiceToggleButtons;
            if (buttons?._jp == null) return;
            RenameSynthesisButton(buttons._jp);
            EnsureSynthesisStatus(buttons._jp);
            bool available = VoiceServiceMonitor.IsAvailable;
            if (_synthesisStatusText != null)
            {
                _synthesisStatusText.text = SynthesisStatusText(UiLanguage());
                _synthesisStatusText.gameObject.SetActive(!available);
            }
            if (_lastSynthesisAvailability == available) return;
            _lastSynthesisAvailability = available;

            // Keep the choice clickable while offline. The grey style and inline
            // status explain that speech will resume when the service returns.
            buttons._jp.enabled = true;
            buttons._jp._allowClickWhenDisabled = true;
            Color color = available ? Color.white : DisabledColor;
            if (buttons._jp._targetImage != null) buttons._jp._targetImage.color = color;
            foreach (TMP_Text label in buttons._jp.GetComponentsInChildren<TMP_Text>(true))
                if (label != null) label.color = color;
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
                if (_synthesisLabels[i] == null ||
                    _synthesisLabels[i].gameObject.name == "LilithSynthesisStatus") continue;
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
                if (_synthesisLabels[i] != null &&
                    _synthesisLabels[i].gameObject.name != "LilithSynthesisStatus")
                    _synthesisLabels[i].text = text;
            }
        }

        /// <summary>Adds an inline status beneath the synthesis choice.</summary>
        private static void EnsureSynthesisStatus(Component button)
        {
            if (_synthesisStatusText != null || button == null ||
                _synthesisLabels == null) return;

            TMP_Text source = null;
            for (int i = 0; i < _synthesisLabels.Length; i++)
            {
                if (_synthesisLabels[i] != null &&
                    _synthesisLabels[i].gameObject.name != "LilithSynthesisStatus")
                {
                    source = _synthesisLabels[i];
                    break;
                }
            }
            if (source == null) return;

            var statusObject = UnityEngine.Object.Instantiate(source.gameObject, source.transform);
            statusObject.name = "LilithSynthesisStatus";
            _synthesisStatusText = statusObject.GetComponent<TMP_Text>();
            TraySettingView.StripLabelLocalizer(_synthesisStatusText);
            RectTransform rect = _synthesisStatusText.rectTransform;
            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(1f, 0f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(18f, -2f);
            rect.sizeDelta = new Vector2(0f, 16f);
            _synthesisStatusText.alignment = TextAlignmentOptions.Top;
            _synthesisStatusText.enableAutoSizing = false;
            _synthesisStatusText.enableWordWrapping = false;
            _synthesisStatusText.overflowMode = TextOverflowModes.Overflow;
            _synthesisStatusText.fontSize = Mathf.Max(8f, source.fontSize * 0.5f);
            _synthesisStatusText.color = new Color(0.75f, 0.55f, 0.55f, 1f);
            _synthesisStatusText.raycastTarget = false;
        }

        private static string SynthesisStatusText(string language)
        {
            return language == "ja" ? "合成サービスはオフラインです" :
                language == "zh" ? "合成服务未运行" :
                "Service unavailable";
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
        /// Labels on the rows this mod adds follow the game's display language,
        /// re-checked on the refresh tick so switching applies without a restart.
        /// These are clones of native rows with their localiser stripped, so nothing
        /// else will ever set their text. Help is excluded; see where it is assigned.
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
                ja ? "音声合成\nフォルダを開く" : zh ? "打开合成\n语音文件夹" : "Open Vocal\nSynth Folder");
            SetWrappedLabel(_opacityLabel,
                ja ? "不透明度" : zh ? "不透明度" : "Opacity");
            SetWrappedLabel(_musicVolumeLabel,
                ja ? "音楽の音量" : zh ? "音乐音量" : "Music volume");
            SetWrappedLabel(_allowOpenAppsLabel,
                ja ? "アプリ起動を\n許可" : zh ? "允许莉莉丝\n打开应用" : "Allow Lilith\nto open Apps");
            // No parenthetical: the hint mark beside the toggle carries that now.
            SetWrappedLabel(_wakeWordLabel,
                ja ? "ウェイクワード" : zh ? "唤醒词" : "Wake word");
            SetWrappedLabel(_wakeWordTipText,
                ja ? "名前を呼ぶと応えてくれる。" : zh ? "呼唤她的名字，她就会回应。"
                   : "She answers when you call her name.");
            // Native row, relabelled by this mod and localiser-stripped, so it needs
            // the same treatment as the cloned ones.
            ApplySynthesisLabel(language);
        }

        /// <summary>
        /// The game's own UI language - "en", "ja" or "zh", never null. Deliberately
        /// NOT PersonaPrompt.CurrentDisplayLanguage(), which returns her *subtitle*
        /// language: voice-config.ini pins that independently, so using it here left
        /// the settings labels unmoved when the game language changed.
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
        /// Opens the mod overview in whatever handles .txt. Never greyed out: it is
        /// where the answer to "why is this greyed out" lives, so it has to work when
        /// nothing else does. Prefers a translation matching the game language and
        /// falls back to English, so OVERVIEW.ja.txt drops in with no code change.
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

        private void TryShowOpacityDialogue()
        {
            if (_pendingOpacityDialogueDirection == 0 ||
                Time.unscaledTime < _opacityDialogueAt) return;

            bool lower = _pendingOpacityDialogueDirection < 0;
            _pendingOpacityDialogueDirection = 0;
            LlmChatController.ShowFixedDialogue(
                OpacityDialogueText(lower, PersonaPrompt.CurrentVoiceLanguage()),
                OpacityDialogueText(lower, PersonaPrompt.CurrentDisplayLanguage()));
        }

        private static string OpacityDialogueText(bool lower, string language)
        {
            if (language == "ja")
                return lower
                    ? "おお…リリスが透明になっていくよ。"
                    : "あっ…リリスがまたはっきり見えてきたね。";
            if (language == "zh")
                return lower
                    ? "哦……莉莉丝正在变透明。"
                    : "啊……莉莉丝又渐渐清晰了。";
            return lower
                ? "Ooh... Lilith is turning invisible."
                : "Ah... Lilith is coming back into view.";
        }

        private void OnDestroy()
        {
            CapturingChatKey = false;
            if (_settingsInteractive)
                WindowFocus.SetSettingsInteractive(false);
        }
    }
}
