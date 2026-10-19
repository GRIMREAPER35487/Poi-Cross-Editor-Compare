using System;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Synthos.PoiCrossEditorCompare
{
    /// <summary>
    /// Docks one editor window to the right of another, the way dragging its tab onto the other's
    /// right edge would. Unity has no public API for this, so it drives the internal SplitView drop.
    /// </summary>
    static class CrossEditorDock
    {
        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        static readonly Type DockAreaType = typeof(EditorWindow).Assembly.GetType("UnityEditor.DockArea");

        public static void DockRight(EditorWindow target, EditorWindow window)
        {
            try
            {
                var targetHost = Parent(target);
                var windowHost = Parent(window);
                if (targetHost == null || windowHost == null) return;
                // Already split within the same container window: they're side by side already.
                if (targetHost != windowHost && Container(targetHost) == Container(windowHost)) return;

                var container = Container(targetHost);
                var splitView = container.GetType().GetProperty("rootSplitView", Any).GetValue(container);
                var dragOver = splitView.GetType().GetMethods(Any).First(m => m.Name == "DragOver" && m.GetParameters().Length == 2);
                var performDrop = splitView.GetType().GetMethods(Any).First(m => m.Name == "PerformDrop" && m.GetParameters().Length == 3);
                var originalSource = DockAreaType.GetField("s_OriginalDragSource", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

                // A point just inside the target's right edge, in screen space.
                var rect = target.position;
                var point = new Vector2(rect.xMax - 20, rect.y + rect.height / 2);
                var dropInfo = dragOver.Invoke(splitView, new object[] { window, point });
                if (dropInfo == null) return;
                originalSource.SetValue(null, windowHost);
                performDrop.Invoke(splitView, new object[] { window, dropInfo, point });
                originalSource.SetValue(null, null);
                // An edge drop only gives the new window a thin strip; share the space evenly once laid out.
                EditorApplication.delayCall += () => { if (target != null && window != null) SplitEvenly(target, window); };
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Poi Cross Editor Compare] Couldn't dock the Cross Shader Editor windows together: " + e.Message);
            }
        }

        // Both windows' dock areas sit side by side in one horizontal SplitView after the drop:
        // give them half of their combined width each, then have the split view re-read its children.
        static void SplitEvenly(EditorWindow left, EditorWindow right)
        {
            try
            {
                var leftHost = Parent(left);
                var rightHost = Parent(right);
                var split = rightHost.GetType().GetProperty("parent", Any).GetValue(rightHost);
                if (split == null || split != leftHost.GetType().GetProperty("parent", Any).GetValue(leftHost)) return;
                if (split.GetType().GetField("vertical", Any)?.GetValue(split) is bool vertical && vertical) return;

                var position = leftHost.GetType().GetProperty("position", Any);
                var l = (Rect)position.GetValue(leftHost);
                var r = (Rect)position.GetValue(rightHost);
                float half = Mathf.Floor((l.width + r.width) / 2);
                float x = Mathf.Min(l.x, r.x);
                position.SetValue(leftHost, new Rect(x, l.y, half, l.height));
                position.SetValue(rightHost, new Rect(x + half, r.y, l.width + r.width - half, r.height));

                split.GetType().GetMethod("SetupSplitter", Any, null, Type.EmptyTypes, null)?.Invoke(split, null);
                split.GetType().GetMethod("SetupRectsFromSplitter", Any, null, Type.EmptyTypes, null)?.Invoke(split, null);
                left.Repaint(); right.Repaint();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Poi Cross Editor Compare] Couldn't resize the docked Cross Shader Editor windows: " + e.Message);
            }
        }

        static object Parent(EditorWindow window) => typeof(EditorWindow).GetField("m_Parent", Any).GetValue(window);

        static object Container(object host) => host.GetType().GetProperty("window", Any).GetValue(host);
    }
}
