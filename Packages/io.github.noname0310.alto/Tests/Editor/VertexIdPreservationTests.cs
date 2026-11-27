using System;
using System.Collections.Generic;
using System.Linq;
using Noname.AvatarTools.Analysis;
using Noname.AvatarTools.Meshes;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Noname.AvatarTools.BlendShapeOptimization.Tests
{
    public sealed partial class BlendShapeOptimizationTests
    {
        private MeshMergeCandidate VertexIdCandidate(SkinnedMeshRenderer part, Material material)
        {
            part.sharedMaterial = material;
            var candidate = new MeshMergeCandidate { Renderer = part, Shaders = new ShaderPropertyUsage() };
            candidate.VertexIdMaterials.Add(material);
            Assert.That(ShaderVertexIdRewriter.TryCreate(material.shader, out var adapter, out string issue), Is.True, issue);
            candidate.VertexIdAdapters.Add(material.shader, adapter);
            return candidate;
        }

        [TestCase(false)] [TestCase(true)]
        public void VertexIdIdentityWithoutHolesDoesNotRequireAnUnusedAdapter(bool trim)
        {
            var bone = PolicyChild("Bone").transform;
            var plain = AssemblyPart("Plain", new[] { bone }, Vector3.zero);
            var effect = AssemblyPart("Effect", new[] { bone }, Vector3.zero);
            var material = Track(new Material(Shader.Find("Hidden/Noname/Vertex ID Preservation")));
            effect.sharedMaterial = material;
            var candidate = new MeshMergeCandidate { Renderer = effect, Shaders = new ShaderPropertyUsage() };
            candidate.VertexIdMaterials.Add(material);
            using var plan = VertexIdMergePlan.Create(new[] { new MeshMergeCandidate { Renderer = plain }, candidate }, trim, 3);
            Assert.That(candidate.VertexIdAdaptersPrepared, Is.False);
            Assert.That(plan.VertexOrder[0], Is.SameAs(effect));
            Assert.That(plan.KeepVertices, Does.Contain(effect));
            Assert.That(plan.Materials[effect][0], Is.SameAs(material));
            Assert.That(plan.AttributeInputs, Is.Empty);
            Assert.That(plan.LiteralShaders + plan.UniformShaders, Is.Zero);
        }

        [Test]
        public void VertexIdAdaptersArePreparedOnlyForOtherwiseCompatiblePairs()
        {
            var pair = MergePair();
            var material = Track(new Material(Shader.Find("Hidden/Noname/Vertex ID Preservation")));
            pair.First.sharedMaterial = pair.Second.sharedMaterial = material;
            var policy = new MeshMergePolicy { PreserveVertexIds = true };
            var candidates = MergeCandidates(new[] { pair.First, pair.Second }, policy: policy);
            Assert.That(candidates.All(c => !c.VertexIdAdaptersPrepared), Is.True);
            pair.First.shadowCastingMode = ShadowCastingMode.Off;
            pair.Second.shadowCastingMode = ShadowCastingMode.On;
            Assert.That(MeshMergeAnalysis.Incompatibility(candidates[0], candidates[1], policy), Does.Contain("shadow settings"));
            Assert.That(candidates.All(c => !c.VertexIdAdaptersPrepared), Is.True);
            pair.First.shadowCastingMode = ShadowCastingMode.On;
            Assert.That(MeshMergeAnalysis.Incompatibility(candidates[0], candidates[1], policy), Is.Null);
            Assert.That(candidates.All(c => c.VertexIdAdaptersPrepared), Is.True);
            Assert.That(candidates[0].VertexIdAdapters[material.shader], Is.SameAs(candidates[1].VertexIdAdapters[material.shader]));
        }

        [Test]
        public void VertexIdIdentityLayoutDoesNotMoveThePinnedRendererOrItsMorphInterface()
        {
            var bone = PolicyChild("Bone").transform;
            var body = AssemblyPart("Pinned Body", new[] { bone }, Vector3.zero);
            var effect = AssemblyPart("Index sensitive", new[] { bone }, Vector3.zero);
            Frame(body.sharedMesh, "Pinned Morph", 100, Vector3.up);
            Frame(effect.sharedMesh, "Other Morph", 100, Vector3.right);
            var plain = new MeshMergeCandidate { Renderer = body, Shaders = new ShaderPropertyUsage(), Pinned = true };
            var sensitive = VertexIdCandidate(effect, Track(new Material(Shader.Find("Hidden/Noname/Vertex ID Preservation"))));
            using var plan = VertexIdMergePlan.Create(new[] { plain, sensitive }, true, 3);
            Assert.That(plan.VertexOrder[0], Is.SameAs(effect));
            Assert.That(plan.Materials[effect][0], Is.SameAs(effect.sharedMaterial));
            Assert.That(plan.AttributeInputs, Is.Empty);
            using var assembly = SkinnedMeshAssembler.Create(new[] { body, effect }, true, removeUnusedData: true, vertexIds: plan);
            Assert.That(assembly.Mesh.GetBlendShapeName(0), Is.EqualTo("Pinned Morph"));
            Assert.That(assembly.Materials[0], Is.SameAs(body.sharedMaterial));
            Assert.That(assembly.Mesh.GetIndices(0), Is.EqualTo(new[] { 3, 4, 5 }));
            Assert.That(assembly.Mesh.GetIndices(1), Is.EqualTo(new[] { 0, 1, 2 }));
            var delta = new Vector3[6]; assembly.Mesh.GetBlendShapeFrameVertices(0, 0, delta, null, null);
            Assert.That(delta.Take(3), Is.All.EqualTo(Vector3.zero));
            Assert.That(delta.Skip(3), Is.All.EqualTo(Vector3.up));
        }

        [TestCase(2, false)] [TestCase(3, true)] [TestCase(4, true)]
        public void VertexIdOffsetsChooseLiteralOrSharedUniformShadersAndPreserveGpuOutput(int count, bool uniform)
        {
            var bone = PolicyChild("Bone").transform;
            var material = Track(new Material(Shader.Find("Hidden/Noname/Vertex ID Preservation")));
            var parts = Enumerable.Range(0, count).Select(i => AssemblyPart("Part " + i, new[] { bone }, new Vector3(i * 1.2f - count * 0.6f, 0, 0))).ToArray();
            var candidates = parts.Select(p => VertexIdCandidate(p, material)).ToArray();
            using var plan = VertexIdMergePlan.Create(candidates, true, 3);
            Assert.That(plan.UniformShaders, Is.EqualTo(uniform ? 1 : 0));
            Assert.That(plan.LiteralShaders, Is.EqualTo(uniform ? 0 : 1));
            if (uniform)
            {
                Assert.That(plan.Materials.Values.SelectMany(m => m).Select(m => m.shader).Distinct().Count(), Is.EqualTo(1));
                for (int i = 0; i < count; i++) Assert.That(plan.Materials[parts[i]][0].GetInteger(ShaderVertexIdRewriter.OffsetProperty), Is.EqualTo(-i * 3));
            }
            using var assembly = SkinnedMeshAssembler.Create(parts, true, removeUnusedData: true, vertexIds: plan);
            Assert.That(assembly.Materials.Length, Is.EqualTo(count), "Different per-draw offsets must not collapse into one material draw.");
            AssertVertexIdPixels(parts, assembly);
        }

        [Test]
        public void VertexIdSparseCompactionStoresOriginalIdsInAnUnusedAttribute()
        {
            var part = SparseVertexIdPart();
            part.sharedMaterial = Track(new Material(Shader.Find("Hidden/Noname/Vertex ID Preservation")));
            var candidate = new MeshMergeCandidate { Renderer = part, Shaders = new ShaderPropertyUsage() };
            candidate.VertexIdMaterials.Add(part.sharedMaterial);
            using var plan = VertexIdMergePlan.Create(new[] { candidate }, true, 3);
            Assert.That(candidate.VertexIdAdaptersPrepared, Is.True);
            Assert.That(plan.AttributeInputs, Does.Contain(part));
            using var assembly = SkinnedMeshAssembler.Create(new[] { part }, true, removeUnusedData: true, vertexIds: plan);
            Assert.That(assembly.Mesh.vertexCount, Is.EqualTo(3));
            var originalIds = new List<Vector2>(); assembly.Mesh.GetUVs(plan.AttributeChannel, originalIds);
            Assert.That(originalIds.Select(v => v.x), Is.EqualTo(new[] { 10f, 50f, 99f }));
            AssertVertexIdPixels(new[] { part }, assembly);
        }

        private SkinnedMeshRenderer SparseVertexIdPart()
        {
            var part = AssemblyPart("Sparse", new[] { PolicyChild("Bone").transform }, Vector3.zero);
            var positions = new Vector3[100]; positions[10] = Vector3.zero; positions[50] = Vector3.right; positions[99] = Vector3.up;
            part.sharedMesh.Clear(); part.sharedMesh.vertices = positions;
            part.sharedMesh.normals = Enumerable.Repeat(Vector3.back, 100).ToArray();
            part.sharedMesh.bindposes = new[] { Matrix4x4.identity };
            part.sharedMesh.boneWeights = Enumerable.Range(0, 100).Select(_ => new BoneWeight { boneIndex0 = 0, weight0 = 1 }).ToArray();
            part.sharedMesh.triangles = new[] { 10, 50, 99 };
            return part;
        }

        [TestCase(false)] [TestCase(true)]
        public void VertexIdAttributeSelectionPreservesFollowingUvAliases(bool explicitLastChannel)
        {
            var part = SparseVertexIdPart();
            var uv = Enumerable.Repeat(new Vector2(0.75f, 0.25f), 100).ToList();
            part.sharedMesh.SetUVs(0, uv);
            if (explicitLastChannel) part.sharedMesh.SetUVs(7, uv);
            var candidate = VertexIdCandidate(part, Track(new Material(Shader.Find("Hidden/Noname/Vertex ID UV Alias"))));
            using var plan = VertexIdMergePlan.Create(new[] { candidate }, true, 3);
            Assert.That(plan.AttributeChannel, Is.EqualTo(explicitLastChannel ? 6 : -1),
                "An absent TEXCOORD7 must not start reading the original-ID stream through UV aliasing.");
            using var assembly = SkinnedMeshAssembler.Create(new[] { part }, true, removeUnusedData: true, vertexIds: plan);
            Assert.That(assembly.Mesh.vertexCount, Is.EqualTo(explicitLastChannel ? 3 : 100));
            AssertVertexIdPixels(new[] { part }, assembly);
        }

        [Test]
        public void VertexIdSmallHolesCostLessThanAddingAnAttribute()
        {
            var part = AssemblyPart("Small hole", new[] { PolicyChild("Bone").transform }, Vector3.zero);
            var vertices = Enumerable.Range(0, 100).Select(i => new Vector3(i, 0, 0)).ToArray();
            part.sharedMesh.Clear(); part.sharedMesh.vertices = vertices;
            part.sharedMesh.bindposes = new[] { Matrix4x4.identity };
            part.sharedMesh.boneWeights = vertices.Select(_ => new BoneWeight { boneIndex0 = 0, weight0 = 1 }).ToArray();
            part.sharedMesh.SetIndices(Enumerable.Range(1, 99).ToArray(), MeshTopology.Triangles, 0);
            var candidate = VertexIdCandidate(part, Track(new Material(Shader.Find("Hidden/Noname/Vertex ID Preservation"))));
            using var plan = VertexIdMergePlan.Create(new[] { candidate }, true, 3);
            Assert.That(plan.KeepVertices, Does.Contain(part));
            Assert.That(plan.AttributeInputs, Is.Empty);
            Assert.That(plan.Materials[part][0], Is.SameAs(part.sharedMaterial));
            Assert.That(SkinnedMeshAssembler.HasUnusedData(part, includeVertices: false), Is.False,
                "Preserved holes must not cause repeated no-op rewrites in the fixed point.");
        }

        [Test]
        public void VertexIdUniformValuesSurviveGeneratedAssetPersistence()
        {
            var bone = PolicyChild("Bone").transform;
            var material = Track(new Material(Shader.Find("Hidden/Noname/Vertex ID Preservation")));
            var parts = Enumerable.Range(0, 3).Select(i => AssemblyPart("Part " + i, new[] { bone }, Vector3.zero)).ToArray();
            using var plan = VertexIdMergePlan.Create(parts.Select(p => VertexIdCandidate(p, material)).ToArray(), false, 3);
            string path = AssetDatabase.GenerateUniqueAssetPath("Assets/__ALTO_VertexIdTest.asset");
            try
            {
                var container = new Mesh(); AssetDatabase.CreateAsset(container, path);
                plan.Save(asset => AssetDatabase.AddObjectToAsset(asset, container));
                AssetDatabase.SaveAssetIfDirty(container); AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                Assert.That(AssetDatabase.LoadAllAssetsAtPath(path).OfType<Material>().Select(m => m.GetInteger(ShaderVertexIdRewriter.OffsetProperty)).OrderBy(v => v),
                    Is.EqualTo(new[] { -6, -3, 0 }));
            }
            finally { AssetDatabase.DeleteAsset(path); }
        }

        [Test]
        public void RegionFilteringKeepsTheOriginalBaseVertexAndVertexNumbers()
        {
            var mesh = Track(new Mesh()); mesh.vertices = Enumerable.Range(0, 9).Select(i => new Vector3(i, i, i)).ToArray();
            mesh.SetIndices(new[] { 0, 1, 2, 3, 4, 5 }, MeshTopology.Triangles, 0, false, 3);
            var masks = BlendShapeRegionMasks.Create(mesh, Array.Empty<string>(), 0);
            var filtered = Track(masks.Remove(mesh, new[] { true, false }));
            Assert.That(filtered.GetBaseVertex(0), Is.EqualTo(3));
            Assert.That(filtered.GetIndices(0, false), Is.EqualTo(new[] { 3, 4, 5 }));
            Assert.That(filtered.GetIndices(0, true), Is.EqualTo(new[] { 6, 7, 8 }));
            Assert.That(filtered.vertices, Is.EqualTo(mesh.vertices));
        }

        [TestCase(0f, 0f, false, true, false)] [TestCase(0.02f, 0.38f, false, true, false)]
        [TestCase(0f, 0f, true, true, false)] [TestCase(0.02f, 0.38f, true, true, false)]
        [TestCase(0f, 0.38f, true, false, false)] [TestCase(0.02f, 1f, true, false, false)]
        [TestCase(0.02f, 0.38f, true, true, true)]
        public void ParticleMeshMaterialBindingPreservesNativeEmissionAfterMerge(float normalOffset, float randomDirection,
            bool differentRootBones, bool preserveSequence, bool rendererRoundoff)
        {
            var bone = PolicyChild("Bone").transform;
            var other = AssemblyPart("Other surface", new[] { bone }, Vector3.zero);
            var source = AssemblyPart("Emitter surface", new[] { bone }, Vector3.zero);
            if (rendererRoundoff)
            {
                source.transform.localRotation = Quaternion.Euler(0, 0.00001f, 0);
                source.transform.localScale = Vector3.one * 0.99999994f;
            }
            if (differentRootBones)
            {
                source.rootBone = PolicyChild("Source coordinate root").transform;
                source.rootBone.localPosition = new Vector3(0.1f, -0.2f, 0.3f);
                source.rootBone.localRotation = Quaternion.Euler(15, 30, 5);
                source.rootBone.localScale = Vector3.one * 1.3f;
                other.rootBone = PolicyChild("Destination coordinate root").transform;
                other.rootBone.localPosition = new Vector3(-0.3f, 0.4f, 0.1f);
                other.rootBone.localRotation = Quaternion.Euler(10, -25, 3);
            }
            other.sharedMesh.vertices = other.sharedMesh.vertices.Select(v => v + Vector3.right * 10).ToArray();
            source.sharedMesh.colors = new[] { Color.red, Color.green, Color.blue };
            other.sharedMaterials = source.sharedMaterials;
            var isolation = new Dictionary<SkinnedMeshRenderer, HashSet<int>> { [source] = new() { 0 } };
            using var assembly = SkinnedMeshAssembler.Create(new[] { other, source }, true, isolatedMaterialSlots: isolation, reorderMaterialSlots: true);
            Assert.That(assembly.Mesh.subMeshCount, Is.EqualTo(2), "An observed surface must not absorb another material range.");
            var output = AssemblyOutput(assembly);
            var observedInput = new MeshMergeCandidate { Renderer = source };
            observedInput.ParticleBindings.Add(new ParticleMeshBinding());
            var firstInput = new MeshMergeCandidate { Renderer = other };
            if (!preserveSequence) firstInput.ParticleBindings.Add(new ParticleMeshBinding());
            output.rootBone = MeshMergePolicy.DestinationCoordinateRoot(new[] { firstInput, observedInput });
            Assert.That(output.rootBone, Is.SameAs(preserveSequence ? source.rootBone : other.rootBone));
            var policy = new MeshMergePolicy { Mutations = new TransformMutationAnalysis() };
            Assert.That(policy.RendererFramesMatch(source, output), Is.True);
            Assert.That(policy.ParticleDistributionsMatch(source, output), Is.True);
            int sampleCount = preserveSequence ? 128 : 32768;
            ParticleSystem Particles(string name, SkinnedMeshRenderer mesh)
            {
                var particles = PolicyChild(name).AddComponent<ParticleSystem>();
                particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                particles.GetComponent<ParticleSystemRenderer>().enabled = false;
                var main = particles.main; main.playOnAwake = false; main.startLifetime = 10; main.startSpeed = 1;
                main.simulationSpace = ParticleSystemSimulationSpace.World; main.maxParticles = sampleCount;
                var shape = particles.shape; shape.enabled = true; shape.shapeType = ParticleSystemShapeType.SkinnedMeshRenderer;
                shape.skinnedMeshRenderer = mesh; shape.meshShapeType = ParticleSystemMeshShapeType.Triangle;
                shape.useMeshMaterialIndex = true; shape.meshMaterialIndex = 0; shape.useMeshColors = true;
                shape.normalOffset = normalOffset; shape.randomDirectionAmount = randomDirection;
                return particles;
            }
            var before = Particles("Before", source); var after = Particles("After", source);
            Assert.That(ParticleMeshBinding.TryCapture(after, source, null, out var binding), Is.True);
            binding.Apply(output, assembly.MaterialSlots[source]);
            var camera = PolicyChild("Particle skinning camera").AddComponent<Camera>(); camera.scene = scene;
            camera.transform.localPosition = new Vector3(0, 0.5f, -4); camera.orthographic = true; camera.orthographicSize = 15;
            camera.enabled = false;
            var target = RenderTexture.GetTemporary(32, 32, 24);
            try
            {
                camera.targetTexture = target; camera.Render();
                ParticleSystem.Particle[] Sample(ParticleSystem particles)
                {
                    particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                    particles.useAutoRandomSeed = false; particles.randomSeed = 12345;
                    particles.Emit(sampleCount);
                    var values = new ParticleSystem.Particle[sampleCount];
                    Assert.That(particles.GetParticles(values), Is.EqualTo(values.Length)); return values;
                }
                var a = Sample(before); var b = Sample(after);
                Assert.That(a.Select(p => p.position).Distinct().Count(), Is.GreaterThan(32), "The source must sample the triangle surface.");
                for (int i = 0; i < a.Length; i++)
                {
                    Assert.That(Vector3.Distance(a[i].position, b[i].position), Is.LessThan(0.00001f), "Particle position " + i);
                    if (preserveSequence) Assert.That(Vector3.Distance(a[i].velocity, b[i].velocity), Is.LessThan(0.00001f), "Particle velocity " + i);
                    else Assert.That(Math.Abs(a[i].velocity.magnitude - b[i].velocity.magnitude), Is.LessThan(0.00001f), "Particle speed " + i);
                    Assert.That(a[i].startColor, Is.EqualTo(b[i].startColor), "Particle color " + i);
                }
                if (!preserveSequence)
                {
                    Assert.That(a.Zip(b, (x, y) => Vector3.Distance(x.velocity, y.velocity)).Max(), Is.GreaterThan(0.01f));
                    // Native metamorphic regression: compare means and covariance,
                    // not individual random samples. The production decision uses
                    // the similarity-transform proof, never this statistical test.
                    Func<Vector3, double>[] moments = { v => v.x, v => v.y, v => v.z, v => v.x * v.x, v => v.y * v.y,
                        v => v.z * v.z, v => v.x * v.y, v => v.y * v.z, v => v.z * v.x,
                        v => Math.Pow(v.x, 4), v => Math.Pow(v.y, 4), v => Math.Pow(v.z, 4),
                        v => v.x * v.x * v.y * v.y, v => v.y * v.y * v.z * v.z, v => v.z * v.z * v.x * v.x };
                    foreach (var moment in moments)
                    {
                        var x = a.Select(p => moment(p.velocity)).ToArray(); var y = b.Select(p => moment(p.velocity)).ToArray();
                        double meanX = x.Average(), meanY = y.Average();
                        double variance = x.Average(v => (v - meanX) * (v - meanX)) + y.Average(v => (v - meanY) * (v - meanY));
                        Assert.That(Math.Abs(meanX - meanY), Is.LessThan(8 * Math.Sqrt(variance / sampleCount) + 0.00001), "Particle directional distribution changed.");
                    }
                    source.rootBone.localScale = new Vector3(1, 2, 1);
                    Assert.That(policy.ParticleDistributionsMatch(source, output), Is.False, "Nonuniform relative scale needs a separate proof.");
                }
            }
            finally { camera.targetTexture = null; RenderTexture.ReleaseTemporary(target); }
        }

        [Test]
        public void ParticleMeshBindingRejectsWholeMeshAndVertexSampling()
        {
            var source = AssemblyPart("Source", new[] { PolicyChild("Bone").transform }, Vector3.zero);
            var particles = PolicyChild("Particles").AddComponent<ParticleSystem>();
            var shape = particles.shape; shape.shapeType = ParticleSystemShapeType.SkinnedMeshRenderer; shape.skinnedMeshRenderer = source;
            shape.meshShapeType = ParticleSystemMeshShapeType.Triangle; shape.useMeshMaterialIndex = false;
            Assert.That(ParticleMeshBinding.TryCapture(particles, source, null, out _), Is.False);
            shape.useMeshMaterialIndex = true; shape.meshMaterialIndex = 0; shape.meshShapeType = ParticleSystemMeshShapeType.Vertex;
            Assert.That(ParticleMeshBinding.TryCapture(particles, source, null, out _), Is.False);
        }

        [TestCase(false)] [TestCase(true)]
        public void ParticleMeshShapeRendererReferencesAreReadersRatherThanTransformWriters(bool skinned)
        {
            Renderer source = skinned ? AssemblyPart("Source", new[] { PolicyChild("Bone").transform }, Vector3.zero) :
                PolicyChild("Source").AddComponent<MeshRenderer>();
            var particles = PolicyChild("Particles").AddComponent<ParticleSystem>();
            var shape = particles.shape;
            shape.shapeType = skinned ? ParticleSystemShapeType.SkinnedMeshRenderer : ParticleSystemShapeType.MeshRenderer;
            if (skinned) shape.skinnedMeshRenderer = (SkinnedMeshRenderer)source;
            else shape.meshRenderer = (MeshRenderer)source;
            var index = RigidAnimations();
            var mutations = TransformMutationAnalysis.Create(root, index);
            Assert.That(mutations.Mutable, Has.No.Member(source.transform));
            Assert.That(mutations.MayChangeScale(source.transform), Is.False);
            var writer = source.gameObject.AddComponent<UnityEngine.Animations.ParentConstraint>();
            mutations = TransformMutationAnalysis.Create(root, index);
            Assert.That(mutations.Mutable, Does.Contain(source.transform));
            Assert.That(mutations.WritersOf(source.transform), Does.Contain(writer));
        }

        [Test]
        public void ParticleMeshRandomSequenceOptionRefinesDistributionPreservation()
        {
            var pair = MergePair(); pair.First.transform.localPosition = pair.Second.transform.localPosition = Vector3.zero;
            pair.First.rootBone = PolicyChild("First particle frame").transform;
            pair.Second.rootBone = PolicyChild("Second particle frame").transform;
            pair.Second.rootBone.localRotation = Quaternion.Euler(15, 40, 10);
            pair.Second.rootBone.localScale = Vector3.one * 1.3f;
            var policy = new MeshMergePolicy { ExpandBounds = true, AllowCoordinateSpaceChanges = true, Mutations = new TransformMutationAnalysis() };
            var candidates = MergeCandidates(new[] { pair.First, pair.Second }, policy: policy);
            foreach (var candidate in candidates) candidate.ParticleBindings.Add(new ParticleMeshBinding());
            Assert.That(MeshMergeAnalysis.Incompatibility(candidates[0], candidates[1], policy), Is.Null);
            policy.PreserveParticleRandomSequence = true;
            Assert.That(MeshMergeAnalysis.Incompatibility(candidates[0], candidates[1], policy), Does.Contain("random sequences"));
            policy.PreserveParticleRandomSequence = false;
            pair.Second.rootBone.localScale = new Vector3(1, 2, 1);
            Assert.That(MeshMergeAnalysis.Incompatibility(candidates[0], candidates[1], policy), Does.Contain("relative scale"));
        }

        [Test]
        public void ParticleRendererFrameProofRejectsRelativeMotionAndScaleBeyondRoundoff()
        {
            var pair = MergePair(); pair.First.transform.localPosition = pair.Second.transform.localPosition = Vector3.zero;
            var policy = new MeshMergePolicy { Mutations = new TransformMutationAnalysis() };
            pair.Second.transform.localScale = Vector3.one * 0.99999994f;
            Assert.That(policy.RendererFramesMatch(pair.First, pair.Second), Is.True);
            pair.Second.transform.localPosition = Vector3.up * 0.001f;
            Assert.That(policy.RendererFramesMatch(pair.First, pair.Second), Is.False);
            pair.Second.transform.localPosition = Vector3.zero;
            pair.First.transform.localScale = Vector3.one * 0.00000001f;
            pair.Second.transform.localScale = Vector3.one * 0.00000002f;
            Assert.That(policy.RendererFramesMatch(pair.First, pair.Second), Is.False, "Small absolute scales must not hide a 2x relative change.");
            pair.First.transform.localScale = pair.Second.transform.localScale = Vector3.one;
            policy.Mutations.Mutable.Add(pair.Second.transform);
            Assert.That(policy.RendererFramesMatch(pair.First, pair.Second), Is.False, "Current pose equality cannot prove equality under animation.");
        }

        private void AssertVertexIdPixels(SkinnedMeshRenderer[] parts, SkinnedMeshAssembly assembly)
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) Assert.Ignore("GPU output comparison requires a graphics device.");
            var output = AssemblyOutput(assembly); output.enabled = false;
            var camera = PolicyChild("Camera").AddComponent<Camera>(); camera.scene = scene;
            camera.transform.localPosition = new Vector3(0, 0.5f, -4); camera.orthographic = true; camera.orthographicSize = 3;
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black; camera.enabled = false;
            var target = RenderTexture.GetTemporary(128, 128, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            try
            {
                var before = DrawAssembly(camera, target);
                foreach (var part in parts) part.enabled = false;
                output.enabled = true; var after = DrawAssembly(camera, target);
                Assert.That(before.Any(p => p.b > 100), Is.True, "The original mesh must actually render.");
                Assert.That(before.Zip(after, (a, b) => Math.Max(Math.Max(Math.Abs(a.r - b.r), Math.Abs(a.g - b.g)), Math.Abs(a.b - b.b))).Max(),
                    Is.LessThanOrEqualTo(1), "Original shader-visible vertex IDs changed.");
            }
            finally { RenderTexture.ReleaseTemporary(target); }
        }

        [Test]
        public void VertexIdSourceAdapterTracksTheSemanticParameterRatherThanItsSpelling()
        {
            const string source = "#pragma vertex main\nstruct Input { float4 position : POSITION; uint unrelatedName : SV_VertexID; };\nfloat4 main(Input v) : SV_POSITION { return v.position + v.unrelatedName; }";
            Assert.That(ShaderVertexIdRewriter.RewriteProgram(source, -3, false, -1, out var rewritten, out var issue), Is.True, issue);
            Assert.That(rewritten, Does.Contain("v.unrelatedName = v.unrelatedName + 4294967293u;"));
            Assert.That(rewritten, Does.Contain("uint unrelatedName : SV_VertexID"));
        }

        [TestCase(false)] [TestCase(true)]
        public void VertexIdShaderLabEditsRespectCommentsAndStringLiterals(bool uniform)
        {
            Assert.That(ShaderVertexIdRewriter.TryCreate(Shader.Find("Hidden/Noname/Vertex ID Preservation"), out var adapter, out string issue), Is.True, issue);
            string source = adapter.GenerateSource(-3, uniform);
            Assert.That(source, Does.Contain("// Shader \"Keep this comment\" { Properties { }"));
            Assert.That(source, Does.Contain("\"Properties Shader text\""));
            Assert.That(source, Does.Contain("Shader \"Hidden/ALTO/VertexID/"));
            var shader = Track(adapter.CreateShader(-3, uniform));
            Assert.That(ShaderUtil.ShaderHasError(shader), Is.False);
            Assert.That(shader.FindPropertyIndex(ShaderVertexIdRewriter.OffsetProperty) >= 0, Is.EqualTo(uniform));
        }

        [TestCase("#define ID SV_VertexID\n#pragma vertex main\nfloat4 main(uint id : ID) : SV_POSITION { return id; }")]
        [TestCase("#define LINE __LINE__\n#pragma vertex main\nfloat4 main(uint id : SV_VertexID) : SV_POSITION { return id + LINE; }")]
        [TestCase("#define CAT(a,b) a##b\n#pragma vertex main\nfloat4 main(uint id : CAT(SV_,VertexID)) : SV_POSITION { return id; }")]
        [TestCase("#pragma vertex main\nfloat4 main(uint id : SV_VertexID) : SV_POSITION { return id; } float4 helper(uint id) { return main(id); }")]
        [TestCase("#pragma vertex main\nstruct Input {\n#if USE_ID\nuint id : SV_VertexID;\n#endif\n}; float4 main(Input v) : SV_POSITION { return v.id; }")]
        public void VertexIdSourceAdapterRejectsUnverifiedInterfaces(string source)
            => Assert.That(ShaderVertexIdRewriter.RewriteProgram(source, -3, false, -1, out _, out _), Is.False);
    }
}
