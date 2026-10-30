using System;
using DV.UI.LocoHUD;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace DVTrainSpeedLimit
{
    internal sealed class DrivingReadout
    {
        private RectTransform root;
        private Transform attachedTo;
        private TextMeshProUGUI label;
        private float nextError;
        private float appliedFontSize;

        internal void Refresh(HUDManager manager, string text, bool available, bool overspeed)
        {
            try { Draw(manager, text, available, overspeed); }
            catch (Exception ex)
            {
                Clear();
                if (Time.unscaledTime >= nextError)
                {
                    Main.Log("F4 readout could not be drawn: {0}", ex);
                    nextError = Time.unscaledTime + 30;
                }
            }
        }

        private void Draw(HUDManager manager, string text, bool available, bool overspeed)
        {
            var hud = manager ? manager.currentHUD : null;
            if (!hud || !manager.locoHUDVisible || !Main.Settings.ShowInDrivingUI)
            { if (root) root.gameObject.SetActive(false); return; }
            if (!root || attachedTo != hud.hudRect)
            {
                Clear();
                attachedTo = hud.hudRect;
                var template = hud.GetComponentInChildren<TextMeshProUGUI>(true);
                var go = new GameObject("DVTrainSpeedLimit.Readout", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(LayoutElement));
                root = go.GetComponent<RectTransform>();
                root.SetParent(attachedTo, false);
                root.anchorMin = root.anchorMax = new Vector2(0, 1);
                root.pivot = new Vector2(0, 0);
                go.GetComponent<LayoutElement>().ignoreLayout = true;
                var image = go.GetComponent<Image>();
                image.color = new Color(0.045f, 0.055f, 0.065f, 0.96f);
                image.raycastTarget = false;
                var labelObject = new GameObject("Limit", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
                var rect = labelObject.GetComponent<RectTransform>();
                rect.SetParent(root, false);
                rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
                rect.offsetMin = new Vector2(7, 3); rect.offsetMax = new Vector2(-7, -3);
                label = labelObject.GetComponent<TextMeshProUGUI>();
                if (template && template != label) { label.font = template.font; label.fontSharedMaterial = template.fontSharedMaterial; }
                label.enableAutoSizing = false;
                label.textWrappingMode = TextWrappingModes.NoWrap;
                label.richText = true;
                label.raycastTarget = false;
                label.alignment = TextAlignmentOptions.MidlineLeft;
                appliedFontSize = 0;
                Main.Log("F4 readout attached: hud={0}, parent={1}, font={2}/{3}.", hud.name, attachedTo.name, Main.Settings.FontSize, Main.Settings.DetailFontSize);
            }
            if (appliedFontSize != Main.Settings.FontSize)
            {
                appliedFontSize = Main.Settings.FontSize;
                label.fontSize = appliedFontSize;
                root.sizeDelta = new Vector2(appliedFontSize * 17.5f, appliedFontSize * 3.4f);
            }
            root.anchoredPosition = new Vector2(Main.Settings.HudOffsetX, Main.Settings.HudOffsetY);
            if (!root.gameObject.activeSelf) root.gameObject.SetActive(true);
            if (label.text != text) label.text = text;
            Color color = !available ? new Color(1, 0.8f, 0.4f) : overspeed ? new Color(1, 0.4f, 0.35f) : Color.white;
            if (label.color != color) label.color = color;
        }

        internal void Clear()
        {
            if (root) UnityEngine.Object.Destroy(root.gameObject);
            root = null; label = null; attachedTo = null;
        }
    }
}
