using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace LunarLink
{
    // The pane: a canvas with Quantum Conveyors' own scaler settings, the title row, the tabs, the list (rebuilt when what
    // it shows changes, scrolled, its rows sliding and fading as they open and close), the boxes under it and the line at
    // the foot. The list's rows are laid out 600 units wide (Quantum's size) and stretched to the list's width.
    static partial class Window
    {
        const float RowW = 600f, RowStep = 52f, Ease = 0.22f;      // a row's laid-out width, the step between rows, animation time
        static float listW = RowW;                                 // the list's width: rows are stretched to it

        static RectTransform pane, layout, titleBar, bottomBar, viewport, content, closing;
        static CanvasGroup closingGroup;
        static Text statusText;
        static readonly Image[] tabImages = new Image[4];
        static readonly Text[] tabTexts = new Text[4];
        static Image topFade, bottomFade;

        // Rebuilds the list when what it shows has changed (checked once a second, and after every button).
        static void Draw(bool force)
        {
            if (root == null && !force) return;
            nextRefresh = Time.realtimeSinceStartup + 1f;
            var sig = Signature();
            if (!force && sig == drawn) return;
            drawn = sig;
            if (root == null) BuildPane();
            if (root != null) Refresh();
        }

        // what the tab in view shows, as one string: the list redraws only when it changes
        static string Signature() => tab + "|" + page?.Name + "|" + message + "|" + (ShowModules?.Value ?? false) + "|"
            + PluginSettings.Pending + "|" + CoreWaiting + "|" + restartAt + "|"
            + (tab == Tab.HotMods ? string.Join(",", Hot.Rows().Select(r => r.Name + r.State + r.Error + r.Newer))
             : tab == Tab.Mods ? string.Join(",", BepPlugins.List().Select(e => e.File + e.State))
             : tab == Tab.Api ? Endpoints().Count.ToString() : "") + "|" + UpdatesSignature();

        // The canvas and everything that stays while the window is open. The pane is the old window's size (1040 x 868 at
        // 1080p, scaled by the screen's height); its contents use Quantum's 800 x 600 scaler and its 600-unit-tall layout,
        // scaled to fill the pane, so text and sprites draw at their native size.
        static void BuildPane()
        {
            root = new GameObject("LunarLink Window"); UnityEngine.Object.DontDestroyOnLoad(root);
            var canvas = root.AddComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 31001;
            root.AddComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;   // Quantum's: 800 x 600, by width
            raycaster = root.AddComponent<GraphicRaycaster>();
            Canvas.ForceUpdateCanvases();
            float screenScale = Screen.height / 1080f / Mathf.Max(0.01f, canvas.scaleFactor);   // 1080p units -> canvas units
            float paneW = 1040f * screenScale, paneH = 868f * screenScale, s = screenScale;
            pane = Rect("Pane", root.transform, new Vector2(paneW, paneH), Vector2.zero, Panel);

            // the foot: the line saying what the last button did; the close cross in the corner
            statusText = Label(pane, "", new Vector2(0, -paneH / 2 + 32f * s), new Vector2(paneW - 40f * s, 30f * s),
                Mathf.Max(8, Mathf.RoundToInt(15f * s)), Dim, TextAnchor.MiddleLeft);
            Button(pane, "×", new Vector2(paneW / 2 - 34 * s, paneH / 2 - 36 * s), new Vector2(52 * s, 50 * s), false, Close,
                Mathf.Max(8, Mathf.RoundToInt(34 * s)));

            // the layout: 600 units tall, from the pane's top edge down to the foot's band, as wide as the pane at that scale
            float top = 8f * s, foot = 44f * s, scale = (paneH - top - foot) / 600f, LW = Mathf.Max(640f, (paneW - 32f * s) / scale);
            listW = LW - 40f;
            layout = Rect("Layout", pane, new Vector2(LW, 600), new Vector2(0, (foot - top) / 2f), null);
            layout.localScale = new Vector3(scale, scale, 1f);
            Label(layout, "Lunar Link", new Vector2(0, 278), new Vector2(LW - 20, 44), 30, Ink, TextAnchor.MiddleCenter);
            Help(layout, new Vector2(105, 278), HelpText(), new Vector2(560, 230), new Vector2(0, 120));
            titleBar = Rect("Title Bar", layout, new Vector2(LW - 40, 40), new Vector2(0, 278), null);

            const float gap = 10f; float tabW = Mathf.Min(170f, (LW - 40f - 3 * gap) / 4f), x = -(tabW * 4f + gap * 3f) / 2f + tabW / 2f;
            string[] names = { "Hot Mods", "Mods", "API", "Updates" };
            for (int i = 0; i < 4; i++)
            {
                var t = (Tab)i;
                var b = Button(layout, names[i], new Vector2(x + (tabW + gap) * i, 228), new Vector2(tabW, 40), false,
                    () => { tab = t; page = null; openRow = null; Draw(true); }, 14);
                tabImages[i] = b.GetComponent<Image>(); tabTexts[i] = b.GetComponentInChildren<Text>();
            }

            // the list: masked, its content scrolled up and down; rows closing slide and fade in a layer behind it
            viewport = Rect("List", layout, new Vector2(LW - 20, 446), new Vector2(0, -20.5f), Panel);
            viewport.gameObject.AddComponent<Mask>().showMaskGraphic = true;
            closing = Rect("Closing Rows", viewport, new Vector2(listW, 44), Vector2.zero, null);
            content = Rect("Rows", viewport, new Vector2(listW, 44), Vector2.zero, null);
            closing.anchorMin = closing.anchorMax = closing.pivot = content.anchorMin = content.anchorMax = content.pivot = new Vector2(0.5f, 1f);
            closingGroup = closing.gameObject.AddComponent<CanvasGroup>(); closingGroup.blocksRaycasts = false; closingGroup.interactable = false;
            topFade = Fade(viewport, true, new Vector2(0, 217), LW - 20);
            bottomFade = Fade(viewport, false, new Vector2(0, -217), LW - 20);
            bottomBar = Rect("Boxes", layout, new Vector2(listW, 44), new Vector2(0, -272), null);
            paneActions.UnionWith(actions.Keys);
        }

        // the buttons that stay while the window is open (tabs, the close cross); the rest go with each rebuild
        static readonly HashSet<int> paneActions = new HashSet<int>();

        static string HelpText() =>
            "<b>Hot Mods</b>: the DLLs in BepInEx/hot/. Enable, Disable and Reload, all while the game runs.\n" +
            "<b>Mods</b>: the normal plugins in BepInEx/plugins/. Turning one off or on applies at the next start.\n" +
            "<b>API</b>: every HTTP endpoint on 127.0.0.1:8765, a folder per path, with the module that serves it.\n" +
            "<b>Updates</b>: Lunar Link's releases, its modules and plugins that update themselves. Only a new core needs a restart.\n" +
            "<b>Support file</b>: a zip in BepInEx/ to attach to a bug report (names and ids blanked out).\n\n" +
            KeyLabel + " opens and closes this window; the mouse wheel scrolls the list.";

        static void DestroyPane()
        {
            actions.Clear(); paneActions.Clear(); tips.Clear(); live.Clear(); rowTweens.Clear(); fades.Clear(); fadeIn.Clear(); reloadFlash.Clear();
            if (root != null) UnityEngine.Object.Destroy(root);
            root = null; raycaster = null; drawn = null;
            pane = layout = titleBar = bottomBar = viewport = content = closing = null; closingGroup = null; statusText = null;
            topFade = bottomFade = null; settingText = null;
            scroll = scrollTarget = scrollVelocity = 0f; scrollFromUntil = closingUntil = 0f;
        }

        // ------------------------------------------------ the list
        static readonly Dictionary<string, RectTransform> live = new Dictionary<string, RectTransform>(StringComparer.Ordinal);   // key -> its row now
        static int rowCount;
        static bool animateNext;                   // the next rebuild slides rows into place (a row opened or closed)
        static string opened;                      // the row just opened: the scroll after it shows what opened

        static void Refresh()
        {
            UpdateTabs();
            statusText.text = message ?? "";
            foreach (var id in actions.Keys.Where(id => !paneActions.Contains(id)).ToList()) actions.Remove(id);
            // what was there: the rows that stay slide from where they were; the ones going slide into their parent and fade
            float scrollBefore = scroll, targetBefore = scrollTarget;
            bool animate = animateNext || fadeIn.Count > 0;
            Dictionary<string, RectTransform> before = null;
            Clear(closing, immediate: true);
            closingUntil = 0f; closingGroup.alpha = 1f;
            if (animate)
            {
                before = new Dictionary<string, RectTransform>(live, StringComparer.Ordinal);
                for (int i = content.childCount - 1; i >= 0; i--) content.GetChild(i).SetParent(closing, false);
                closing.sizeDelta = content.sizeDelta; closing.anchoredPosition = content.anchoredPosition;
            }
            else Clear(content, immediate: false);
            live.Clear(); rowTweens.Clear(); rowCount = 0;
            Clear(titleBar, immediate: false); Clear(bottomBar, immediate: false);

            if (page != null) AddSettingsPage(page);
            else switch (tab)
            {
                case Tab.HotMods: AddHotMods(); break;
                case Tab.Mods: AddMods(); break;
                case Tab.Api: AddApi(); break;
                case Tab.Updates: AddUpdates(); break;
            }
            foreach (var r in live.Values) Stretch(r);
            content.sizeDelta = new Vector2(listW, Math.Max(44f, rowCount * RowStep));

            ScrollAfterRefresh(scrollBefore, targetBefore, before);
            if (animate) SlideRows(before);
            animateNext = false; fadeIn.Clear(); reloadFlash.Clear();
        }

        static void Clear(Transform t, bool immediate)
        {
            for (int i = t.childCount - 1; i >= 0; i--)
                if (immediate) UnityEngine.Object.DestroyImmediate(t.GetChild(i).gameObject); else UnityEngine.Object.Destroy(t.GetChild(i).gameObject);
        }

        static void UpdateTabs()
        {
            for (int i = 0; i < 4; i++)
            {
                bool on = (int)tab == i;
                tabImages[i].color = on ? Accent : Btn; tabTexts[i].color = on ? Ink : Light;
            }
            int waiting = ReadUpdates().Waiting;          // a count on Updates when something has an update
            tabTexts[3].text = "Updates" + (waiting > 0 ? "  <color=" + (tab == Tab.Updates ? "#1C2232" : "#FF7A2E") + ">●" + waiting + "</color>" : "");
        }

        // a row is laid out 600 wide; this widens it to the list: what sits on the right keeps its distance from the right
        // edge, what sits on the left from the left edge, left-aligned text grows into the room between, and anything
        // centred spans the new width
        static void Stretch(RectTransform row)
        {
            float dx = (listW - RowW) / 2f;
            if (dx <= 0f || Mathf.Abs(row.sizeDelta.x - RowW) > 0.5f) return;
            for (int i = 0; i < row.childCount; i++)
            {
                var c = row.GetChild(i) as RectTransform; if (c == null) continue;
                var p = c.anchoredPosition; var text = c.GetComponent<Text>();
                bool leftText = text != null
                    && (text.alignment == TextAnchor.MiddleLeft || text.alignment == TextAnchor.UpperLeft
                    || text.alignment == TextAnchor.LowerLeft);
                // left-aligned text that starts in the left half (its left edge, not its centre: a wide line's centre can
                // sit right of the middle) grows both ways, so it still starts at the row's left edge
                if (Mathf.Abs(p.x) < 1f || leftText && p.x - c.sizeDelta.x / 2f < 0f) c.sizeDelta = new Vector2(c.sizeDelta.x + 2f * dx, c.sizeDelta.y);
                else c.anchoredPosition = new Vector2(p.x + (p.x > 0f ? dx : -dx), p.y);
            }
            row.sizeDelta = new Vector2(listW, row.sizeDelta.y);
        }

        // ------------------------------------------------ scrolling
        static float scroll, scrollTarget, scrollVelocity, scrollFrom, scrollTo, scrollFromUntil;
        static float MaxScroll => content == null ? 0f : Mathf.Max(0f, content.rect.height - viewport.rect.height);

        static void ScrollBy(float wheel)
        {
            if (MaxScroll <= 0f) return;
            if (scrollFromUntil > 0f) { scrollFromUntil = 0f; scrollTarget = scroll; scrollVelocity = 0f; }
            scrollTarget = Mathf.Clamp(scrollTarget - (Mathf.Abs(wheel) > 10f ? wheel / 6f : wheel * 30f), 0f, MaxScroll);
        }

        // where the list stands after a rebuild: where it was, except that opening a row scrolls on just far enough to
        // show what opened (the rows below move down), never so far that the opened row goes above the top of the list
        static void ScrollAfterRefresh(float scrollBefore, float targetBefore, Dictionary<string, RectTransform> before)
        {
            float target = Mathf.Clamp(targetBefore, 0f, MaxScroll);
            if (opened != null && before != null && live.TryGetValue(opened, out var row))
            {
                float Top(RectTransform r) => -r.anchoredPosition.y;
                float parentTop = Top(row), shownTo = -1f;
                foreach (var r in live) if (!before.ContainsKey(r.Key)
                    && Top(r.Value) > parentTop) shownTo = Mathf.Max(shownTo, Top(r.Value) + r.Value.rect.height);
                if (shownTo > 0f) target = Mathf.Clamp(Mathf.Max(target, Mathf.Min(shownTo - viewport.rect.height, parentTop)), 0f, MaxScroll);
            }
            opened = null;
            scrollTarget = target; scrollVelocity = 0f;
            if (animateNext && Mathf.Abs(scrollBefore - target) > 0.01f) { scrollFrom = scrollBefore; scrollTo = target;
                scrollFromUntil = Time.unscaledTime + Ease; scroll = scrollBefore; }
            else { scrollFromUntil = 0f; scroll = Mathf.Clamp(scrollBefore, 0f, MaxScroll); }
            PlaceList();
        }

        static void PlaceList()
        {
            content.anchoredPosition = closing.anchoredPosition = new Vector2(0f, scroll);
            topFade.gameObject.SetActive(MaxScroll > 0.01f && scroll > 0.5f);
            bottomFade.gameObject.SetActive(MaxScroll > 0.01f && scroll < MaxScroll - 0.5f);
        }

        // ------------------------------------------------ animation (each frame, LateUpdate)
        sealed class RowTween { public RectTransform Row; public Vector2 From, To; public CanvasGroup Group; public float FadeStart, Until; }
        sealed class ImageFade { public Image Image; public float From, To, Start, Until; }
        static readonly List<RowTween> rowTweens = new List<RowTween>();
        static readonly List<ImageFade> fades = new List<ImageFade>();
        static readonly HashSet<string> fadeIn = new HashSet<string>(StringComparer.Ordinal);       // rows whose shade fades back in (turned on or off)
        static readonly HashSet<string> reloadFlash = new HashSet<string>(StringComparer.Ordinal);  // ... with a grey flash first (reloaded)
        static float closingUntil; static bool closingRemoveOnly;

        static float Smooth(float t) => t * t * (3f - 2f * t);

        static void Animate()
        {
            float now = Time.unscaledTime;
            for (int i = rowTweens.Count - 1; i >= 0; i--)
            {
                var a = rowTweens[i]; if (a.Row == null) { rowTweens.RemoveAt(i); continue; }
                float t = Mathf.Clamp01(1f - (a.Until - now) / Ease);
                a.Row.anchoredPosition = Vector2.LerpUnclamped(a.From, a.To, Smooth(t));
                if (a.Group != null) a.Group.alpha = Smooth(Mathf.Clamp01((now - a.FadeStart) / Mathf.Max(0.001f, a.Until - a.FadeStart)));
                if (t >= 1f) rowTweens.RemoveAt(i);
            }
            for (int i = fades.Count - 1; i >= 0; i--)
            {
                var a = fades[i]; if (a.Image == null) { fades.RemoveAt(i); continue; }
                float t = Mathf.Clamp01((now - a.Start) / Mathf.Max(0.001f, a.Until - a.Start));
                var c = a.Image.color; c.a = Mathf.LerpUnclamped(a.From, a.To, Smooth(t)); a.Image.color = c;
                if (t >= 1f) fades.RemoveAt(i);
            }
            if (content != null)
            {
                if (scrollFromUntil > 0f)
                {
                    float t = Mathf.Clamp01(1f - (scrollFromUntil - now) / Ease);
                    scroll = Mathf.LerpUnclamped(scrollFrom, scrollTo, Smooth(t));
                    if (t >= 1f) scrollFromUntil = 0f;
                    PlaceList();
                }
                else if (Mathf.Abs(scroll - scrollTarget) > 0.01f)
                {
                    scrollTarget = Mathf.Clamp(scrollTarget, 0f, MaxScroll);
                    scroll = Mathf.SmoothDamp(scroll, scrollTarget, ref scrollVelocity, 0.09f, 2600f, Time.unscaledDeltaTime);
                    if (Mathf.Abs(scroll - scrollTarget) < 0.05f) { scroll = scrollTarget; scrollVelocity = 0f; }
                    PlaceList();
                }
            }
            if (closingUntil > 0f && closingGroup != null)
            {
                float t = Mathf.Clamp01(1f - (closingUntil - now) / Ease);
                if (!closingRemoveOnly) closingGroup.alpha = 1f - Smooth(t);
                if (closingRemoveOnly ? now >= closingUntil : t >= 1f) { Clear(closing, immediate: true); closingGroup.alpha = 1f; closingUntil = 0f; }
            }
        }

        // After a rebuild: rows that stay slide from where they were; new rows under an opened one fade in as they slide;
        // a row turned on or off fades its shade back in (a reloaded one flashes grey first); rows that went slide into
        // their parent row and fade out.
        static void SlideRows(Dictionary<string, RectTransform> before)
        {
            float now = Time.unscaledTime;
            foreach (var row in live)
            {
                var to = row.Value.anchoredPosition;
                var from = before.TryGetValue(row.Key, out var old) ? old.anchoredPosition : animateNext ? to + new Vector2(0, 16f) : to;
                CanvasGroup group = null;
                if (fadeIn.Contains(row.Key) && Shade(row.Value) is Image shade)
                {
                    var c = shade.color; c.a = 0f; shade.color = c;
                    if (reloadFlash.Contains(row.Key))
                    {
                        var grey = Overlay(row.Value, shade);
                        fades.Add(new ImageFade { Image = grey, From = 0f, To = 1f, Start = now + 0.11f, Until = now + 0.22f });
                        fades.Add(new ImageFade { Image = grey, From = 1f, To = 0f, Start = now + 0.22f, Until = now + 0.33f });
                        fades.Add(new ImageFade { Image = shade, From = 0f, To = 1f, Start = now + 0.33f, Until = now + 0.44f });
                    }
                    else fades.Add(new ImageFade { Image = shade, From = 0f, To = 1f, Start = now + 0.11f, Until = now + 0.22f });
                }
                else if (!before.ContainsKey(row.Key) && row.Key.EndsWith(":actions", StringComparison.Ordinal))
                {
                    group = row.Value.gameObject.AddComponent<CanvasGroup>(); group.alpha = 0f;
                }
                if ((from - to).sqrMagnitude > 0.01f || group != null)
                {
                    row.Value.anchoredPosition = from;
                    rowTweens.Add(new RowTween { Row = row.Value, From = from, To = to, Group = group, FadeStart = now, Until = now + Ease });
                }
            }
            bool anyClosing = false;
            foreach (var old in before)
            {
                if (old.Value == null) continue;
                if (live.ContainsKey(old.Key))
                {
                    // the old copy of a row turned on or off: its shade fades out while the new one's fades in
                    if (fadeIn.Contains(old.Key) && Shade(old.Value) is Image shade)
                    {
                        var c = shade.color; c.a = 1f; shade.color = c;
                        fades.Add(new ImageFade { Image = shade, From = 1f, To = 0f, Start = now, Until = now + 0.11f });
                        anyClosing = true;
                    }
                    else UnityEngine.Object.DestroyImmediate(old.Value.gameObject);
                    continue;
                }
                var target = old.Key.EndsWith(":actions", StringComparison.Ordinal)
                    && live.TryGetValue(old.Key.Substring(0, old.Key.Length - 8), out var parent)
                    ? parent.anchoredPosition : old.Value.anchoredPosition;
                if ((old.Value.anchoredPosition - target).sqrMagnitude > 0.01f)
                    rowTweens.Add(new RowTween { Row = old.Value, From = old.Value.anchoredPosition, To = target, FadeStart = now, Until = now + Ease });
                anyClosing = true;
            }
            if (anyClosing) { closingRemoveOnly = !animateNext && fadeIn.Count > 0; closingUntil = now + (closingRemoveOnly ? 0.11f : Ease); }
        }
    }
}
