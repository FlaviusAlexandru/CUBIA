using System.Collections.Generic;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Teleportation;

namespace DataViz.EditorTools
{
    /// <summary>
    /// Tools > CUBIA > Environment > Build Studio Environment.
    /// Replaces the XR Interaction Toolkit sample room with a clean light studio: a gradient sky, an
    /// "infinite" floor with a fading grid, distance rings with labels and 2 m scale posts (depth cues
    /// for plots scaled up to ~100x), a plot footprint, neutral lighting and a restyled table.
    /// Removes the XRI demo objects. Safe to re-run: it rebuilds the studio in place.
    /// Marks the scene dirty; save it to keep the result.
    /// </summary>
    static class StudioEnvironmentSetup
    {
        const string k_Root = "Assets/Environment";
        const string k_MatDir = k_Root + "/Materials";

        static readonly float[] k_RingRadii = { 5f, 10f, 25f, 50f, 100f };
        static readonly float[] k_PostRadii = { 10f, 25f, 50f };
        const float k_PostHeight = 2f;

        // XRI sample content that CUBIA doesn't use.
        static readonly string[] k_Legacy =
        {
            "Environment/Template Environment", "Environment/Arrows", "Environment/Grid",
            "Interactables",
            "UI/Tutorial Player", "UI/Affordance Callout",
            "UI/Spatial Panel Manipulator Model", "UI/Spatial Panel Manipulator UI Examples",
        };

        static readonly Color k_Horizon = new(0.93f, 0.935f, 0.945f);
        static readonly Color k_Ring = new(0.42f, 0.52f, 0.66f);

        [MenuItem("Tools/CUBIA/Environment/Build Studio Environment")]
        static void Build()
        {
            var table = GameObject.Find("DataVisualizationTable");
            if (table == null) { Debug.LogError("[Studio] DataVisualizationTable not found in the open scene."); return; }

            Undo.SetCurrentGroupName("Build Studio Environment");
            RemoveLegacy();

            GameObject env = GameObject.Find("Environment") ?? new GameObject("Environment");
            Transform old = env.transform.Find("Studio");
            if (old != null) Undo.DestroyObjectImmediate(old.gameObject);

            var studio = new GameObject("Studio");
            Undo.RegisterCreatedObjectUndo(studio, "Studio");
            studio.transform.SetParent(env.transform, false);

            Collider floorCollider = BuildFloor(studio.transform);
            Transform markers = BuildMarkers(studio.transform, table.transform.position);

            var footprint = studio.AddComponent<PlotFloorFootprint>();
            footprint.m_RingCentre = table.transform;
            footprint.m_Markers = markers;
            var plot = Object.FindAnyObjectByType<CoordinateSystemInteractable>(FindObjectsInactive.Include);
            if (plot != null) footprint.m_PlotBounds = plot.GetComponent<Collider>();

            RewireTeleport(floorCollider);
            SetupLighting();
            RestyleTable(table.transform);

            EditorSceneManager.MarkSceneDirty(table.scene);
            Debug.Log("[Studio] Studio environment built. Save the scene to keep it.");
        }

        static void RemoveLegacy()
        {
            foreach (string path in k_Legacy)
            {
                GameObject go = FindByPath(path);
                if (go != null) Undo.DestroyObjectImmediate(go);
            }
            // Drop the UI root if the sample objects were all it held.
            GameObject ui = GameObject.Find("UI");
            if (ui != null && ui.transform.parent == null && ui.transform.childCount == 0)
                Undo.DestroyObjectImmediate(ui);
        }

        static Collider BuildFloor(Transform parent)
        {
            var floor = GameObject.CreatePrimitive(PrimitiveType.Plane); // 10 x 10 m, MeshCollider
            floor.name = "Floor";
            floor.transform.SetParent(parent, false);
            floor.transform.localScale = new Vector3(60f, 1f, 60f);    // 600 x 600 m; it fades into the sky long before the edge
            var r = floor.GetComponent<MeshRenderer>();
            r.sharedMaterial = Mat("StudioFloor", "CUBIA/StudioFloor", m =>
            {
                m.SetVector("_Rings", new Vector4(k_RingRadii[0], k_RingRadii[1], k_RingRadii[2], k_RingRadii[3]));
                m.SetFloat("_Ring4", k_RingRadii[4]);
                // Quiet grid: it should read as a measuring surface, not a debug grid.
                m.SetColor("_MinorColor", new Color(0.80f, 0.81f, 0.83f));
                m.SetColor("_MajorColor", new Color(0.69f, 0.71f, 0.75f));
                m.SetFloat("_FadeStart", 10f);
                m.SetFloat("_FadeEnd", 140f);
            });
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = true;
            GameObjectUtility.SetStaticEditorFlags(floor, StaticEditorFlags.BatchingStatic);
            return floor.GetComponent<Collider>();
        }

        static Transform BuildMarkers(Transform parent, Vector3 centre)
        {
            var markers = new GameObject("ScaleMarkers").transform;
            markers.SetParent(parent, false);
            markers.position = new Vector3(centre.x, 0f, centre.z);

            Material postMat = Mat("ScalePost", "Universal Render Pipeline/Lit", m =>
            {
                m.SetColor("_BaseColor", new Color(0.70f, 0.74f, 0.80f));
                m.SetFloat("_Smoothness", 0.25f);
            });
            Material capMat = Mat("ScalePostCap", "Universal Render Pipeline/Unlit", m => m.SetColor("_BaseColor", k_Ring));

            // Labels and posts sit on the four diagonals, so they never stand right in front of the table.
            var diagonals = new List<Vector3>();
            for (int i = 0; i < 4; i++)
            {
                float a = Mathf.Deg2Rad * (45f + 90f * i);
                diagonals.Add(new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a)));
            }

            foreach (float radius in k_RingRadii)
            {
                foreach (Vector3 dir in diagonals)
                {
                    var label = new GameObject($"Ring {radius:0} m");
                    label.transform.SetParent(markers, false);
                    // Just inside the ring, flat on the floor, readable from the table.
                    label.transform.localPosition = dir * (radius - 0.04f * radius - 0.15f) + Vector3.up * 0.002f;
                    label.transform.localRotation = Quaternion.LookRotation(Vector3.down, dir);
                    var tmp = label.AddComponent<TextMeshPro>();
                    tmp.text = $"{radius:0} m";
                    tmp.fontSize = 2.5f + radius * 0.3f;
                    tmp.alignment = TextAlignmentOptions.Center;
                    tmp.color = new Color(k_Ring.r, k_Ring.g, k_Ring.b, 0.85f);
                    tmp.textWrappingMode = TextWrappingModes.NoWrap;
                    tmp.rectTransform.sizeDelta = new Vector2(radius, radius * 0.2f + 0.5f);
                    tmp.GetComponent<MeshRenderer>().shadowCastingMode = ShadowCastingMode.Off;
                }
            }

            foreach (float radius in k_PostRadii)
            {
                foreach (Vector3 dir in diagonals)
                {
                    var post = GameObject.CreatePrimitive(PrimitiveType.Cylinder); // 2 m tall, 1 m wide
                    post.name = $"Post {radius:0} m";
                    Object.DestroyImmediate(post.GetComponent<Collider>());     // don't block rays or teleport
                    post.transform.SetParent(markers, false);
                    post.transform.localPosition = dir * radius + Vector3.up * (k_PostHeight * 0.5f);
                    post.transform.localScale = new Vector3(0.06f, k_PostHeight * 0.5f, 0.06f);
                    post.GetComponent<MeshRenderer>().sharedMaterial = postMat;

                    var cap = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    cap.name = "Cap";
                    Object.DestroyImmediate(cap.GetComponent<Collider>());
                    cap.transform.SetParent(markers, false);
                    cap.transform.localPosition = dir * radius + Vector3.up * k_PostHeight;
                    cap.transform.localScale = Vector3.one * 0.12f;
                    cap.GetComponent<MeshRenderer>().sharedMaterial = capMat;
                    cap.GetComponent<MeshRenderer>().shadowCastingMode = ShadowCastingMode.Off;
                    cap.transform.SetParent(post.transform, true);
                }
            }
            return markers;
        }

        static void RewireTeleport(Collider floorCollider)
        {
            foreach (var area in Object.FindObjectsByType<TeleportationArea>(FindObjectsInactive.Include))
            {
                Undo.RecordObject(area, "Teleport floor");
                area.colliders.RemoveAll(c => c == null);
                if (!area.colliders.Contains(floorCollider)) area.colliders.Add(floorCollider);
                EditorUtility.SetDirty(area);
            }
        }

        static void SetupLighting()
        {
            RenderSettings.skybox = Mat("StudioSky", "CUBIA/GradientSky", null);
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.50f, 0.53f, 0.57f);
            RenderSettings.ambientEquatorColor = new Color(0.44f, 0.45f, 0.47f);
            RenderSettings.ambientGroundColor = new Color(0.30f, 0.31f, 0.32f);
            RenderSettings.fog = false; // the floor fades itself; fog would also wash out far data points

            foreach (var light in Object.FindObjectsByType<Light>(FindObjectsInactive.Include))
            {
                if (light.type != LightType.Directional) continue;
                Undo.RecordObject(light, "Studio light");
                Undo.RecordObject(light.transform, "Studio light");
                light.color = new Color(1f, 0.975f, 0.94f);
                light.intensity = 1.0f;
                light.shadows = LightShadows.Soft;
                light.shadowStrength = 0.6f;
                light.transform.rotation = Quaternion.Euler(55f, -35f, 0f);
            }

            // The reflection probe, light probes and baked lighting data all captured the sample room
            // (blue-tinting the table). The studio is lit by ambient + one light, so drop them.
            foreach (var probe in Object.FindObjectsByType<ReflectionProbe>(FindObjectsInactive.Include))
                Undo.DestroyObjectImmediate(probe.gameObject);
            foreach (var group in Object.FindObjectsByType<LightProbeGroup>(FindObjectsInactive.Include))
                Undo.DestroyObjectImmediate(group.gameObject);
            Lightmapping.Clear();
            // Default reflections: a plain studio-coloured cubemap instead of the sample room's sky,
            // which tinted every glossy surface blue.
            RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;
            RenderSettings.customReflectionTexture = StudioReflectionCubemap();
            DynamicGI.UpdateEnvironment();
        }

        static Cubemap StudioReflectionCubemap()
        {
            string path = k_Root + "/StudioReflection.cubemap";
            var cube = AssetDatabase.LoadAssetAtPath<Cubemap>(path);
            if (cube != null) return cube;

            const int size = 16;
            cube = new Cubemap(size, TextureFormat.RGBA32, false);
            var zenith = new Color(0.82f, 0.86f, 0.91f);
            var ground = new Color(0.84f, 0.85f, 0.86f);
            foreach (CubemapFace face in new[] { CubemapFace.PositiveX, CubemapFace.NegativeX, CubemapFace.PositiveZ, CubemapFace.NegativeZ })
            {
                var px = new Color[size * size];
                for (int y = 0; y < size; y++)            // row 0 is the top of the face
                    for (int x = 0; x < size; x++)
                    {
                        float v = 1f - (y + 0.5f) / size;  // 1 at the top, 0 at the bottom
                        px[y * size + x] = v >= 0.5f ? Color.Lerp(k_Horizon, zenith, (v - 0.5f) * 2f) : Color.Lerp(k_Horizon, ground, (0.5f - v) * 2f);
                    }
                cube.SetPixels(px, face);
            }
            cube.SetPixels(Fill(size, zenith), CubemapFace.PositiveY);
            cube.SetPixels(Fill(size, ground), CubemapFace.NegativeY);
            cube.Apply();
            AssetDatabase.CreateAsset(cube, path);
            return cube;
        }

        static Color[] Fill(int size, Color c)
        {
            var px = new Color[size * size];
            for (int i = 0; i < px.Length; i++) px[i] = c;
            return px;
        }

        static void RestyleTable(Transform table)
        {
            Material top = Mat("TableTop", "Universal Render Pipeline/Lit", m =>
            {
                m.SetColor("_BaseColor", new Color(0.84f, 0.845f, 0.85f));
                m.SetFloat("_Smoothness", 0.35f);
            });
            Material frame = Mat("TableFrame", "Universal Render Pipeline/Lit", m =>
            {
                m.SetColor("_BaseColor", new Color(0.18f, 0.19f, 0.21f));
                m.SetFloat("_Metallic", 0.6f);
                m.SetFloat("_Smoothness", 0.55f);
            });

            SetMaterial(table.Find("Table"), top);
            Transform t = table.Find("Table");
            if (t != null)
                foreach (Transform leg in t)
                    if (leg.name.StartsWith("Leg")) SetMaterial(leg, frame);
            SetMaterial(table.Find("UINook"), frame);
        }

        static void SetMaterial(Transform t, Material m)
        {
            if (t == null) return;
            var r = t.GetComponent<MeshRenderer>();
            if (r == null) return;
            Undo.RecordObject(r, "Table material");
            r.sharedMaterial = m;
        }

        /// <summary>Loads or creates a material asset under Assets/Environment/Materials, then applies its settings.</summary>
        static Material Mat(string name, string shaderName, System.Action<Material> configure)
        {
            if (!AssetDatabase.IsValidFolder(k_Root)) AssetDatabase.CreateFolder("Assets", "Environment");
            if (!AssetDatabase.IsValidFolder(k_MatDir)) AssetDatabase.CreateFolder(k_Root, "Materials");

            string path = $"{k_MatDir}/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            Shader shader = Shader.Find(shaderName);
            if (mat == null)
            {
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, path);
            }
            else if (mat.shader != shader)
            {
                mat.shader = shader;
            }
            configure?.Invoke(mat);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static GameObject FindByPath(string path)
        {
            string[] parts = path.Split('/');
            foreach (GameObject root in EditorSceneManager.GetActiveScene().GetRootGameObjects())
            {
                if (root.name != parts[0]) continue;
                Transform t = root.transform;
                for (int i = 1; i < parts.Length && t != null; i++) t = t.Find(parts[i]);
                if (t != null) return t.gameObject;
            }
            return null;
        }
    }
}
