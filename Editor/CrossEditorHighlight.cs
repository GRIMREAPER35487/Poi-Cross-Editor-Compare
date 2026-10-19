using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UIElements;

namespace Synthos.PoiCrossEditorCompare
{
    /// <summary>
    /// Pairs two Cross Shader Editor windows and highlights, in both, every property whose value differs
    /// between their enabled materials, plus the headers of the sections those properties sit in.
    /// Paired windows are docked side by side, kept lined up, and scroll together.
    /// Everything runs each editor frame but only does real work when something changed.
    /// </summary>
    static class CrossEditorHighlight
    {
        // What kind of difference an element shows. Green and yellow are avoided: Thry uses them for animated dots.
        internal enum Kind { None, Value, Texture, Toggle, Contains }

        static readonly Color ValueColor = new Color(1f, 0.55f, 0.1f);    // orange: a setting differs
        static readonly Color TextureColor = new Color(0.72f, 0.45f, 1f); // purple: a texture or its tiling differs
        static readonly Color ToggleColor = new Color(0.3f, 0.62f, 1f);   // blue: section on in one, off in the other
        static readonly Color AnimatedColor = new Color(1f, 0.4f, 0.7f);  // pink: marked Animated in one, not the other
        const float TintAlpha = 0.18f;
        const float BarWidth = 3f;
        // Sections that only contain differences: a thin, dim bar and a count, no tint.
        static readonly Color ContainsBar = new Color(1f, 1f, 1f, 0.35f);
        const float ContainsBarWidth = 2f;
        const string CountName = "synthos-diff-count";
        // Safety nets for changes the cheap checks can't see (e.g. a script setting a value without dirtying).
        const double RediffInterval = 2.0;
        const double RestyleInterval = 1.0;

        // Symmetric: both windows of a pair map to each other.
        static readonly Dictionary<EditorWindow, EditorWindow> Pairs = new Dictionary<EditorWindow, EditorWindow>();
        // Which window of each pair drives the scrolling, keyed by both windows of the pair.
        static readonly Dictionary<EditorWindow, EditorWindow> Leaders = new Dictionary<EditorWindow, EditorWindow>();
        // Each window's scroll view and offset as of the last frame. Thry swaps the view out on every rebuild.
        static readonly Dictionary<EditorWindow, (ScrollView view, float offset)> ScrollState = new Dictionary<EditorWindow, (ScrollView, float)>();
        // Per pair (keyed by both windows): the materials' change signature the diff was taken at, and its result.
        static readonly Dictionary<EditorWindow, DiffState> Diffs = new Dictionary<EditorWindow, DiffState>();
        // Per window: elements we've styled, and what they were styled against.
        static readonly Dictionary<EditorWindow, StyleState> Styles = new Dictionary<EditorWindow, StyleState>();

        class DiffState
        {
            public long Signature;
            public double Time;
            public int Version;
            public Dictionary<string, RowDiff> Properties = new Dictionary<string, RowDiff>();
            public Dictionary<string, SectionDiff> Sections = new Dictionary<string, SectionDiff>();
        }

        internal struct RowDiff
        {
            public Kind Kind;     // Value or Texture when the value differs, None when only the animated state does
            public bool Animated; // Thry's Animated / Renamed marking differs
        }

        internal struct SectionDiff
        {
            public bool Toggled;  // its header toggle(s) differ
            public bool Animated; // its header toggle(s) are marked Animated differently
            public int Count;    // differences inside it (rows, and nested sections switched differently)
        }

        class StyleState
        {
            public readonly Dictionary<VisualElement, (Kind kind, bool animated, int count)> Styled = new Dictionary<VisualElement, (Kind, bool, int)>();
            public VisualElement Content;
            public float Height;
            public int Version = -1;
            public double Time;
        }

        public static bool IsHighlighting(EditorWindow window) => Pairs.ContainsKey(window);

        internal static int PairCount => Pairs.Count / 2;

        public static void Start(EditorWindow a, EditorWindow b)
        {
            Stop(a); Stop(b);
            Pairs[a] = b; Pairs[b] = a;
            Diffs[a] = Diffs[b] = new DiffState { Signature = -1 };
            CrossEditorAlign.Start(a);
            // Not from inside the click: re-docking rebuilds the window the button lives in.
            EditorApplication.delayCall += () => { if (a != null && b != null) CrossEditorDock.DockRight(a, b); };
        }

        public static void Stop(EditorWindow window)
        {
            if (!Pairs.TryGetValue(window, out var other)) return;
            Forget(window); Forget(other);
        }

        static void Forget(EditorWindow window)
        {
            Pairs.Remove(window);
            Leaders.Remove(window);
            ScrollState.Remove(window);
            Diffs.Remove(window);
            if (Styles.TryGetValue(window, out var style))
            {
                foreach (var e in style.Styled.Keys) SetStyle(e, Kind.None, false, 0);
                Styles.Remove(window);
            }
            CrossEditorAlign.Stop(window);
        }

        // Every editor frame.
        static string _lastError;

        public static void Tick()
        {
            try { TickPairs(); }
            catch (System.Exception e)
            {
                if (_lastError != e.Message) Debug.LogException(e);
                _lastError = e.Message;
                CrossEditorAlign.Note("tick failed: " + e);
            }
        }

        static void TickPairs()
        {
            if (Pairs.Count == 0) return;
            foreach (var window in Pairs.Keys.Where(w => w == null || Pairs[w] == null).ToList())
            {
                var other = Pairs[window];
                Forget(window);
                if (other != null) Forget(other);
            }

            double now = EditorApplication.timeSinceStartup;
            var over = EditorWindow.mouseOverWindow;
            var done = new HashSet<EditorWindow>();
            foreach (var pair in Pairs.ToList())
            {
                EditorWindow a = pair.Key, b = pair.Value;
                if (!done.Add(a) || !done.Add(b)) continue;
                var viewA = a.rootVisualElement.Q<ScrollView>();
                var viewB = b.rootVisualElement.Q<ScrollView>();
                if (viewA == null || viewB == null) { CrossEditorAlign.Note($"no scroll view: A={viewA != null} B={viewB != null}"); continue; }

                var diff = Diffs[a];
                var materialsA = PoiCrossEditorButtons.GetTargets(a);
                var materialsB = PoiCrossEditorButtons.GetTargets(b);
                long signature = Signature(materialsA, materialsB);
                if (signature != diff.Signature || now - diff.Time > RediffInterval)
                {
                    var (properties, sections) = Diff(materialsA, materialsB);
                    if (!SameDict(properties, diff.Properties) || !SameDict(sections, diff.Sections)) diff.Version++;
                    diff.Properties = properties; diff.Sections = sections;
                    diff.Signature = signature; diff.Time = now;
                }

                CrossEditorAlign.Update(a, b, viewA, viewB);
                Restyle(a, viewA, diff, now);
                Restyle(b, viewB, diff, now);
                SyncScroll(a, b, viewA, viewB, over);
            }
        }

        static bool SameDict<T>(Dictionary<string, T> x, Dictionary<string, T> y) =>
            x.Count == y.Count && x.All(kv => y.TryGetValue(kv.Key, out var v) && EqualityComparer<T>.Default.Equals(v, kv.Value));

        // Changes when a material is edited (its dirty count goes up), swapped, or a list changes.
        static long Signature(List<Material> a, List<Material> b)
        {
            unchecked
            {
                long hash = 17;
                foreach (var m in a.Concat(b))
                {
                    hash = hash * 31 + m.GetInstanceID();
                    hash = hash * 31 + EditorUtility.GetDirtyCount(m);
                    hash = hash * 31 + (m.shader != null ? m.shader.GetInstanceID() : 0);
                }
                return hash * 31 + a.Count;
            }
        }

        // ---- Scrolling ----

        // The padding keeps every shared row at the same height in both windows, so the partner can simply
        // take the same offset. Only the window under the mouse leads: a follower that hits the end of its
        // content gets clamped and nudged by Unity, and letting that count as a scroll dragged the leader back.
        static void SyncScroll(EditorWindow a, EditorWindow b, ScrollView viewA, ScrollView viewB, EditorWindow over)
        {
            if (over == a || over == b) Leaders[a] = Leaders[b] = over;
            if (Leaders.TryGetValue(a, out var leader))
            {
                bool aLeads = leader == a;
                var (lead, follow) = aLeads ? (viewA, viewB) : (viewB, viewA);
                if (Moved(leader, lead))
                {
                    follow.scrollOffset = new Vector2(follow.scrollOffset.x, lead.scrollOffset.y);
                    (aLeads ? b : a).Repaint();
                }
            }
            ScrollState[a] = (viewA, viewA.scrollOffset.y);
            ScrollState[b] = (viewB, viewB.scrollOffset.y);
        }

        // A fresh view (window just paired or rebuilt) doesn't count as a scroll.
        static bool Moved(EditorWindow window, ScrollView view) =>
            ScrollState.TryGetValue(window, out var last) && last.view == view && !Mathf.Approximately(last.offset, view.scrollOffset.y);

        // ---- Element identity ----

        // Property rows are named "property-<name>". Sections are all named "section-" (Thry leaves their
        // identifier empty), so they're keyed by the start property of the ShaderGroup in their userData.
        internal static string Key(VisualElement e)
        {
            if (e.ClassListContains("thry-section"))
            {
                string start = SectionProperty(e);
                return start != null ? "section-" + start : null;
            }
            return e.name != null && e.name.StartsWith("property-") ? e.name : null;
        }

        static readonly Dictionary<System.Type, System.Func<object, object>> MaterialPropertyGetters = new Dictionary<System.Type, System.Func<object, object>>();

        static string SectionProperty(VisualElement e)
        {
            var group = e.userData;
            if (group == null) return null;
            var type = group.GetType();
            if (!MaterialPropertyGetters.TryGetValue(type, out var get))
            {
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                var property = type.GetProperty("MaterialProperty", flags);
                var field = property == null ? type.GetField("MaterialProperty", flags) : null;
                get = property != null ? (System.Func<object, object>)property.GetValue
                    : field != null ? field.GetValue
                    : (System.Func<object, object>)(_ => null);
                MaterialPropertyGetters[type] = get;
            }
            return (get(group) as MaterialProperty)?.name;
        }

        internal static bool Visible(VisualElement e)
        {
            var bound = e.worldBound;
            if (float.IsNaN(bound.height) || bound.height <= 0) return false;
            // hierarchy.parent, not .parent: the logical parent skips content containers, which can be hidden.
            for (var v = e; v != null; v = v.hierarchy.parent)
                if (v.resolvedStyle.display == DisplayStyle.None) return false;
            return true;
        }

        // ---- Highlighting ----

        // Only when the diff changed, or the window's rows did (rebuilt, or its content height changed
        // because a section opened or rows appeared).
        static void Restyle(EditorWindow window, ScrollView view, DiffState diff, double now)
        {
            if (!Styles.TryGetValue(window, out var state)) Styles[window] = state = new StyleState();
            var content = view.contentContainer;
            float height = content.layout.height;
            if (state.Content == content && state.Version == diff.Version && Mathf.Approximately(state.Height, height)
                && now - state.Time < RestyleInterval) return;
            state.Content = content; state.Version = diff.Version; state.Height = height; state.Time = now;

            var wanted = new Dictionary<VisualElement, (Kind kind, bool animated, int count)>();
            content.Query(className: "thry-property").ForEach(e =>
            {
                if (e.name != null && e.name.StartsWith("property-") && diff.Properties.TryGetValue(e.name.Substring(9), out var row))
                    wanted[e] = (row.Kind, row.Animated, 0);
            });
            content.Query(className: "thry-section").ForEach(e =>
            {
                string start = SectionProperty(e);
                if (start == null || !diff.Sections.TryGetValue(start, out var section)) return;
                // Only its own header, not those of the sections nested inside it.
                var header = e.hierarchy.Children().FirstOrDefault(c => c.ClassListContains("thry-section-header"));
                if (header != null) wanted[header] = (section.Toggled ? Kind.Toggle : Kind.Contains, section.Animated, section.Count);
            });

            foreach (var e in state.Styled.Keys.Where(e => !wanted.ContainsKey(e)).ToList()) { SetStyle(e, Kind.None, false, 0); state.Styled.Remove(e); }
            foreach (var kv in wanted)
            {
                if (state.Styled.TryGetValue(kv.Key, out var was) && was == kv.Value) continue;
                SetStyle(kv.Key, kv.Value.kind, kv.Value.animated, kv.Value.count);
                state.Styled[kv.Key] = kv.Value;
            }
        }

        // The left bar and tint say what differs; a pink bar on the right says the Animated marking
        // differs too. A row whose only difference is the Animated marking is tinted pink instead.
        static void SetStyle(VisualElement e, Kind kind, bool animated, int count)
        {
            bool onlyAnimated = kind == Kind.None && animated;
            var color = onlyAnimated ? AnimatedColor : kind == Kind.Texture ? TextureColor : kind == Kind.Toggle ? ToggleColor : ValueColor;
            if (kind == Kind.None && !onlyAnimated)
            {
                e.style.backgroundColor = StyleKeyword.Null;
                e.style.borderLeftColor = StyleKeyword.Null;
                e.style.borderLeftWidth = StyleKeyword.Null;
            }
            else if (kind == Kind.Contains)
            {
                e.style.backgroundColor = StyleKeyword.Null;
                e.style.borderLeftColor = ContainsBar;
                e.style.borderLeftWidth = ContainsBarWidth;
            }
            else
            {
                e.style.backgroundColor = new Color(color.r, color.g, color.b, TintAlpha);
                e.style.borderLeftColor = color;
                e.style.borderLeftWidth = BarWidth;
            }
            if (animated && !onlyAnimated)
            {
                e.style.borderRightColor = AnimatedColor;
                e.style.borderRightWidth = BarWidth;
            }
            else
            {
                e.style.borderRightColor = StyleKeyword.Null;
                e.style.borderRightWidth = StyleKeyword.Null;
            }
            SetCount(e, kind == Kind.None && !animated ? 0 : count);
        }

        // "3 different" after the section title. Headers only; rows never get a count.
        static void SetCount(VisualElement header, int count)
        {
            var titleArea = header.Q(className: "thry-section-title-area");
            if (titleArea == null) return;
            var label = titleArea.Q<Label>(CountName);
            if (count <= 0) { label?.RemoveFromHierarchy(); return; }
            if (label == null)
            {
                label = new Label { name = CountName, pickingMode = PickingMode.Ignore };
                label.style.marginLeft = 6;
                label.style.fontSize = 10;
                label.style.opacity = 0.65f;
                label.style.unityFontStyleAndWeight = FontStyle.Italic;
                titleArea.Add(label);
            }
            label.text = count == 1 ? "1 different" : count + " different";
        }

        // ---- Diff ----

        // What the diff needs from a shader, worked out once per shader rather than on every diff.
        class ShaderInfo
        {
            public int Count;
            public string[] Names;
            // "<name>Animated": the material tag Thry stores a property's Animated marking in
            // (empty or "0" not animated, "1" animated, "2" animated and renamed).
            public string[] AnimatedTags;
            public int[] Ids;
            public ShaderPropertyType[] Types;
            // Indices of real properties (not Thry layout markers).
            public int[] Properties;
            // Property name -> the sections (by start property name) it sits in, outermost first.
            public readonly Dictionary<string, string[]> SectionsOf = new Dictionary<string, string[]>();
            // Section -> the properties drawn in its header (its on/off toggles), from reference_property(ies).
            public readonly Dictionary<string, string[]> HeaderProperties = new Dictionary<string, string[]>();
            // Properties hidden from the inspector. They're compared only as header toggles.
            public readonly HashSet<string> Hidden = new HashSet<string>();
        }

        static readonly Dictionary<Shader, ShaderInfo> ShaderInfos = new Dictionary<Shader, ShaderInfo>();

        static ShaderInfo Info(Shader shader)
        {
            int count = shader.GetPropertyCount();
            if (ShaderInfos.TryGetValue(shader, out var info) && info.Count == count) return info;
            info = new ShaderInfo { Count = count, Names = new string[count], AnimatedTags = new string[count], Ids = new int[count], Types = new ShaderPropertyType[count] };
            var properties = new List<int>();
            var open = new List<string>();
            for (int i = 0; i < count; i++)
            {
                string name = shader.GetPropertyName(i);
                info.Names[i] = name;
                info.AnimatedTags[i] = name + "Animated";
                info.Ids[i] = shader.GetPropertyNameId(i);
                info.Types[i] = shader.GetPropertyType(i);
                // Thry's layout markers. A bare m_ header closes the previous header and opens itself.
                bool header = name.StartsWith("m_") && !name.StartsWith("m_start") && !name.StartsWith("m_end");
                if (header || name.StartsWith("m_start") || name.StartsWith("g_start") || name.StartsWith("s_start") || name.StartsWith("ss_start"))
                {
                    if (header && open.Count > 0) open.RemoveAt(open.Count - 1);
                    info.SectionsOf[name] = open.ToArray();
                    info.HeaderProperties[name] = HeaderReferences(shader.GetPropertyDescription(i));
                    open.Add(name);
                    continue;
                }
                if (name.StartsWith("m_end") || name.StartsWith("g_end") || name.StartsWith("s_end") || name.StartsWith("ss_end")) { if (open.Count > 0) open.RemoveAt(open.Count - 1); continue; }
                if ((shader.GetPropertyFlags(i) & ShaderPropertyFlags.HideInInspector) != 0) info.Hidden.Add(name);
                if (!info.SectionsOf.ContainsKey(name)) info.SectionsOf[name] = open.ToArray();
                properties.Add(i);
            }
            info.Properties = properties.ToArray();
            ShaderInfos[shader] = info;
            return info;
        }

        static readonly Regex ReferenceProperties = new Regex(@"reference_propert(?:y|ies)\s*:\s*(\[[^\]]*\]|[^,}]+)");

        // Thry descriptions carry options after "--", e.g. "Matcaps--{reference_properties:[_MatcapEnable,_Matcap2Enable]}".
        static string[] HeaderReferences(string description)
        {
            int options = description.IndexOf("--", System.StringComparison.Ordinal);
            if (options < 0) return System.Array.Empty<string>();
            var refs = new List<string>();
            foreach (Match m in ReferenceProperties.Matches(description.Substring(options)))
                refs.AddRange(m.Groups[1].Value.Trim('[', ']', ' ').Split(',').Select(r => r.Trim()).Where(r => r.Length > 0));
            return refs.ToArray();
        }

        // Properties both sides have whose value or Animated marking differs (a side whose materials
        // disagree counts as its set of values); and per section (by start property name) whether its
        // header toggle differs, in value or in Animated marking, and how many differences sit inside it.
        static (Dictionary<string, RowDiff> properties, Dictionary<string, SectionDiff> sections) Diff(List<Material> a, List<Material> b)
        {
            var infos = new List<ShaderInfo>();
            var valuesA = Collect(a, infos, out var animatedA);
            var valuesB = Collect(b, infos, out var animatedB);

            var differing = new Dictionary<string, ShaderPropertyType>();
            foreach (var kv in valuesA)
                if (valuesB.TryGetValue(kv.Key, out var other) && !SameSet(kv.Value, other))
                    differing[kv.Key.name] = kv.Key.type;
            var animatedDiffering = new HashSet<string>();
            foreach (var kv in animatedA)
                if (animatedB.TryGetValue(kv.Key, out var other) && !kv.Value.SetEquals(other))
                    animatedDiffering.Add(kv.Key);

            var properties = new Dictionary<string, RowDiff>();
            var sections = new Dictionary<string, SectionDiff>();
            void CountIn(string name)
            {
                var containing = new HashSet<string>();
                AddContaining(infos, name, containing);
                foreach (var section in containing)
                {
                    sections.TryGetValue(section, out var d);
                    d.Count++;
                    sections[section] = d;
                }
            }
            foreach (var name in differing.Keys.Concat(animatedDiffering).Distinct())
            {
                if (infos.Any(i => i.Hidden.Contains(name))) continue;
                var kind = !differing.TryGetValue(name, out var type) ? Kind.None : type == ShaderPropertyType.Texture ? Kind.Texture : Kind.Value;
                properties[name] = new RowDiff { Kind = kind, Animated = animatedDiffering.Contains(name) };
                CountIn(name);
            }
            // Sections whose header toggle differs (switched on in one window and off in the other, or
            // marked Animated in only one); each counts once in the sections around it.
            foreach (var info in infos)
                foreach (var kv in info.HeaderProperties)
                {
                    bool toggled = kv.Value.Any(differing.ContainsKey), animated = kv.Value.Any(animatedDiffering.Contains);
                    if (!toggled && !animated) continue;
                    sections.TryGetValue(kv.Key, out var d);
                    bool counted = d.Toggled || d.Animated;
                    d.Toggled |= toggled;
                    d.Animated |= animated;
                    sections[kv.Key] = d;
                    if (!counted) CountIn(kv.Key);
                }
            return (properties, sections);
        }

        static void AddContaining(List<ShaderInfo> infos, string name, HashSet<string> sections)
        {
            foreach (var info in infos)
                if (info.SectionsOf.TryGetValue(name, out var containing)) sections.UnionWith(containing);
        }

        static bool SameSet(List<Value> x, List<Value> y) =>
            x.Count == y.Count && x.All(v => y.Any(v.SameAs));

        // Per (name, type): the distinct values across the materials. Per name: the distinct Animated markings.
        static Dictionary<(string name, ShaderPropertyType type), List<Value>> Collect(List<Material> materials, List<ShaderInfo> infos,
            out Dictionary<string, HashSet<string>> animated)
        {
            var values = new Dictionary<(string, ShaderPropertyType), List<Value>>();
            animated = new Dictionary<string, HashSet<string>>();
            foreach (var material in materials)
            {
                if (material.shader == null) continue;
                var info = Info(material.shader);
                if (!infos.Contains(info)) infos.Add(info);
                foreach (int i in info.Properties)
                {
                    var key = (info.Names[i], info.Types[i]);
                    if (!values.TryGetValue(key, out var list)) values[key] = list = new List<Value>(1);
                    var value = Value.Read(material, info.Ids[i], info.Types[i]);
                    bool seen = false;
                    for (int j = 0; j < list.Count && !seen; j++) seen = list[j].SameAs(value);
                    if (!seen) list.Add(value);

                    string tag = material.GetTag(info.AnimatedTags[i], false, "");
                    if (!animated.TryGetValue(info.Names[i], out var tags)) animated[info.Names[i]] = tags = new HashSet<string>();
                    tags.Add(tag.Length == 0 ? "0" : tag);
                }
            }
            return values;
        }

        struct Value
        {
            Vector4 _vector;
            Texture _texture;

            public static Value Read(Material m, int id, ShaderPropertyType type)
            {
                var v = new Value();
                switch (type)
                {
                    case ShaderPropertyType.Color: v._vector = m.GetColor(id); break;
                    case ShaderPropertyType.Vector: v._vector = m.GetVector(id); break;
                    case ShaderPropertyType.Int: v._vector.x = m.GetInteger(id); break;
                    case ShaderPropertyType.Texture:
                        v._texture = m.GetTexture(id);
                        var s = m.GetTextureScale(id); var o = m.GetTextureOffset(id);
                        v._vector = new Vector4(s.x, s.y, o.x, o.y);
                        break;
                    default: v._vector.x = m.GetFloat(id); break;
                }
                return v;
            }

            public bool SameAs(Value other) => _texture == other._texture && (_vector - other._vector).sqrMagnitude < 1e-10f;
        }
    }
}
