using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;

namespace Synthos.PoiCrossEditorCompare
{
    /// <summary>
    /// Adds "grab avatar materials" buttons to Thry's Cross Shader Editor (Poiyomi).
    /// The window rebuilds its whole UI on every target change, so the buttons are re-injected
    /// whenever they go missing. Everything is found by reflection: no hard dependency on Thry.
    /// </summary>
    [InitializeOnLoad]
    static class PoiCrossEditorButtons
    {
        const string CrossEditorTypeName = "Thry.ThryEditor.CrossEditor";
        const string AvatarDescriptorTypeName = "VRC.SDK3.Avatars.Components.VRCAvatarDescriptor";
        const string RowName = "synthos-avatar-grab-row";
        const string CompareName = "synthos-compare-button";
        const double PollInterval = 0.25;

        static readonly Type CrossEditorType;
        static readonly MethodInfo UpdateTargetsMethod;
        static readonly FieldInfo TargetsField;
        static readonly Type AvatarDescriptorType;
        static double _nextPoll;

        static PoiCrossEditorButtons()
        {
            CrossEditorType = TypeCache.GetTypesDerivedFrom<EditorWindow>().FirstOrDefault(t => t.FullName == CrossEditorTypeName);
            if (CrossEditorType == null) return;
            UpdateTargetsMethod = CrossEditorType.GetMethod("UpdateTargets", BindingFlags.Public | BindingFlags.Instance,
                null, new[] { typeof(IEnumerable<Material>), typeof(bool) }, null);
            if (UpdateTargetsMethod == null) return;
            // The enabled, Thry-compatible materials the window is editing. Optional: only the Compare button needs it.
            TargetsField = CrossEditorType.GetField("_targets", BindingFlags.NonPublic | BindingFlags.Instance);
            AvatarDescriptorType = TypeCache.GetTypesDerivedFrom<Component>().FirstOrDefault(t => t.FullName == AvatarDescriptorTypeName);
            EditorApplication.update += Poll;
            EditorApplication.update += CrossEditorHighlight.Tick;
            // After the menus are built, which is after [InitializeOnLoad] runs.
            EditorApplication.delayCall += ReplaceThryMenuItem;
        }

        const string ThryMenuPath = "Poi/Thry/Cross Shader Editor";
        const string FallbackMenuPath = "Poi/Thry/New Cross Shader Editor";
        const int ThryMenuPriority = 2;

        // Thry's own entry goes through GetWindow, which always reuses the open window. Swap it for one
        // that opens a fresh window every time. Unity only exposes this through its internal Menu API;
        // if that isn't there, add ours beside Thry's instead.
        static void ReplaceThryMenuItem()
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            var remove = typeof(Menu).GetMethod("RemoveMenuItem", flags, null, new[] { typeof(string) }, null);
            var add = typeof(Menu).GetMethods(flags).FirstOrDefault(m => m.Name == "AddMenuItem" && m.GetParameters().Length == 6);
            Action execute = OpenNewWindow;
            Func<bool> validate = () => CrossEditorType != null;
            try
            {
                if (remove == null || add == null) throw new MissingMethodException("UnityEditor.Menu", "RemoveMenuItem/AddMenuItem");
                remove.Invoke(null, new object[] { ThryMenuPath });
                add.Invoke(null, new object[] { ThryMenuPath, "", false, ThryMenuPriority, execute, validate });
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Poi Cross Editor Compare] Couldn't replace Thry's Cross Shader Editor menu item ({e.Message}); adding \"{FallbackMenuPath}\" instead.");
                add?.Invoke(null, new object[] { FallbackMenuPath, "", false, ThryMenuPriority, execute, validate });
            }
        }

        static void OpenNewWindow()
        {
            var window = (EditorWindow)ScriptableObject.CreateInstance(CrossEditorType);
            window.Show();
            window.Focus();
        }

        static void Poll()
        {
            if (EditorApplication.timeSinceStartup < _nextPoll) return;
            _nextPoll = EditorApplication.timeSinceStartup + PollInterval;
            var windows = Resources.FindObjectsOfTypeAll(CrossEditorType).Cast<EditorWindow>().ToArray();
            bool canCompare = windows.Length >= 2 && TargetsField != null;
            foreach (var window in windows)
            {
                Inject(window);
                if (window.rootVisualElement.Q(CompareName) is Button compare)
                {
                    bool active = CrossEditorHighlight.IsHighlighting(window);
                    compare.text = active ? "Stop highlighting" : "Highlight differences";
                    compare.style.display = canCompare || active ? DisplayStyle.Flex : DisplayStyle.None;
                }
            }
        }

        internal static List<Material> GetTargets(EditorWindow window) =>
            (TargetsField?.GetValue(window) as IEnumerable<Material>)?.Where(m => m != null).ToList() ?? new List<Material>();

        // Pairs straight up with two windows open, otherwise pick which one to compare against.
        static void Compare(EditorWindow window)
        {
            if (CrossEditorHighlight.IsHighlighting(window)) { CrossEditorHighlight.Stop(window); return; }
            var others = Resources.FindObjectsOfTypeAll(CrossEditorType).Cast<EditorWindow>().Where(w => w != window).ToArray();
            if (others.Length == 1) { CrossEditorHighlight.Start(window, others[0]); return; }
            var menu = new GenericMenu();
            for (int i = 0; i < others.Length; i++)
            {
                var other = others[i];
                var materials = GetTargets(other);
                string label = materials.Count == 0 ? "(no materials)" : materials.Count == 1 ? materials[0].name : $"{materials[0].name} +{materials.Count - 1}";
                menu.AddItem(new GUIContent($"{i + 1}. {label}"), false, () => CrossEditorHighlight.Start(window, other));
            }
            menu.ShowAsContext();
        }

        static void Inject(EditorWindow window)
        {
            var root = window.rootVisualElement;
            if (root.Q(RowName) != null) return;
            // Thry's action row holds "Add material", "Add selected", "Clear" and "Groups".
            var addSelected = root.Query<Button>().Where(b => b.text == "Add selected").First();
            var actions = addSelected?.parent;
            if (actions?.parent == null) return;

            // Thry's row and ours stack in a column, with the drop zone to the right of both.
            var container = new VisualElement { name = RowName };
            container.style.flexDirection = FlexDirection.Row;
            var column = new VisualElement();
            var row = new VisualElement();
            row.AddToClassList("thry-components");
            row.Add(new Button(() => Grab(window, false))
            {
                text = "Grab avatar (enabled)",
                tooltip = "Replace the list with the materials on the avatar's active, enabled renderers. Skips EditorOnly objects.",
            });
            row.Add(new Button(() => Grab(window, true))
            {
                text = "Grab avatar (all)",
                tooltip = "Replace the list with the materials on every renderer on the avatar, including disabled and EditorOnly objects.",
            });
            row.Add(new Button(() => Compare(window))
            {
                name = CompareName,
                text = "Highlight differences",
                tooltip = "Dock this window next to another Cross Shader Editor window, scroll them together, and highlight every property whose value differs between their enabled materials.",
                style = { display = DisplayStyle.None },
            });
            var parent = actions.parent;
            int index = parent.IndexOf(actions);
            column.Add(actions);
            column.Add(row);
            container.Add(column);
            container.Add(CreateDropZone(window));
            parent.Insert(index, container);
        }

        static VisualElement CreateDropZone(EditorWindow window)
        {
            var zone = new VisualElement
            {
                tooltip = "Drop scene objects, prefabs or models here to add the materials of every renderer under them, including disabled ones.",
            };
            var s = zone.style;
            s.flexGrow = 1;
            s.minHeight = 36;
            s.marginLeft = 6; s.marginRight = 4; s.marginTop = 2; s.marginBottom = 2;
            s.justifyContent = Justify.Center;
            s.alignItems = Align.Center;
            s.borderTopLeftRadius = s.borderTopRightRadius = s.borderBottomLeftRadius = s.borderBottomRightRadius = 4;
            s.borderTopWidth = s.borderBottomWidth = s.borderLeftWidth = s.borderRightWidth = 1;
            var idle = new Color(1f, 1f, 1f, 0.25f);
            var hover = new Color(0.35f, 0.65f, 1f, 1f);
            void SetHighlight(bool on)
            {
                var c = on ? hover : idle;
                s.borderTopColor = s.borderBottomColor = s.borderLeftColor = s.borderRightColor = c;
                s.backgroundColor = on ? new Color(0.35f, 0.65f, 1f, 0.12f) : new Color(0f, 0f, 0f, 0f);
            }
            SetHighlight(false);

            var label = new Label("Drop objects / prefabs here");
            label.style.opacity = 0.7f;
            label.pickingMode = PickingMode.Ignore;
            zone.Add(label);

            zone.RegisterCallback<DragUpdatedEvent>(e =>
            {
                if (!DroppedMaterials().Any()) return;
                DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                SetHighlight(true);
                e.StopPropagation();
            });
            zone.RegisterCallback<DragLeaveEvent>(e => SetHighlight(false));
            zone.RegisterCallback<DragExitedEvent>(e => SetHighlight(false));
            zone.RegisterCallback<DragPerformEvent>(e =>
            {
                var materials = DroppedMaterials().ToList();
                SetHighlight(false);
                if (materials.Count == 0) return;
                DragAndDrop.AcceptDrag();
                // Stop Thry's own window-wide drop handler adding loose materials a second time.
                e.StopPropagation();
                UpdateTargetsMethod.Invoke(window, new object[] { materials, true });
            });
            return zone;
        }

        // Materials of every renderer under each dragged object (scene object, prefab or model),
        // plus any materials dragged in directly.
        static IEnumerable<Material> DroppedMaterials()
        {
            return DragAndDrop.objectReferences.SelectMany(o =>
                {
                    switch (o)
                    {
                        case Material m: return new[] { m };
                        case GameObject g: return g.GetComponentsInChildren<Renderer>(true).SelectMany(r => r.sharedMaterials);
                        case Component c: return c.GetComponentsInChildren<Renderer>(true).SelectMany(r => r.sharedMaterials);
                        default: return Enumerable.Empty<Material>();
                    }
                })
                .Where(m => m != null)
                .Distinct();
        }

        static void Grab(EditorWindow window, bool includeInactive)
        {
            WithAvatar(avatar =>
            {
                var materials = avatar.GetComponentsInChildren<Renderer>(true)
                    .Where(r => includeInactive || IsShipped(r, avatar.transform))
                    .SelectMany(r => r.sharedMaterials)
                    .Where(m => m != null)
                    .Distinct()
                    .ToList();
                if (materials.Count == 0)
                {
                    EditorUtility.DisplayDialog("Cross Shader Editor", $"No materials found on {avatar.name}.", "OK");
                    return;
                }
                UpdateTargetsMethod.Invoke(window, new object[] { materials, false });
            });
        }

        // Active in the hierarchy, renderer enabled, and nothing up to the avatar root tagged EditorOnly.
        static bool IsShipped(Renderer renderer, Transform avatarRoot)
        {
            if (!renderer.enabled || !renderer.gameObject.activeInHierarchy) return false;
            for (var t = renderer.transform; t != null; t = t == avatarRoot ? null : t.parent)
                if (t.CompareTag("EditorOnly")) return false;
            return true;
        }

        // Picks the avatar: the one the selection is in, else the only one open, else asks.
        static void WithAvatar(Action<GameObject> action)
        {
            var avatars = FindAvatars();
            var selected = Selection.activeGameObject;
            if (selected != null)
            {
                var containing = avatars.FirstOrDefault(a => selected.transform.IsChildOf(a.transform));
                if (containing != null) { action(containing); return; }
            }

            if (avatars.Count == 1) { action(avatars[0]); return; }
            if (avatars.Count == 0)
            {
                // Without the VRChat SDK, fall back to whatever root is selected.
                if (AvatarDescriptorType == null && selected != null) { action(selected.transform.root.gameObject); return; }
                EditorUtility.DisplayDialog("Cross Shader Editor", "No avatar found in the open scenes.", "OK");
                return;
            }

            var menu = new GenericMenu();
            foreach (var avatar in avatars)
            {
                var a = avatar;
                menu.AddItem(new GUIContent(a.name + (a.activeInHierarchy ? "" : " (inactive)")), false, () => action(a));
            }
            menu.ShowAsContext();
        }

        static List<GameObject> FindAvatars()
        {
            if (AvatarDescriptorType == null) return new List<GameObject>();
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            IEnumerable<Component> found = stage != null
                ? stage.prefabContentsRoot.GetComponentsInChildren(AvatarDescriptorType, true)
                : UnityEngine.Object.FindObjectsOfType(AvatarDescriptorType, true).Cast<Component>();
            return found.Select(c => c.gameObject).OrderBy(g => g.name).ToList();
        }
    }
}
