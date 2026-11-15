using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace LunarLink
{
    // The pieces the window is built from: rows (plain, opening, folders), the boxes under the list, buttons, labels,
    // the "?" tips, and the sprites they're drawn with.
    static partial class Window
    {
        // Quantum Conveyors' menu look: a cream pane whose rows are cream too, told apart by a soft gradient (orange for
        // something live, dark for a heading, grey for something off) and rounded corners; dark text; grey buttons with
        // light text; orange accent; dark tips.
        static readonly Color Panel = new Color(0.937f, 0.922f, 0.882f, 1f), TipBg = new Color(0.165f, 0.165f, 0.196f, 1f),
            Btn = new Color(0.286f, 0.286f, 0.294f, 1f), Accent = new Color(1f, 0.478f, 0.18f, 1f), Light = new Color(0.949f, 0.929f, 0.894f, 1f),
            Ink = new Color(0.11f, 0.133f, 0.196f, 1f), Dim = new Color(0.47f, 0.45f, 0.43f, 1f), Faint = new Color(0.729f, 0.71f, 0.686f, 1f),
            Red = new Color(0.8f, 0.24f, 0.2f, 1f), Green = new Color(0.18f, 0.55f, 0.28f, 1f);
        const string DimHex = "#77736E", AccentHex = "#FF7A2E", RedHex = "#E6524A";

        // a row's parts, in its 600-wide layout: text starts at Left, the +/- button sits at Plus, buttons end at Right
        const float Left = -255f, Plus = 264f, Right = 288f, Gap = 8f;

        // ------------------------------------------------ rows
        enum Look { Live, Heading, Off }

        // the next row down the list, registered under its key
        static RectTransform AddRow(string key, Look look)
        {
            var row = Rect("Row", content, new Vector2(RowW, 46), new Vector2(0, -rowCount++ * RowStep), Panel);
            row.anchorMin = row.anchorMax = row.pivot = new Vector2(0.5f, 1f); row.anchoredPosition = new Vector2(0, -(rowCount - 1) * RowStep);
            EnsureSprites();
            var shade = Rect("Shade", row, new Vector2(RowW, 46), Vector2.zero, null);
            var g = shade.gameObject.AddComponent<Image>();
            g.sprite = look == Look.Off ? grey : look == Look.Heading ? cool : warm; g.type = Image.Type.Sliced;
            g.raycastTarget = false;
            shade.SetAsFirstSibling();
            live[key] = row;
            return row;
        }

        static Image Shade(RectTransform row) => row == null ? null : row.Find("Shade")?.GetComponent<Image>();

        // a grey layer over a row's shade (the flash a reload shows), clear to start with
        static Image Overlay(RectTransform row, Image shade)
        {
            var r = Rect("Flash", row, row.sizeDelta, Vector2.zero, null);
            r.SetSiblingIndex(shade.transform.GetSiblingIndex() + 1);
            var image = r.gameObject.AddComponent<Image>(); image.sprite = grey; image.type = Image.Type.Sliced; image.raycastTarget = false;
            image.color = new Color(1f, 1f, 1f, 0f);
            return image;
        }

        // text from a row's left edge: width w, centred vertically on y
        static Text RowText(RectTransform row, string text, float y, float w, int size, Color colour) =>
            Label(row, text, new Vector2(Left + w / 2f, y), new Vector2(w, y == 0 ? 34 : 22), size, colour, TextAnchor.MiddleLeft);

        // One row that opens: its title (and a second line under it), the whole row and its +/- button opening and
        // closing it; one row open at a time (openRow). Open, it returns the row of actions under it, else null.
        static string openRow;
        static RectTransform OpeningRow(string key, string title, Color titleColour, string line2, Color line2Colour, Look look, out RectTransform header)
        {
            bool open = openRow == key;
            Action toggle = () => { openRow = open ? null : key; opened = open ? null : key; animateNext = true; Draw(true); };
            header = AddRow(key, look);
            Clickable(header, toggle);
            RowText(header, title, line2 == null ? 0 : 10, 270, 15, titleColour);
            if (line2 != null) RowText(header, line2, -12, 300, 13, line2Colour);
            Button(header, open ? "-" : "+", new Vector2(Plus, 0), new Vector2(48, 34), true, toggle, 22);     // + opens, - closes
            if (!open) return null;
            var actionsRow = AddRow(key + ":actions", Look.Heading);
            actionsRow.SetSiblingIndex(header.GetSiblingIndex());           // behind its header while it slides out from under it
            return actionsRow;
        }

        // a folder: its icon, a name with a note after it, and +/- (the API tab's paths)
        static void FolderRow(string key, string name, string note, bool open, Action toggle)
        {
            var row = AddRow(key, Look.Heading);
            Clickable(row, toggle);
            var icon = Rect("Folder", row, new Vector2(22, 22), new Vector2(-270, 0), null);
            var image = icon.gameObject.AddComponent<Image>(); image.sprite = folder; image.preserveAspect = true; image.raycastTarget = false;
            RowText(row, name + "  <size=13><color=" + AccentHex + ">" + note + "</color></size>", 0, 480, 15, Ink);
            Button(row, open ? "-" : "+", new Vector2(Plus, 0), new Vector2(48, 34), true, toggle, 22);
        }

        // buttons along a row from its right edge in, the first given rightmost: (label, width, orange, action or null for greyed)
        static void RowButtons(RectTransform row, float right, params (string label, float width, bool accent, Action act)[] buttons)
        {
            float x = right;
            foreach (var (label, width, accent, act) in buttons)
            {
                var b = Button(row, label, new Vector2(x - width / 2f, 0), new Vector2(width, 34), accent, act, 16, act != null);
                if (act == null) b.gameObject.AddComponent<CanvasGroup>().alpha = 0.45f;
                x -= width + Gap;
            }
        }

        // a message alone in a row (an empty list says why)
        static void NoteRow(string key, string text) => RowText(AddRow(key, Look.Off), text, 0, 520, 14, Dim);

        static void Clickable(RectTransform r, Action act)
        {
            var b = r.gameObject.AddComponent<Button>(); b.targetGraphic = r.GetComponent<Image>();
            actions[r.gameObject.GetInstanceID()] = act;
        }

        // ------------------------------------------------ the boxes under the list and the buttons by the title
        // a half-width box: its label on the left, its control on the right (side 0 left, 1 right)
        static RectTransform Box(int side, string label)
        {
            float w = (listW - 10f) / 2f;
            var box = Rect("Box", bottomBar, new Vector2(w, 44), new Vector2((side == 0 ? -1 : 1) * (w + 10f) / 2f, 0), Panel);
            Label(box, label, new Vector2(-w / 2 + 15 + (w - 150f) / 2, 0), new Vector2(w - 150f, 30), 18, Ink, TextAnchor.MiddleLeft);
            return box;
        }
        static float BoxRight => (listW - 10f) / 4f - 15f;          // a box's control: its right edge, from the box's centre

        // buttons at the title row's ends (the title bar spans the list's width)
        static RectTransform TitleButton(bool left, float offset, string label, float width, bool accent, Action act) =>
            Button(titleBar, label, new Vector2((left ? -1 : 1) * (listW / 2f - offset - width / 2f), 0), new Vector2(width, 36), accent, act, 15, act != null);

        // ------------------------------------------------ buttons and labels
        static RectTransform Rect(string name, Transform parent, Vector2 size, Vector2 pos, Color? bg)
        {
            var go = new GameObject(name); var r = go.AddComponent<RectTransform>(); r.SetParent(parent, false);
            r.anchorMin = r.anchorMax = r.pivot = new Vector2(0.5f, 0.5f); r.sizeDelta = size; r.anchoredPosition = pos;
            if (bg.HasValue) { var img = go.AddComponent<Image>(); img.color = bg.Value; Round(img, rounded); }
            return r;
        }

        static Text Label(Transform parent, string text, Vector2 pos, Vector2 size, int fontSize, Color color, TextAnchor align)
        {
            var t = Rect("Text", parent, size, pos, null).gameObject.AddComponent<Text>();
            t.font = Resources.GetBuiltinResource<Font>("Arial.ttf"); t.text = text; t.fontSize = fontSize; t.color = color; t.alignment = align;
            t.supportRichText = true; t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Overflow; t.verticalOverflow = VerticalWrapMode.Truncate;
            return t;
        }

        // grey with light text, or orange with dark; "×" is the bare close cross; interactable false greys nothing by
        // itself (RowButtons fades it) but makes clicks pass by
        static RectTransform Button(Transform parent, string label, Vector2 pos, Vector2 size, bool accent, Action act,
            int fontSize = 16, bool interactable = true)
        {
            bool close = label == "×";
            var r = Rect("Button", parent, size, pos, close ? new Color(0, 0, 0, 0) : accent ? Accent : Btn);
            Round(r.GetComponent<Image>(), button);
            var b = r.gameObject.AddComponent<Button>(); b.targetGraphic = r.GetComponent<Image>(); b.interactable = interactable;
            if (act != null) actions[r.gameObject.GetInstanceID()] = act;
            Label(r, label, Vector2.zero, size - new Vector2(8, 4), fontSize, close ? Faint : accent ? Ink : Light, TextAnchor.MiddleCenter);
            return r;
        }

        // ------------------------------------------------ "?" tips: a round "?" whose dark tip shows while the mouse is on it
        static readonly List<(RectTransform dot, RectTransform tip)> tips = new List<(RectTransform, RectTransform)>();
        static void Help(Transform parent, Vector2 pos, string text, Vector2 tipSize, Vector2 tipPos)
        {
            EnsureSprites();
            var dot = Rect("Help", parent, new Vector2(28, 28), pos, Btn);
            var img = dot.GetComponent<Image>(); img.sprite = circle; img.type = Image.Type.Sliced;
            Label(dot, "<b>?</b>", Vector2.zero, new Vector2(24, 24), 16, Light, TextAnchor.MiddleCenter);
            var tip = Rect("Tip", parent, tipSize, tipPos, TipBg);
            var tx = Label(tip, text, Vector2.zero, tipSize - new Vector2(26, 22), 13, Light, TextAnchor.UpperLeft);
            tx.horizontalOverflow = HorizontalWrapMode.Wrap; tx.verticalOverflow = VerticalWrapMode.Overflow;
            tip.gameObject.SetActive(false);
            tips.Add((dot, tip));
        }

        static void Tooltips()
        {
            var mouse = Mouse.current; if (mouse == null) return;
            var at = mouse.position.ReadValue();
            for (int i = tips.Count - 1; i >= 0; i--)
            {
                var (dot, tip) = tips[i];
                if (dot == null || tip == null) { tips.RemoveAt(i); continue; }
                bool over = dot.gameObject.activeInHierarchy && RectTransformUtility.RectangleContainsScreenPoint(dot, at, null);
                if (tip.gameObject.activeSelf != over) { tip.gameObject.SetActive(over); if (over) tip.SetAsLastSibling(); }
            }
        }

        // ------------------------------------------------ the sprites (drawn once at 2x, so corners stay smooth)
        static Sprite rounded, button, circle, warm, cool, grey, folder, fadeTop, fadeBottom;

        // made when first needed; each on its own, since a reload can tear one down before the others
        static void EnsureSprites()
        {
            if (rounded == null) rounded = Sprite256(256, 24, "LunarLink Rounded", (x, y) => Color.white);
            if (button == null) button = Sprite256(256, 12, "LunarLink Button", (x, y) => Color.white);
            if (circle == null) circle = Sprite256(256, 128, "LunarLink Circle", (x, y) => Color.white);
            if (warm == null) warm = Gradient(Accent, 0.22f, "LunarLink Warm");
            if (cool == null) cool = Gradient(Ink, 0.24f, "LunarLink Cool");
            if (grey == null) grey = Gradient(new Color(0.48f, 0.48f, 0.48f), 0.22f, "LunarLink Grey");
            if (folder == null) folder = FolderIcon();
        }

        static void DestroySprites()
        {
            foreach (var s in new[] { rounded, button, circle, warm, cool, grey, folder, fadeTop, fadeBottom })
                if (s != null) { UnityEngine.Object.Destroy(s.texture); UnityEngine.Object.Destroy(s); }
            rounded = button = circle = warm = cool = grey = folder = fadeTop = fadeBottom = null;
        }

        static Sprite FolderIcon()
        {
            const int size = 48;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "LunarLink Folder",
                filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color[size * size];
            void Fill(int x0, int x1, int y0, int y1) { for (var y = y0; y <= y1; y++) for (var x = x0; x <= x1; x++) pixels[y * size + x] = Ink; }
            Fill(6, 40, 12, 36); Fill(10, 22, 36, 40);                 // the folder and its tab
            texture.SetPixels(pixels); texture.Apply(false, true);
            return Sprite.Create(texture, new UnityEngine.Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 200f);
        }

        // a soft colour across a row, from alpha on the left to none on the right, with the rows' rounded corners
        static Sprite Gradient(Color c, float alpha, string name) =>
            Sprite256(128, 24, name, (x, y) => new Color(c.r, c.g, c.b, Mathf.Lerp(alpha, 0f, x / 255f)));

        static Sprite Sprite256(int h, int radius, string name, Func<int, int, Color> colour)
        {
            const int w = 256;
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false) { name = name, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            var px = new Color[w * h];
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
            {
                float fx = x + 0.5f, fy = y + 0.5f, cx = Mathf.Clamp(fx, radius, w - radius), cy = Mathf.Clamp(fy, radius, h - radius);
                var c = colour(x, y); c.a *= Mathf.Clamp01(radius + 0.75f - Vector2.Distance(new Vector2(fx, fy), new Vector2(cx, cy)));
                px[y * w + x] = c;
            }
            t.SetPixels(px); t.Apply(false, true);
            return Sprite.Create(t, new UnityEngine.Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 200f, 0, SpriteMeshType.FullRect,
                new Vector4(radius, radius, radius, radius));
        }

        // a rounded surface; small ones stay square
        static void Round(Image img, Sprite sprite)
        {
            var size = img.rectTransform.sizeDelta;
            if (size.x < 24 || size.y < 24) return;
            EnsureSprites(); img.sprite = sprite ?? rounded; img.type = Image.Type.Sliced;
        }

        // the soft edge at the list's top or bottom while there's more to scroll that way
        static Image Fade(Transform parent, bool top, Vector2 pos, float width)
        {
            const int h = 64;
            var texture = new Texture2D(1, h, TextureFormat.RGBA32, false) { name = top ? "LunarLink Fade Top"
                : "LunarLink Fade Bottom", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color[h];
            for (int y = 0; y < h; y++) { float t = y / (float)(h - 1); pixels[y] = new Color(1, 1, 1, top ? t : 1f - t); }
            texture.SetPixels(pixels); texture.Apply(false, true);
            var sprite = Sprite.Create(texture, new UnityEngine.Rect(0, 0, 1, h), new Vector2(0.5f, 0.5f), 100f);
            var before = top ? fadeTop : fadeBottom;               // the last time the window opened
            if (before != null) { UnityEngine.Object.Destroy(before.texture); UnityEngine.Object.Destroy(before); }
            if (top) fadeTop = sprite; else fadeBottom = sprite;
            var image = Rect("Fade", parent, new Vector2(width, 32), pos, null).gameObject.AddComponent<Image>();
            image.sprite = sprite; image.type = Image.Type.Sliced; image.color = Panel; image.raycastTarget = false;
            image.gameObject.SetActive(false);
            return image;
        }
    }
}
