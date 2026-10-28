using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using VRC.SDK3.Avatars.ScriptableObjects;
using Menu = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu;
using Control = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionsMenu.Control;

namespace Noname.AvatarTools.Expressions
{
    internal sealed class ExpressionSettings
    {
        internal bool Frozen, Dead, Empty, LocalParameters;
        internal bool Enabled => Frozen || Dead || Empty || LocalParameters;
        internal readonly HashSet<(string Name, Control.ControlType Type)> Protected = new();
        internal string InvalidProtection;
        internal static (string, Control.ControlType) Key(Control control) => (control.name ?? "", control.type);

        internal static ExpressionSettings Capture(ALTOTraceAndOptimizeExpressions component)
        {
            var result = new ExpressionSettings
            {
                Frozen = component.RemoveFrozenControls, Dead = component.RemoveDeadControls,
                Empty = component.RemoveEmptySubMenus, LocalParameters = component.PruneUnusedLocalParameters
            };
            void Include(Menu menu, bool hasProtectedParent = false)
            {
                bool any = false;
                foreach (var item in ExpressionMenuOptimizer.Menus(menu))
                    foreach (var control in item.controls ?? new())
                        if (control != null) { result.Protected.Add(Key(control)); any = true; }
                if (menu != null && !any && !hasProtectedParent) result.InvalidProtection = "an empty preserved menu cannot be identified after menu generation";
            }
            foreach (var menu in component.PreserveMenus ?? new())
                if (menu == null) result.InvalidProtection = "a preserved menu reference is missing";
                else Include(menu);
            foreach (var selection in component.PreserveControls ?? new())
            {
                var matches = selection?.Menu != null && selection.Selected ? (selection.Menu.controls ?? new())
                    .Where(c => c != null && Key(c) == (selection.ControlName ?? "", selection.ControlType)).ToArray() : Array.Empty<Control>();
                if (matches.Length == 0) { result.InvalidProtection = "a preserved control selection no longer matches its source menu"; continue; }
                foreach (var control in matches)
                {
                    result.Protected.Add(Key(control));
                    if (selection.IncludeSubMenus && control.type == Control.ControlType.SubMenu) Include(control.subMenu, true);
                }
            }
            return result;
        }
    }

    [Serializable]
    public sealed class ExpressionControlReport
    {
        public string Path, Name, Reason;
        public string[] Parameters;
        public bool Removed;
    }

    [Serializable]
    public sealed class ExpressionOptimizationReport
    {
        public bool Applied;
        public string SkippedReason;
        public int InputMenus, OutputMenus, InputControls, OutputControls, InputParameters, OutputParameters, SyncedParameters;
        public long Milliseconds;
        public List<string> RemovedLocalParameters = new();
        public List<ExpressionControlReport> Controls = new();
    }

    internal static class ExpressionMenuOptimizer
    {
        internal static IEnumerable<Menu> Menus(Menu root)
        {
            var seen = new HashSet<Menu>(); var pending = new Queue<Menu>();
            if (root != null) pending.Enqueue(root);
            while (pending.Count > 0)
            {
                var menu = pending.Dequeue();
                if (menu == null || !seen.Add(menu)) continue;
                yield return menu;
                foreach (var control in menu.controls ?? new())
                    if (control != null && control.type == Control.ControlType.SubMenu && control.subMenu != null) pending.Enqueue(control.subMenu);
            }
        }

        internal static IEnumerable<string> Parameters(Control control)
        {
            if (control == null) yield break;
            if (!string.IsNullOrEmpty(control.parameter?.name)) yield return control.parameter.name;
            foreach (var parameter in control.subParameters ?? Array.Empty<Control.Parameter>())
                if (!string.IsNullOrEmpty(parameter?.name)) yield return parameter.name;
        }

        internal static Menu Optimize(Menu root, ExpressionSettings settings, ISet<string> live, ISet<string> frozen,
            ExpressionOptimizationReport report, Action<UnityEngine.Object> save, Action<Menu, Menu> replacement = null)
        {
            var menus = Menus(root).ToArray();
            report.InputMenus = menus.Length; report.InputControls = menus.Sum(m => m.controls?.Count ?? 0);
            bool Protected(Control c) => c != null && settings.Protected.Contains(ExpressionSettings.Key(c));
            bool RemovableParameter(string name) => !live.Contains(name) && (frozen.Contains(name) ? settings.Frozen : settings.Dead);
            bool Removable(Control c) => c != null && !Protected(c) && Enum.IsDefined(typeof(Control.ControlType), c.type) &&
                Parameters(c).Any() && Parameters(c).All(RemovableParameter);
            var kept = menus.ToDictionary(m => m, m => new HashSet<int>(Enumerable.Range(0, m.controls?.Count ?? 0)
                .Where(i => m.controls[i] == null || m.controls[i].type == Control.ControlType.SubMenu || !Removable(m.controls[i]))));

            if (settings.Empty)
            {
                // Least fixed point removes empty navigation cycles without recursive stack growth.
                var useful = new HashSet<Menu>(menus.Where(m => kept[m].Any(i =>
                {
                    var c = m.controls[i];
                    return c == null || c.type != Control.ControlType.SubMenu || Protected(c) || Parameters(c).Any(p => !RemovableParameter(p));
                })));
                bool changed;
                do
                {
                    changed = false;
                    foreach (var m in menus)
                        if (!useful.Contains(m) && kept[m].Any(i => m.controls[i]?.subMenu != null && useful.Contains(m.controls[i].subMenu)))
                            changed |= useful.Add(m);
                } while (changed);
                foreach (var menu in menus)
                    kept[menu].RemoveWhere(i =>
                    {
                        var c = menu.controls[i];
                        return c != null && c.type == Control.ControlType.SubMenu && !Protected(c) &&
                            Parameters(c).All(RemovableParameter) && (c.subMenu == null || !useful.Contains(c.subMenu));
                    });
            }

            var paths = new Dictionary<Menu, string>();
            if (root != null) paths[root] = "Root";
            foreach (var menu in menus)
                for (int i = 0; i < (menu.controls?.Count ?? 0); i++)
                {
                    var c = menu.controls[i]; if (c == null) continue;
                    string path = paths[menu] + "/" + (c.name ?? "") + " [" + i + "]";
                    if (c.type == Control.ControlType.SubMenu && c.subMenu != null && !paths.ContainsKey(c.subMenu)) paths[c.subMenu] = path;
                    var parameters = Parameters(c).Distinct().ToArray();
                    bool removed = !kept[menu].Contains(i);
                    string reason = removed ? c.type == Control.ControlType.SubMenu ? "empty submenu" :
                        parameters.All(frozen.Contains) ? "frozen inputs" : "no observable consumer" :
                        Protected(c) ? "explicit menu/control preservation" :
                        parameters.Any(live.Contains) ? "live or preserved input: " + string.Join(", ", parameters.Where(live.Contains)) :
                        c.type == Control.ControlType.SubMenu ? "retained navigation or activation parameter" :
                        parameters.Length == 0 ? "control without parameter inputs" : "cleanup disabled or unsupported control";
                    report.Controls.Add(new ExpressionControlReport { Path = path, Name = c.name, Parameters = parameters, Removed = removed, Reason = reason });
                }
            if (menus.All(m => kept[m].Count == (m.controls?.Count ?? 0)))
            { report.OutputMenus = report.InputMenus; report.OutputControls = report.InputControls; return root; }

            var reachable = new HashSet<Menu>(); var queue = new Queue<Menu>();
            if (root != null) queue.Enqueue(root);
            while (queue.Count > 0)
            {
                var menu = queue.Dequeue(); if (!reachable.Add(menu)) continue;
                foreach (int i in kept[menu])
                    if (menu.controls[i]?.type == Control.ControlType.SubMenu && menu.controls[i].subMenu != null) queue.Enqueue(menu.controls[i].subMenu);
            }
            var copies = new Dictionary<Menu, Menu>();
            foreach (var source in reachable)
            {
                var copy = UnityEngine.Object.Instantiate(source); copy.name = source.name;
                copies.Add(source, copy); save(copy); replacement?.Invoke(source, copy);
            }
            foreach (var pair in copies)
            {
                var controls = pair.Value.controls;
                pair.Value.controls = Enumerable.Range(0, controls?.Count ?? 0).Where(kept[pair.Key].Contains).Select(i => controls[i]).ToList();
                foreach (var c in pair.Value.controls)
                    if (c?.type == Control.ControlType.SubMenu && c.subMenu != null) c.subMenu = copies[c.subMenu];
            }
            report.OutputMenus = copies.Count; report.OutputControls = copies.Values.Sum(m => m.controls?.Count ?? 0);
            return root == null ? null : copies[root];
        }
    }
}
