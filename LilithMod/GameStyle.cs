using System;
using Il2CppInterop.Runtime;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LilithMod
{
    /// <summary>
    /// Makes the mod's chat box look like the game's own dialogue bar rather than a
    /// hand-built grey rectangle.
    ///
    /// The donor is <c>DialogueBubbleUI</c> - the wide, thin, softly-lit strip Lilith speaks
    /// through - not one of the settings input fields. The single most important thing
    /// copied is <c>fontSharedMaterial</c>: TMP keeps outline, glow and face colour on the
    /// material, so copying only the font asset yields plain text with none of the look.
    /// </summary>
    public static class GameStyle
    {
        private static bool s_warned;

        /// <summary>
        /// Copies the dialogue bar's appearance onto the supplied UI. Layout is untouched -
        /// alignment, wrapping, overflow and every RectTransform stay as the caller set
        /// them, because the caller owns centring and scrolling.
        /// </summary>
        public static void Apply(Image background, TextMeshProUGUI inputText,
                                 TextMeshProUGUI placeholderText, Transform ownRoot)
        {
            try
            {
                var donorText = FindDialogueText(ownRoot);
                if (donorText == null)
                {
                    if (!s_warned)
                    {
                        s_warned = true;
                        LilithModPlugin.Logger.LogInfo(
                            "[GameStyle] No dialogue bar found to copy; keeping the plain look.");
                    }
                    return;
                }

                ApplyTextLook(inputText, donorText, false);
                ApplyTextLook(placeholderText, donorText, true);
                ApplyBackground(background, donorText);

                LilithModPlugin.Logger.LogInfo(
                    $"[GameStyle] Adopted the dialogue bar look from '{donorText.name}' "
                    + $"(font '{(donorText.font != null ? donorText.font.name : "?")}', "
                    + $"material '{(donorText.fontSharedMaterial != null ? donorText.fontSharedMaterial.name : "?")}').");
            }
            catch (Exception ex)
            {
                LilithModPlugin.Logger.LogWarning($"[GameStyle] Styling failed, keeping defaults: {ex.Message}");
            }
        }

        // The bubble is usually inactive when the pet is idle, so FindObjectOfType is not
        // enough - FindObjectsOfTypeAll also returns inactive objects.
        private static TextMeshProUGUI FindDialogueText(Transform ownRoot)
        {
            var bubbles = Resources.FindObjectsOfTypeAll(Il2CppType.Of<DialogueBubbleUI>());
            for (int i = 0; i < bubbles.Length; i++)
            {
                var bubble = bubbles[i].TryCast<DialogueBubbleUI>();
                if (bubble == null) continue;

                // Never style from our own UI - that mistake is how an earlier attempt
                // adopted TMP's Latin-only default font from the box it was styling.
                if (ownRoot != null && bubble.transform.IsChildOf(ownRoot)) continue;

                var t = bubble._dialogueText;
                if (t != null && t.font != null) return t;
            }
            return null;
        }

        private static void ApplyTextLook(TextMeshProUGUI target, TextMeshProUGUI donor, bool isPlaceholder)
        {
            if (target == null) return;

            target.font = donor.font;

            // Carries the outline and glow. Without this the text is flat.
            if (donor.fontSharedMaterial != null)
                target.fontSharedMaterial = donor.fontSharedMaterial;

            target.fontStyle = donor.fontStyle;
            target.characterSpacing = donor.characterSpacing;

            var c = donor.color;
            if (isPlaceholder)
                c.a *= 0.45f; // hint text reads as secondary
            target.color = c;
        }

        private static void ApplyBackground(Image background, TextMeshProUGUI donorText)
        {
            if (background == null) return;

            var donorImage = FindBackdropImage(donorText);
            if (donorImage == null || donorImage.sprite == null) return;

            background.sprite = donorImage.sprite;
            background.type = donorImage.type;
            background.color = donorImage.color;
            if (donorImage.material != null)
                background.material = donorImage.material;
        }

        // The backdrop is the strip behind the words: walk up from the text and take the
        // first Image found, which is the bubble's own panel rather than an option button.
        private static Image FindBackdropImage(TextMeshProUGUI donorText)
        {
            var t = donorText.transform;
            for (int depth = 0; depth < 4 && t != null; depth++)
            {
                var img = t.GetComponent<Image>();
                if (img != null && img.sprite != null) return img;
                t = t.parent;
            }

            // Fall back to any Image among the bubble's descendants.
            var root = donorText.transform.parent != null ? donorText.transform.parent : donorText.transform;
            var images = root.GetComponentsInChildren<Image>(true);
            for (int i = 0; i < images.Length; i++)
            {
                if (images[i] != null && images[i].sprite != null) return images[i];
            }
            return null;
        }
    }
}
