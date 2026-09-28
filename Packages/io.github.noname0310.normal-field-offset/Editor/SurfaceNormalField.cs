using System;
using System.Collections.Generic;
using UnityEngine;

namespace Noname.AvatarTools.Editor
{
    /// <summary>
    /// Extends the body's interpolated surface normals to nearby space by closest-triangle lookup.
    /// A BVH keeps this practical for dense body and clothing meshes. Positions/normals are world-space.
    /// </summary>
    internal sealed class SurfaceNormalField
    {
        private readonly Vector3[] positions;
        private readonly Vector3[] normals;
        private readonly Triangle[] triangles;
        private readonly List<Node> nodes = new List<Node>();

        private struct Triangle
        {
            internal int A, B, C, Id;
            internal Bounds Bounds;
            internal Vector3 Center;
        }

        private struct Node
        {
            internal Bounds Bounds;
            internal int Start, Count, Left, Right;
        }

        private sealed class CenterComparer : IComparer<Triangle>
        {
            private readonly int axis;
            internal CenterComparer(int axis) { this.axis = axis; }
            public int Compare(Triangle a, Triangle b)
            {
                int result = a.Center[axis].CompareTo(b.Center[axis]);
                return result != 0 ? result : a.Id.CompareTo(b.Id);
            }
        }

        internal SurfaceNormalField(Vector3[] positions, Vector3[] normals, int[] indices)
        {
            this.positions = positions;
            this.normals = normals;
            var valid = new List<Triangle>(indices.Length / 3);
            for (int i = 0; i + 2 < indices.Length; i += 3)
            {
                int a = indices[i], b = indices[i + 1], c = indices[i + 2];
                var cross = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
                if (!Finite(cross) || cross.sqrMagnitude < 1e-20f) continue;
                var bounds = new Bounds(positions[a], Vector3.zero);
                bounds.Encapsulate(positions[b]);
                bounds.Encapsulate(positions[c]);
                valid.Add(new Triangle
                {
                    A = a, B = b, C = c, Id = i / 3, Bounds = bounds,
                    Center = (positions[a] + positions[b] + positions[c]) / 3f
                });
            }

            if (valid.Count == 0)
                throw new InvalidOperationException("Normal Field Offset: the target body has no usable triangles.");
            triangles = valid.ToArray();
            BuildNode(0, triangles.Length);
        }

        internal bool Sample(Vector3 point, float maxDistance, out Vector3 normal, out float distance)
        {
            float bestDistanceSq = maxDistance > 0 ? maxDistance * maxDistance : float.PositiveInfinity;
            int best = -1;
            var barycentric = Vector3.zero;
            Search(0, point, ref bestDistanceSq, ref best, ref barycentric);
            normal = Vector3.zero;
            distance = Mathf.Sqrt(bestDistanceSq);
            if (best < 0) return false;

            var t = triangles[best];
            normal = normals[t.A] * barycentric.x + normals[t.B] * barycentric.y + normals[t.C] * barycentric.z;
            if (!Finite(normal) || normal.sqrMagnitude < 1e-12f)
                normal = Vector3.Cross(positions[t.B] - positions[t.A], positions[t.C] - positions[t.A]);
            normal.Normalize();
            return normal.sqrMagnitude > 0;
        }

        private int BuildNode(int start, int count)
        {
            var bounds = triangles[start].Bounds;
            var centers = new Bounds(triangles[start].Center, Vector3.zero);
            for (int i = start + 1; i < start + count; i++)
            {
                bounds.Encapsulate(triangles[i].Bounds);
                centers.Encapsulate(triangles[i].Center);
            }
            int index = nodes.Count;
            nodes.Add(default);
            if (count <= 8)
            {
                nodes[index] = new Node { Bounds = bounds, Start = start, Count = count };
                return index;
            }

            var size = centers.size;
            int axis = size.x >= size.y && size.x >= size.z ? 0 : size.y >= size.z ? 1 : 2;
            Array.Sort(triangles, start, count, new CenterComparer(axis));
            int half = count / 2;
            int left = BuildNode(start, half);
            int right = BuildNode(start + half, count - half);
            nodes[index] = new Node { Bounds = bounds, Left = left, Right = right };
            return index;
        }

        private void Search(int index, Vector3 point, ref float bestDistanceSq, ref int best, ref Vector3 barycentric)
        {
            var node = nodes[index];
            if (node.Bounds.SqrDistance(point) > bestDistanceSq) return;
            if (node.Count == 0)
            {
                int first = node.Left, second = node.Right;
                if (nodes[second].Bounds.SqrDistance(point) < nodes[first].Bounds.SqrDistance(point))
                {
                    first = node.Right;
                    second = node.Left;
                }
                Search(first, point, ref bestDistanceSq, ref best, ref barycentric);
                Search(second, point, ref bestDistanceSq, ref best, ref barycentric);
                return;
            }

            for (int i = node.Start; i < node.Start + node.Count; i++)
            {
                var t = triangles[i];
                var bary = ClosestBarycentric(point, positions[t.A], positions[t.B], positions[t.C]);
                var closest = positions[t.A] * bary.x + positions[t.B] * bary.y + positions[t.C] * bary.z;
                float d = (point - closest).sqrMagnitude;
                if (d > bestDistanceSq || (d == bestDistanceSq && best >= 0 && t.Id > triangles[best].Id)) continue;
                bestDistanceSq = d;
                best = i;
                barycentric = bary;
            }
        }

        // Closest point regions of a triangle; returns weights for normal interpolation too.
        private static Vector3 ClosestBarycentric(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            var ab = b - a;
            var ac = c - a;
            var ap = p - a;
            float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0 && d2 <= 0) return new Vector3(1, 0, 0);

            var bp = p - b;
            float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0 && d4 <= d3) return new Vector3(0, 1, 0);
            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0 && d1 >= 0 && d3 <= 0)
            {
                float v = d1 / (d1 - d3);
                return new Vector3(1 - v, v, 0);
            }

            var cp = p - c;
            float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0 && d5 <= d6) return new Vector3(0, 0, 1);
            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0 && d2 >= 0 && d6 <= 0)
            {
                float w = d2 / (d2 - d6);
                return new Vector3(1 - w, 0, w);
            }
            float va = d3 * d6 - d5 * d4;
            if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0)
            {
                float w = (d4 - d3) / ((d4 - d3) + (d5 - d6));
                return new Vector3(0, 1 - w, w);
            }
            float denominator = 1f / (va + vb + vc);
            float insideV = vb * denominator, insideW = vc * denominator;
            return new Vector3(1 - insideV - insideW, insideV, insideW);
        }

        internal static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        internal static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
    }
}
