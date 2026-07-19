using System;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Il2CppInterop.Runtime;

namespace LilithMod
{
    public static class GameStyle
    {
        public static void Apply(Image background, TMP_InputField ownField,
            TextMeshProUGUI inputText, TextMeshProUGUI placeholderText,
            Transform ownRoot)
        {
            try
            {
                if (ownField == null || background == null || inputText == null || placeholderText == null || ownRoot == null)
                    return;

                TMP_InputField donor = null;

                // Enumerate all TMP_InputFields in the scene, including inactive ones
                var allFields = Resources.FindObjectsOfTypeAll(Il2CppType.Of<TMP_InputField>());
                foreach (var candidate in allFields)
                {
                    var field = candidate.TryCast<TMP_InputField>();
                    if (field == null)
                        continue;

                    // Skip our own UI
                    if (field == ownField)
                        continue;

                    // Skip if transform is a descendant of ownRoot
                    if (field.transform.IsChildOf(ownRoot))
                        continue;

                    // Prefer a donor with a valid text component and font
                    if (field.textComponent != null && field.textComponent.font != null)
                    {
                        donor = field;
                        break;
                    }
                }

                if (donor == null)
                {
                    LilithModPlugin.Logger.LogInfo("[GameStyle] Could not find a game input field with a valid text component and font to copy style from.");
                    return;
                }

                // --- Copy background image ---
                Image donorImage = donor.gameObject.GetComponent<Image>();
                if (donorImage == null && donor.transform.parent != null)
                    donorImage = donor.transform.parent.GetComponent<Image>();

                if (donorImage != null)
                {
                    if (donorImage.sprite != null) background.sprite = donorImage.sprite;
                    if (donorImage.material != null) background.material = donorImage.material;
                    background.type = donorImage.type;
                    background.color = donorImage.color;
                    background.pixelsPerUnitMultiplier = donorImage.pixelsPerUnitMultiplier;
                }

                // --- Copy text styling ---
                var donorText = donor.textComponent;

                // Font and other typographic settings
                inputText.font = donorText.font;
                inputText.fontSize = donorText.fontSize;
                inputText.fontStyle = donorText.fontStyle;
                inputText.characterSpacing = donorText.characterSpacing;
                inputText.color = donorText.color;   // basic solid colour

                placeholderText.font = donorText.font;
                placeholderText.fontSize = donorText.fontSize;
                placeholderText.fontStyle = donorText.fontStyle;
                placeholderText.characterSpacing = donorText.characterSpacing;

                // Placeholder colour: use donor’s placeholder colour if it is a TextMeshProUGUI
                if (donor.placeholder != null && donor.placeholder is TextMeshProUGUI)
                {
                    var donorPlaceholder = donor.placeholder.TryCast<TextMeshProUGUI>();
                    if (donorPlaceholder != null)
                        placeholderText.color = donorPlaceholder.color;
                }
                else
                {
                    placeholderText.color = donorText.color;
                }

                // --- Log what we used ---
                LilithModPlugin.Logger.LogInfo($"[GameStyle] Adopted style from '{donor.gameObject.name}' using font '{donorText.font.name}'.");
            }
            catch (Exception ex)
            {
                LilithModPlugin.Logger.LogWarning($"[GameStyle] Apply failed: {ex}");
            }
        }
    }
}