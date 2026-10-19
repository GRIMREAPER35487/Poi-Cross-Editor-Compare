using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Synthos.PoiCrossEditorCompare
{
    /// <summary>
    /// Keeps a pair of Cross Shader Editor windows looking the same: opening or closing a section in
    /// one opens or closes it in the other, and invisible spacers pad whichever side is shorter so
    /// every row both windows show sits at the same height.
    /// </summary>
    static class CrossEditorAlign
    {
        const string SpacerName = "synthos-align-spacer";
        const float Tolerance = 0.5f;
        // An existing spacer is kept while the gap it fills stays within this. Layout snaps to the pixel
        // grid, so a measured gap wobbles slightly; without this a spacer could flip on and off each pass.
        const float Hysteresis = 1.5f;

        // Each window's section open states as of the last pass, against the content they were read from.
        static readonly Dictionary<EditorWindow, (VisualElement content, Dictionary<string, bool> open)> OpenStates =
            new Dictionary<EditorWindow, (VisualElement, Dictionary<string, bool>)>();
        // Windows whose sections the partner should be made to match on the next pass (just paired).
        static readonly HashSet<EditorWindow> MirrorFrom = new HashSet<EditorWindow>();

        public static void Start(EditorWindow leader) => MirrorFrom.Add(leader);

        public static void Stop(EditorWindow window)
        {
            OpenStates.Remove(window);
            MirrorFrom.Remove(window);
            Measured.Remove(window);
            if (window == null) return;
            foreach (var spacer in window.rootVisualElement.Query(name: SpacerName).ToList()) spacer.RemoveFromHierarchy();
        }

        // Safety net for layout changes that leave both content sizes exactly as they were.
        const double RecheckInterval = 1.0;

        // Per window: its content and that content's size as of the last pass.
        static readonly Dictionary<EditorWindow, (VisualElement content, Vector2 size, double time)> Measured =
            new Dictionary<EditorWindow, (VisualElement, Vector2, double)>();

        // Called every editor frame; only does the work when either window's content was rebuilt or
        // resized (a section opened or closed, rows appeared, the window was resized, padding changed).
        public static void Update(EditorWindow a, EditorWindow b, ScrollView viewA, ScrollView viewB)
        {
            double now = EditorApplication.timeSinceStartup;
            bool changedA = Changed(a, viewA.contentContainer, now), changedB = Changed(b, viewB.contentContainer, now);
            if (!changedA && !changedB && !MirrorFrom.Contains(a) && !MirrorFrom.Contains(b)) return;
            Log("pass running");
            try
            {
                SyncExpansion(a, b, viewA.contentContainer, viewB.contentContainer);
                Pad(viewA.contentContainer, viewB.contentContainer);
            }
            catch (Exception e)
            {
                // Log once per kind of failure rather than every frame.
                if (_lastError != e.Message) Debug.LogException(e);
                _lastError = e.Message;
                Log("pass failed: " + e);
            }
            Measured[a] = (viewA.contentContainer, viewA.contentContainer.layout.size, now);
            Measured[b] = (viewB.contentContainer, viewB.contentContainer.layout.size, now);
        }

        static string _lastError;

        // ---- Diagnostics ----

        static System.Text.StringBuilder _log;

        [MenuItem("Poi/Thry/Debug Cross Editor Alignment")]
        static void DebugNextPass()
        {
            _log = new System.Text.StringBuilder();
            _log.AppendLine("[Poi Cross Editor Compare] Alignment pass");
            Log($"paired windows: {CrossEditorHighlight.PairCount}");
            Measured.Clear();
        }

        // Also written to <project>/Temp/SynthosAlignDebug.txt, so it can be read without the Console.
        static readonly string LogPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, "..", "Temp", "SynthosAlignDebug.txt"));

        static void Log(string line)
        {
            if (_log == null) return;
            _log.AppendLine(line);
            try { System.IO.File.WriteAllText(LogPath, _log.ToString()); } catch (System.IO.IOException) { }
        }

        internal static void Note(string line) => Log(line);

        static string Describe(VisualElement e) =>
            e == null ? "null" : $"{e.GetType().Name} name='{e.name}' classes=[{string.Join(",", e.GetClasses())}]";

        // Written as !(x <= y) so a size that was NaN (not laid out yet) counts as changed.
        static bool Changed(EditorWindow window, VisualElement content, double now) =>
            !Measured.TryGetValue(window, out var last) || last.content != content
            || !((last.size - content.layout.size).sqrMagnitude <= Tolerance * Tolerance)
            || now - last.time > RecheckInterval;

        // ---- Section open/close ----

        static void SyncExpansion(EditorWindow a, EditorWindow b, VisualElement contentA, VisualElement contentB)
        {
            var sectionsA = Sections(contentA);
            var sectionsB = Sections(contentB);
            var openA = sectionsA.ToDictionary(kv => kv.Key, kv => IsOpen(kv.Value));
            var openB = sectionsB.ToDictionary(kv => kv.Key, kv => IsOpen(kv.Value));
            var lastA = Last(a, contentA);
            var lastB = Last(b, contentB);
            bool mirrorA = MirrorFrom.Remove(a), mirrorB = MirrorFrom.Remove(b);

            foreach (var kv in openA)
            {
                if (!openB.TryGetValue(kv.Key, out bool stateB) || stateB == kv.Value) continue;
                bool changedA = lastA != null && lastA.TryGetValue(kv.Key, out bool wasA) && wasA != kv.Value;
                bool changedB = lastB != null && lastB.TryGetValue(kv.Key, out bool wasB) && wasB != stateB;
                if ((changedA && !changedB) || (mirrorA && !changedB)) Toggle(sectionsB[kv.Key]);
                else if ((changedB && !changedA) || mirrorB) Toggle(sectionsA[kv.Key]);
            }
            OpenStates[a] = (contentA, openA);
            OpenStates[b] = (contentB, openB);
        }

        static Dictionary<string, bool> Last(EditorWindow window, VisualElement content) =>
            OpenStates.TryGetValue(window, out var last) && last.content == content ? last.open : null;

        static Dictionary<string, VisualElement> Sections(VisualElement content)
        {
            var sections = new Dictionary<string, VisualElement>();
            foreach (var e in content.Query(className: "thry-section").ToList())
            {
                string key = CrossEditorHighlight.Key(e);
                if (key != null && !sections.ContainsKey(key)) sections[key] = e;
            }
            return sections;
        }

        static bool IsOpen(VisualElement section) => section.ClassListContains("thry-section-open");

        static readonly FieldInfo ClickedField = typeof(Clickable).GetField("clicked", BindingFlags.NonPublic | BindingFlags.Instance);

        // Press the section's own fold arrow, so Thry expands it (and saves that) exactly as a click would.
        static void Toggle(VisualElement section)
        {
            var header = section.hierarchy.Children().FirstOrDefault(c => c.ClassListContains("thry-section-header"));
            var fold = header?.Q<Button>(className: "thry-fold-arrow");
            if (fold == null) return;
            if (ClickedField?.GetValue(fold.clickable) is Action clicked) { clicked(); return; }
            using (var e = NavigationSubmitEvent.GetPooled()) { e.target = fold; fold.SendEvent(e); }
        }

        // ---- Padding ----

        // Rows both windows show are matched top to bottom (keeping order), and each time one side's
        // match sits higher than the other's, a spacer goes in above it to make up the difference.
        static void Pad(VisualElement contentA, VisualElement contentB)
        {
            var rowsA = Rows(contentA);
            var rowsB = Rows(contentB);
            var indexB = new Dictionary<string, int>();
            for (int i = 0; i < rowsB.Count; i++) if (!indexB.ContainsKey(rowsB[i].key)) indexB[rowsB[i].key] = i;
            var spacersA = VisibleSpacers(contentA);
            var spacersB = VisibleSpacers(contentB);
            float topA = contentA.worldBound.yMin, topB = contentB.worldBound.yMin;
            Log($"content A: {Describe(contentA)} top={topA} size={contentA.layout.size} rows={rowsA.Count} spacers={spacersA.Count}");
            Log($"content B: {Describe(contentB)} top={topB} size={contentB.layout.size} rows={rowsB.Count} spacers={spacersB.Count}");
            int logged = 0;

            var existingA = Existing(contentA);
            var existingB = Existing(contentB);
            var wanted = new Dictionary<VisualElement, float>();
            float addedA = 0, addedB = 0;
            int lastB = -1;
            foreach (var (key, elementA) in rowsA)
            {
                if (!indexB.TryGetValue(key, out int i) || i <= lastB) continue;
                lastB = i;
                var elementB = rowsB[i].element;
                float yA = Unpadded(elementA, spacersA) - topA + addedA;
                float yB = Unpadded(elementB, spacersB) - topB + addedB;
                float gap = yB - yA;
                if (_log != null && logged++ < 12)
                {
                    var at = gap > 0 ? StackedAncestor(elementA, contentA) : StackedAncestor(elementB, contentB);
                    Log($"match {key}: yA={yA:0.#} yB={yB:0.#} gap={gap:0.#} pad-at={Describe(at)} parent={Describe(at.hierarchy.parent)} parentDir={at.hierarchy.parent?.resolvedStyle.flexDirection}");
                }
                var anchorA = StackedAncestor(elementA, contentA);
                var anchorB = StackedAncestor(elementB, contentB);
                // Positive: a spacer above A's row; negative: above B's. Whole pixels, and the current
                // spacer stays as it is unless the gap really moved.
                float current = existingA.TryGetValue(anchorA, out float hA) ? hA : existingB.TryGetValue(anchorB, out float hB) ? -hB : 0;
                float pad = Mathf.Abs(gap - current) <= Hysteresis ? current : Mathf.Round(gap);
                if (pad > 0) { Want(wanted, anchorA, pad); addedA += pad; }
                else if (pad < 0) { Want(wanted, anchorB, -pad); addedB -= pad; }
            }
            Log($"wanted spacers: {wanted.Count}");
            ApplySpacers(contentA, wanted);
            ApplySpacers(contentB, wanted);
            if (_log != null)
            {
                foreach (var spacer in contentA.Query(name: SpacerName).ToList().Concat(contentB.Query(name: SpacerName).ToList()))
                    Log($"spacer in {Describe(spacer.hierarchy.parent)} height style={spacer.style.height.value.value} resolved={spacer.resolvedStyle.height} display={spacer.resolvedStyle.display}");
                Log($"first rows A: {string.Join(" | ", rowsA.Take(5).Select(r => r.key))}");
                Log($"first rows B: {string.Join(" | ", rowsB.Take(5).Select(r => r.key))}");
                Debug.Log(_log.ToString());
                _log = null;
            }
        }

        // The element each spacer sits above, and the height we gave it.
        static Dictionary<VisualElement, float> Existing(VisualElement content)
        {
            var existing = new Dictionary<VisualElement, float>();
            foreach (var spacer in content.Query(name: SpacerName).ToList())
            {
                var siblings = spacer.hierarchy.parent.hierarchy;
                int index = siblings.IndexOf(spacer);
                if (index + 1 < siblings.childCount) existing[siblings[index + 1]] = spacer.style.height.value.value;
            }
            return existing;
        }

        static void Want(Dictionary<VisualElement, float> wanted, VisualElement at, float height) =>
            wanted[at] = (wanted.TryGetValue(at, out float existing) ? existing : 0) + height;

        // A spacer only pushes things down inside a top-to-bottom container; in a row (like Thry's
        // Presets / rendering mode row) it would just add width. So pad above the nearest ancestor that
        // sits in a column instead.
        static VisualElement StackedAncestor(VisualElement element, VisualElement content)
        {
            for (var v = element; v != null && v != content; v = v.hierarchy.parent)
            {
                var direction = v.hierarchy.parent?.resolvedStyle.flexDirection;
                if (direction == FlexDirection.Column || direction == FlexDirection.ColumnReverse) return v;
            }
            return element;
        }

        static List<(string key, VisualElement element)> Rows(VisualElement content)
        {
            var rows = new List<(string, VisualElement)>();
            content.Query().Where(e => e.ClassListContains("thry-property") || e.ClassListContains("thry-section")).ForEach(e =>
            {
                string key = CrossEditorHighlight.Key(e);
                if (key != null && CrossEditorHighlight.Visible(e)) rows.Add((key, e));
            });
            return rows;
        }

        static List<VisualElement> VisibleSpacers(VisualElement content) =>
            content.Query(name: SpacerName).ToList().Where(CrossEditorHighlight.Visible).ToList();

        // Where the element would be without our spacers: every spacer above it (before it, or before
        // one of its ancestors) pushed it down.
        static float Unpadded(VisualElement element, List<VisualElement> spacers)
        {
            float y = element.worldBound.yMin;
            float pushed = 0;
            foreach (var spacer in spacers)
                if (spacer.worldBound.yMax <= y + Tolerance) pushed += spacer.worldBound.height;
            return y - pushed;
        }

        static void ApplySpacers(VisualElement content, Dictionary<VisualElement, float> wanted)
        {
            // Drop or resize the spacers already there.
            // Through .hierarchy throughout. An element's .parent is its logical parent, which skips
            // content containers (a ScrollView's child reports the ScrollView, not its content element),
            // and IndexOf/Insert/childCount on such a container redirect to that inner element.
            foreach (var spacer in content.Query(name: SpacerName).ToList())
            {
                var siblings = spacer.hierarchy.parent.hierarchy;
                int index = siblings.IndexOf(spacer);
                var next = index + 1 < siblings.childCount ? siblings[index + 1] : null;
                if (next == null || !wanted.TryGetValue(next, out float height)) { spacer.RemoveFromHierarchy(); continue; }
                if (Mathf.Abs(spacer.style.height.value.value - height) > Tolerance) spacer.style.height = height;
                MatchThryNeighbour(spacer);
                wanted.Remove(next);
            }
            // Add the missing ones belonging to this window.
            foreach (var kv in wanted.Where(kv => kv.Key.panel == content.panel && IsInside(kv.Key, content)).ToList())
            {
                var parent = kv.Key.hierarchy.parent;
                if (parent == null) continue;
                var spacer = new VisualElement { name = SpacerName, pickingMode = PickingMode.Ignore };
                spacer.style.height = kv.Value;
                spacer.style.flexShrink = 0;
                // It may carry thry-section (below), whose stylesheet margin would add height we don't track.
                spacer.style.marginTop = spacer.style.marginBottom = spacer.style.marginLeft = spacer.style.marginRight = 0;
                parent.hierarchy.Insert(parent.hierarchy.IndexOf(kv.Key), spacer);
                MatchThryNeighbour(spacer);
                wanted.Remove(kv.Key);
            }
        }

        // Thry gives a subsection extra top margin when the sibling before it isn't a section
        // (RetainedMaterialBody.FollowsProperty). A spacer in that spot would flip that answer, shift
        // the layout, and change the gap the spacer was sized for: the source of the jiggle. So the
        // spacer looks to Thry like whatever was there before it: a section, or nothing at all (which
        // Thry also counts as "not after a property").
        static void MatchThryNeighbour(VisualElement spacer)
        {
            var siblings = spacer.hierarchy.parent.hierarchy;
            VisualElement before = null;
            for (int i = siblings.IndexOf(spacer) - 1; i >= 0 && before == null; i--)
            {
                var sibling = siblings[i];
                if (sibling.style.display == DisplayStyle.None || sibling.name == SpacerName) continue;
                before = sibling;
            }
            spacer.EnableInClassList("thry-section", before == null || before.ClassListContains("thry-section"));
        }

        static bool IsInside(VisualElement e, VisualElement ancestor)
        {
            for (var v = e; v != null; v = v.hierarchy.parent) if (v == ancestor) return true;
            return false;
        }
    }
}
