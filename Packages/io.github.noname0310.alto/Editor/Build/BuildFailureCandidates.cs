using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace Noname.AvatarTools.Build
{
    internal sealed class BuildFailureCandidate
    {
        internal Component Component;
        internal string Evidence;
        internal int MatchingInstances;
    }

    internal static class BuildFailureCandidates
    {
        internal static IReadOnlyList<BuildFailureCandidate> Find(GameObject avatar, IEnumerable<AvatarBuildErrorTrace> errors)
        {
            var components = avatar.GetComponentsInChildren<Component>(true).Where(c => c != null &&
                c.GetType().Assembly != typeof(ALTOBuildExclusions).Assembly && IsBuildOnly(c.GetType())).ToArray();
            var signatures = (errors ?? Array.Empty<AvatarBuildErrorTrace>()).Where(e => e != null)
                .SelectMany(e => (e.StackTrace ?? "").Split('\n')).Select(s => s.Trim()).Where(IsFrame).Distinct().ToArray();
            var result = new List<BuildFailureCandidate>();
            foreach (var group in components.GroupBy(c => c.GetType()))
            {
                // Use only method signatures, never prose in an error message. An
                // argument type proves participation, not causality or safe removal.
                var type = group.Key;
                string token = Regex.Escape(type.FullName ?? type.Name).Replace(@"\+", @"[+./]");
                var matcher = new Regex(@"(?<![\w.+`])" + token + @"(?=[\s,:>)\[&]|[.:][\w<>`]+\s*\()");
                string evidence = signatures.FirstOrDefault(s => matcher.IsMatch(s.Substring(0, s.IndexOf(')') + 1)));
                if (evidence == null) continue;
                foreach (var component in group)
                    result.Add(new BuildFailureCandidate { Component = component, Evidence = evidence, MatchingInstances = group.Count() });
            }
            return result;
        }

        private static bool IsFrame(string value)
        {
            int open = value.IndexOf('('), close = value.IndexOf(')');
            if (open < 1 || close <= open) return false;
            return Regex.IsMatch(value.Substring(0, open), @"^(?:at\s+)?[\w.+`<>/]+[.:][\w<>`]+\s*$");
        }

        internal static bool IsBuildOnly(Type type) => typeof(VRC.SDKBase.IEditorOnly).IsAssignableFrom(type) ||
            typeof(nadena.dev.ndmf.INDMFEditorOnly).IsAssignableFrom(type);

        internal static Component InClone(Component source, GameObject sourceAvatar, GameObject clone)
        {
            if (source == null || !source.transform.IsChildOf(sourceAvatar.transform))
                throw new InvalidOperationException("The exclusion candidate no longer belongs to this avatar.");
            var indices = new Stack<int>();
            for (var t = source.transform; t != sourceAvatar.transform; t = t.parent) indices.Push(t.GetSiblingIndex());
            var target = clone.transform;
            while (indices.Count > 0)
            {
                int index = indices.Pop();
                if (index >= target.childCount) throw new InvalidOperationException("The build clone hierarchy changed before candidate mapping.");
                target = target.GetChild(index);
            }
            int componentIndex = Array.IndexOf(source.GetComponents(source.GetType()), source);
            var matches = target.GetComponents(source.GetType());
            if (componentIndex < 0 || componentIndex >= matches.Length) throw new InvalidOperationException("The candidate component could not be mapped to the fresh clone.");
            return matches[componentIndex];
        }

        internal static AvatarBuildAnalysisResult Test(ALTOBuildExclusions filter, Component candidate)
        {
            var avatar = filter.FindDescriptor()?.gameObject;
            Validate(filter, candidate);
            return AvatarBuildAnalysis.Analyze(avatar, clone =>
            {
                var clonedFilter = (ALTOBuildExclusions)InClone(filter, avatar, clone);
                var clonedCandidate = InClone(candidate, avatar, clone);
                clonedFilter.ExcludedComponents ??= new();
                if (!clonedFilter.ExcludedComponents.Contains(clonedCandidate)) clonedFilter.ExcludedComponents.Add(clonedCandidate);
            });
        }

        internal static void Validate(ALTOBuildExclusions filter, Component candidate)
        {
            if (filter == null || candidate == null || !IsBuildOnly(candidate.GetType()) || candidate.GetType().Assembly == typeof(ALTOBuildExclusions).Assembly)
                throw new InvalidOperationException("Choose an existing build-only component and Build Exclusions.");
            var avatar = filter.FindDescriptor()?.gameObject;
            if (avatar == null || !candidate.transform.IsChildOf(avatar.transform))
                throw new InvalidOperationException("The candidate must belong to this filter's avatar.");
            if (!OptimizationBuildScope.Applies(filter, EditorUserBuildSettings.activeBuildTarget))
                throw new InvalidOperationException("The selected Build Exclusions is inactive on the current platform.");
            var scope = filter.ScopeRoot != null ? filter.ScopeRoot : filter.transform;
            if (!candidate.transform.IsChildOf(scope)) throw new InvalidOperationException("The candidate is outside this filter's Scope Root.");
            if ((filter.ExcludedComponents ?? new()).Contains(candidate)) throw new InvalidOperationException("The candidate is already excluded.");
        }

        internal static void AddSelected(ALTOBuildExclusions filter, Component candidate)
        {
            Validate(filter, candidate);
            Undo.RecordObject(filter, "Add selected build exclusion");
            filter.ExcludedComponents ??= new(); filter.ExcludedComponents.Add(candidate);
            PrefabUtility.RecordPrefabInstancePropertyModifications(filter);
            EditorUtility.SetDirty(filter);
        }
    }
}
