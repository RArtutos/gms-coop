using UnityEngine;
using UnityEngine.UI;

namespace GMSCoop
{
    /// <summary>
    /// On-screen status panel. Uses its own uGUI canvas with the highest sorting order,
    /// because the game's menu canvases draw over IMGUI (OnGUI) text.
    /// </summary>
    internal static class Overlay
    {
        private static Text _text;
        private static GameObject _root;

        internal static void SetText(string richText)
        {
            if (_text == null) Create();
            if (_text.text != richText) _text.text = richText;
        }

        private static void Create()
        {
            _root = new GameObject("GMSCoopOverlay");
            Object.DontDestroyOnLoad(_root);

            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = short.MaxValue;
            var scaler = _root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);

            // Dark background panel in the top-left corner; grows with the text.
            var panel = new GameObject("Panel");
            panel.transform.SetParent(_root.transform, false);
            var bg = panel.AddComponent<Image>();
            bg.color = new Color(0f, 0f, 0f, 0.6f);
            bg.raycastTarget = false;
            var prt = panel.GetComponent<RectTransform>();
            prt.anchorMin = prt.anchorMax = prt.pivot = new Vector2(0f, 1f);
            prt.anchoredPosition = new Vector2(10f, -10f);
            var layout = panel.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(10, 10, 6, 6);
            layout.childControlWidth = layout.childControlHeight = true;
            var fitter = panel.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var textGo = new GameObject("Text");
            textGo.transform.SetParent(panel.transform, false);
            _text = textGo.AddComponent<Text>();
            _text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            _text.fontSize = 22;
            _text.color = Color.white;
            _text.supportRichText = true;
            _text.raycastTarget = false;
            _text.horizontalOverflow = HorizontalWrapMode.Overflow;
            _text.verticalOverflow = VerticalWrapMode.Overflow;
        }

        internal static void Destroy()
        {
            if (_root != null) Object.Destroy(_root);
        }
    }
}
