using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace DescendersCyclingPOC
{
    /// <summary>
    /// Flashes a short message in the middle of the screen with the game's own in-ride text (used for .zwo text events).
    /// The line comes from the game's stunt log (UI_RepPopup.AddLineToStuntLog, the list of landed tricks, bottom left): it is
    /// taken out of the log before its StuntLogLine.Start() runs and moved into a centred box, so its fade/slide-in happens there
    /// and it destroys itself after 7.5 s as usual. The game refuses rep &lt; 1 and appends the rep number, so the line is added
    /// with rep 1 and its text is then overwritten. A new message replaces the previous one. Tried and dropped (look, not function):
    /// PermaGUI.ShowMultiplayerMessage (the online-chat style banner) and leaving the line in the stunt log.
    /// Member names used are unobfuscated; the log's line list is found by type (List&lt;GameObject&gt;).
    /// </summary>
    internal static class GameText
    {
        private const float Height = 0.7f;   // box centre, fraction of screen height (above the rider)
        private const float Scale = 1.5f;    // font size relative to a stunt-log line
        private const float PanelGap = 24f;  // screen px kept clear before the workout panel (text stays centred, so both sides shrink)

        private static bool _resolved, _componentsLogged;
        private static MethodInfo _addStunt;
        private static FieldInfo _stuntLines;
        private static Type _tmpType;
        private static PropertyInfo _tmpText;
        private static UI_RepPopup _rep;
        private static GameObject _box, _line;
        private static Canvas _canvas;
        private static string _sizeLog = "";

        public static void Show(string msg)
        {
            Log.Info("Text event: " + msg);
            if (string.IsNullOrEmpty(msg)) return;
            try
            {
                Resolve();
                if (!Centre(msg)) Log.WarnLimited("gametext", "GameText: no game UI to show the message on (see the resolve line above)", 60f);
            }
            catch (Exception ex) { Log.ErrorLimited("gametext", "GameText.Show", ex, 30f); }
        }

        private static bool Centre(string msg)
        {
            if (object.ReferenceEquals(_addStunt, null) || object.ReferenceEquals(_stuntLines, null)) return false;
            if (_rep == null) _rep = UnityEngine.Object.FindObjectOfType(typeof(UI_RepPopup)) as UI_RepPopup;
            if (_rep == null) { Log.WarnLimited("gametext-rep", "GameText: no UI_RepPopup in the scene", 60f); return false; }
            _addStunt.Invoke(_rep, new object[] { msg, 1 });
            var lines = _stuntLines.GetValue(_rep) as List<GameObject>;
            if (lines == null || lines.Count == 0 || lines[lines.Count - 1] == null) return false;
            GameObject line = lines[lines.Count - 1];
            lines.RemoveAt(lines.Count - 1);                     // the log no longer moves it up or counts it
            if (_line != null) UnityEngine.Object.Destroy(_line);
            _line = line;
            if (_box == null)
            {
                Canvas c = _rep.GetComponentInParent<Canvas>();
                _canvas = c != null ? c.rootCanvas : null;
                Transform root = _canvas != null ? _canvas.transform : _rep.transform;
                _box = new GameObject("CyclingPOC_CentreText", typeof(RectTransform));
                var box = (RectTransform)_box.transform;
                box.SetParent(root, false);
                box.anchorMin = box.anchorMax = new Vector2(0.5f, Height);
                box.pivot = new Vector2(0.5f, 0.5f);
                box.anchoredPosition = Vector2.zero; box.sizeDelta = Vector2.zero;
                Log.Info("GameText: centre box under '" + Path(root) + "'");
            }
            // the stunt line is made for one short line: switch off anything that sizes it to its text, fill the box, wrap
            foreach (Component comp in line.GetComponents<Component>())
            {
                if (comp == null) continue;
                string n = comp.GetType().Name;
                if (!_componentsLogged) Log.Info("GameText: stunt line component " + comp.GetType().FullName);
                if ((n == "ContentSizeFitter" || n == "LayoutElement") && comp is Behaviour) ((Behaviour)comp).enabled = false;
            }
            _componentsLogged = true;
            var rt = (RectTransform)line.transform;
            rt.SetParent(_box.transform, false);
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            // width in real screen pixels: centred, clear of the workout panel on either side (HudSide), so convert through the
            // canvas scale factor and any scale the line itself carries (the prefab's is not 1: 80 % of the canvas came out wider)
            float halfPx = Screen.width / 2f - WorkoutPanel.Width - 12f - PanelGap;
            halfPx = Mathf.Max(halfPx, Screen.width * 0.2f);
            float pxPerUnit = (_canvas != null ? _canvas.scaleFactor : 1f) * (_canvas != null ? rt.lossyScale.x / _canvas.transform.lossyScale.x : 1f);
            if (!(pxPerUnit > 0.01f)) pxPerUnit = 1f;
            rt.sizeDelta = new Vector2(2f * halfPx / pxPerUnit, 200f);
            string sz = Screen.width + " px wide, canvas scale " + (_canvas != null ? _canvas.scaleFactor.ToString("F3") : "?") + ", line scale " + rt.localScale.x.ToString("F3") + " -> text " + (2f * halfPx).ToString("F0") + " px";
            if (sz != _sizeLog) { _sizeLog = sz; Log.Info("GameText: " + sz); }
            Component tmp = object.ReferenceEquals(_tmpType, null) ? null : line.GetComponent(_tmpType);
            if (tmp != null)
            {
                _tmpText.SetValue(tmp, msg, null);               // drop the " 1" the game appended
                SetProp(tmp, "alignment", Enum.Parse(PropType(tmp, "alignment"), "Center"));
                SetProp(tmp, "enableWordWrapping", true);
                SetProp(tmp, "overflowMode", Enum.Parse(PropType(tmp, "overflowMode"), "Overflow"));   // extra lines grow up and down
                PropertyInfo fs = _tmpType.GetProperty("fontSize", BindingFlags.Instance | BindingFlags.Public);
                if (!object.ReferenceEquals(fs, null)) fs.SetValue(tmp, (float)fs.GetValue(tmp, null) * Scale, null);
            }
            return true;
        }

        private static Type PropType(Component c, string name)
        {
            PropertyInfo p = c.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            if (object.ReferenceEquals(p, null)) throw new MissingMemberException(c.GetType().Name, name);
            return p.PropertyType;
        }

        private static void SetProp(Component c, string name, object value)
        {
            PropertyInfo p = c.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            if (object.ReferenceEquals(p, null)) { Log.WarnLimited("gametext-prop-" + name, "GameText: TMP has no '" + name + "'", 600f); return; }
            p.SetValue(c, value, null);
        }

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;
            Type rep = typeof(UI_RepPopup);
            _addStunt = rep.GetMethod("AddLineToStuntLog", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new Type[] { typeof(string), typeof(int) }, null);
            foreach (FieldInfo f in rep.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (object.ReferenceEquals(f.FieldType, typeof(List<GameObject>))) _stuntLines = f;
                else if (f.FieldType.Name == "TextMeshProUGUI") _tmpType = f.FieldType;
            }
            if (!object.ReferenceEquals(_tmpType, null)) _tmpText = _tmpType.GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
            if (object.ReferenceEquals(_tmpText, null)) _tmpType = null;   // Centre() needs both
            Log.Info("GameText: UI_RepPopup.AddLineToStuntLog " + Ok(_addStunt) + ", stunt line list " + Ok(_stuntLines) + ", TMP text " + Ok(_tmpText));
        }

        private static string Path(Transform t)
        {
            string p = t.name;
            for (t = t.parent; t != null; t = t.parent) p = t.name + "/" + p;
            return p;
        }

        private static string Ok(object m) { return object.ReferenceEquals(m, null) ? "MISSING" : "ok"; }
    }
}
