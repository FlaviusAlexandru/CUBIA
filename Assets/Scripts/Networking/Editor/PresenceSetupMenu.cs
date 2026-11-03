using System.Collections.Generic;
using TMPro;
using Unity.Netcode;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Transformers;

namespace DataViz.Networking
{
    /// <summary>
    /// Tools/CUBIA/Multiplayer/2 Setup Presence — idempotent. Builds the player avatar prefab and its
    /// materials under Assets/Prefabs/Networking, registers it as NetworkManager.PlayerPrefab
    /// (needs "1 Setup Network" first), wires TableFrame + Place-table grab + TableAlignTool on
    /// DataVisualizationTable, and adds the "SharedPlotSync" in-scene NetworkObject that shares the
    /// plot pose. Marks the scene dirty; does not save it.
    /// </summary>
    static class PresenceSetupMenu
    {
        const string k_Folder = "Assets/Prefabs/Networking";
        const string k_PrefabPath = k_Folder + "/NetworkPlayerAvatar.prefab";
        const string k_LitPath = k_Folder + "/AvatarLit.mat";
        const string k_UnlitPath = k_Folder + "/AvatarUnlit.mat";
        const string k_VisorPath = k_Folder + "/AvatarVisor.mat";
        const string k_TableName = "DataVisualizationTable";
        const string k_PlotSyncName = "SharedPlotSync";
        const string k_Log = "[CUBIA Presence Setup] ";

        [MenuItem("Tools/CUBIA/Multiplayer/2 Setup Presence")]
        static void Setup()
        {
            GameObject prefab = BuildPlayerPrefab();
            Material unlit = AssetDatabase.LoadAssetAtPath<Material>(k_UnlitPath);

            bool ok = SetupTable(unlit);
            ok &= SetupPlotSync();
            ok &= RegisterPlayerPrefab(prefab);

            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            if (ok) Debug.Log(k_Log + "Done. Save the scene to keep the changes.");
            else Debug.LogError(k_Log + "Finished with errors (see above). Fix them and run the menu again.");
        }

        // ---------- assets ----------

        /// <summary>Creates or overwrites the player prefab (overwriting keeps its GUID and references).</summary>
        internal static GameObject BuildPlayerPrefab()
        {
            EnsureFolder();
            Material lit = EnsureMaterial(k_LitPath, "Universal Render Pipeline/Lit", Color.white);
            Material unlit = EnsureMaterial(k_UnlitPath, "Universal Render Pipeline/Unlit", Color.white);
            Material visor = EnsureMaterial(k_VisorPath, "Universal Render Pipeline/Lit", new Color(0.08f, 0.08f, 0.1f));

            var root = new GameObject("NetworkPlayerAvatar");
            try
            {
                var networkObject = root.AddComponent<NetworkObject>();
                networkObject.SynchronizeTransform = false; // the root never moves; poses travel in NetworkVariables
                var avatar = root.AddComponent<NetworkPlayerAvatar>();
                var pin = root.AddComponent<PinnedTooltipSync>();

                Transform visuals = new GameObject("Visuals").transform;
                visuals.SetParent(root.transform, false);

                Transform head = Primitive(PrimitiveType.Sphere, "Head", visuals, new Vector3(0.17f, 0.21f, 0.19f), lit);
                // Dark visor on the face so others can tell where this player is looking.
                Transform visorT = Primitive(PrimitiveType.Cube, "Visor", head, new Vector3(0.13f / 0.17f, 0.045f / 0.21f, 0.03f / 0.19f), visor);
                visorT.localPosition = new Vector3(0f, 0.1f, 0.42f);

                Transform leftHand = Primitive(PrimitiveType.Sphere, "LeftHand", visuals, new Vector3(0.07f, 0.035f, 0.1f), lit);
                Transform rightHand = Primitive(PrimitiveType.Sphere, "RightHand", visuals, new Vector3(0.07f, 0.035f, 0.1f), lit);
                LineRenderer leftRay = Ray("LeftRay", visuals, unlit);
                LineRenderer rightRay = Ray("RightRay", visuals, unlit);

                // Nameplate: label + speaker icon.
                var plate = new GameObject("Nameplate");
                plate.transform.SetParent(visuals, false);
                var nameplate = plate.AddComponent<AvatarNameplate>();
                TextMeshPro label = Label("Label", plate.transform, 0.4f);
                var icon = new GameObject("SpeakerIcon");
                icon.transform.SetParent(plate.transform, false);
                icon.transform.localScale = Vector3.one * 0.04f;
                var iconRenderer = icon.AddComponent<SpriteRenderer>();

                // Pinned-point marker (shown to others on the point this player pinned).
                var marker = new GameObject("PinMarker");
                marker.transform.SetParent(visuals, false);
                Transform dot = Primitive(PrimitiveType.Sphere, "Dot", marker.transform, Vector3.one * 0.025f, unlit);
                TextMeshPro pinLabel = Label("Label", marker.transform, 0.3f);
                marker.SetActive(false);

                Set(avatar, "m_Visuals", visuals.gameObject);
                Set(avatar, "m_Head", head);
                Set(avatar, "m_LeftHand", leftHand);
                Set(avatar, "m_RightHand", rightHand);
                Set(avatar, "m_LeftRay", leftRay);
                Set(avatar, "m_RightRay", rightRay);
                SetArray(avatar, "m_TintedRenderers", new Object[]
                {
                    head.GetComponent<Renderer>(), leftHand.GetComponent<Renderer>(), rightHand.GetComponent<Renderer>(),
                    leftRay, rightRay, dot.GetComponent<Renderer>(),
                });
                Set(nameplate, "m_Avatar", avatar);
                Set(nameplate, "m_Label", label);
                Set(nameplate, "m_SpeakerIcon", iconRenderer);
                Set(pin, "m_Avatar", avatar);
                Set(pin, "m_Marker", marker.transform);
                Set(pin, "m_Label", pinLabel);

                GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, k_PrefabPath, out bool success);
                if (!success) Debug.LogError(k_Log + "Could not save " + k_PrefabPath);
                return saved;
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        static void EnsureFolder()
        {
            if (!AssetDatabase.IsValidFolder("Assets/Prefabs")) AssetDatabase.CreateFolder("Assets", "Prefabs");
            if (!AssetDatabase.IsValidFolder(k_Folder)) AssetDatabase.CreateFolder("Assets/Prefabs", "Networking");
        }

        static Material EnsureMaterial(string path, string shaderName, Color colour)
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material != null) return material;

            Shader shader = Shader.Find(shaderName);
            if (shader == null)
            {
                Debug.LogWarning(k_Log + $"Shader '{shaderName}' not found; using the default material shader.");
                shader = Shader.Find("Standard");
            }
            material = new Material(shader);
            material.SetColor("_BaseColor", colour);
            AssetDatabase.CreateAsset(material, path);
            return material;
        }

        static Transform Primitive(PrimitiveType type, string name, Transform parent, Vector3 scale, Material material)
        {
            GameObject go = GameObject.CreatePrimitive(type);
            go.name = name;
            Object.DestroyImmediate(go.GetComponent<Collider>()); // avatars must never block interactor casts
            go.transform.SetParent(parent, false);
            go.transform.localScale = scale;
            var renderer = go.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            return go.transform;
        }

        static LineRenderer Ray(string name, Transform parent, Material material)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var line = go.AddComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.positionCount = 2;
            line.widthMultiplier = 0.004f;
            line.sharedMaterial = material;
            line.shadowCastingMode = ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.enabled = false;
            return line;
        }

        static TextMeshPro Label(string name, Transform parent, float fontSize)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var text = go.AddComponent<TextMeshPro>();
            text.font = TMP_Settings.defaultFontAsset;
            text.fontSize = fontSize; // 1 pt ≈ 0.11 m with LiberationSans SDF
            text.alignment = TextAlignmentOptions.Center;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.rectTransform.sizeDelta = new Vector2(1f, 0.1f);
            text.text = "";
            return text;
        }

        static void Set(Object target, string property, Object value)
        {
            var so = new SerializedObject(target);
            so.FindProperty(property).objectReferenceValue = value;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static void SetArray(Object target, string property, Object[] values)
        {
            var so = new SerializedObject(target);
            SerializedProperty array = so.FindProperty(property);
            array.arraySize = values.Length;
            for (int i = 0; i < values.Length; i++) array.GetArrayElementAtIndex(i).objectReferenceValue = values[i];
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        // ---------- scene wiring ----------

        static bool SetupTable(Material markerMaterial)
        {
            GameObject table = FindRoot(k_TableName);
            if (table == null)
            {
                Debug.LogError(k_Log + $"'{k_TableName}' not found at the scene root.");
                return false;
            }

            BoxCollider top = table.transform.Find("Table")?.GetComponent<BoxCollider>();
            if (top == null)
            {
                Debug.LogError(k_Log + $"'{k_TableName}/Table' has no BoxCollider; Place table and Align need it.");
                return false;
            }

            var frame = GetOrAdd<TableFrame>(table);

            // Place-table grab: kinematic, level (yaw only), no scaling, disabled until placing.
            var body = GetOrAdd<Rigidbody>(table);
            Undo.RecordObject(body, "Setup Presence");
            body.isKinematic = true;
            body.useGravity = false;

            var grab = GetOrAdd<XRGrabInteractable>(table);
            var general = GetOrAdd<XRGeneralGrabTransformer>(table);
            var yaw = GetOrAdd<TableYawGrabTransformer>(table);

            Undo.RecordObject(general, "Setup Presence");
            general.allowOneHandedScaling = false;
            general.allowTwoHandedScaling = false;

            Undo.RecordObject(grab, "Setup Presence");
            grab.enabled = false;
            grab.colliders.Clear();
            grab.colliders.Add(top); // only the table top, never the plot's or UI colliders below the root
            grab.movementType = XRBaseInteractable.MovementType.Instantaneous;
            grab.trackPosition = true;
            grab.trackRotation = true;
            grab.trackScale = false;
            grab.throwOnDetach = false;
            grab.useDynamicAttach = true; // hold the table where it was touched instead of snapping to the hand
            grab.retainTransformParent = true;
            grab.addDefaultGrabTransformers = false;
            grab.startingSingleGrabTransformers = new List<XRBaseGrabTransformer> { general, yaw };
            grab.startingMultipleGrabTransformers = new List<XRBaseGrabTransformer> { general, yaw };
            EditorUtility.SetDirty(grab);

            Set(frame, "m_PlaceGrab", grab);
            Set(frame, "m_TableTop", top);

            var align = GetOrAdd<TableAlignTool>(table);
            Set(align, "m_MarkerMaterial", markerMaterial);
            return true;
        }

        static bool SetupPlotSync()
        {
            var plot = Object.FindAnyObjectByType<CoordinateSystemInteractable>(FindObjectsInactive.Include);
            if (plot == null)
            {
                Debug.LogError(k_Log + "No CoordinateSystemInteractable (plot) in the scene.");
                return false;
            }

            // A separate scene-root NetworkObject: the plot itself un-parents/re-docks under the
            // (non-networked) table, which NGO parent sync would reject.
            GameObject sync = FindRoot(k_PlotSyncName);
            if (sync == null)
            {
                sync = new GameObject(k_PlotSyncName);
                SceneManager.MoveGameObjectToScene(sync, plot.gameObject.scene);
                Undo.RegisterCreatedObjectUndo(sync, "Setup Presence");
            }

            var networkObject = GetOrAdd<NetworkObject>(sync);
            Undo.RecordObject(networkObject, "Setup Presence");
            networkObject.SynchronizeTransform = false;
            networkObject.AutoObjectParentSync = false;

            var plotSync = GetOrAdd<NetworkPlotTransform>(sync);
            Set(plotSync, "m_Plot", plot);
            return true;
        }

        static bool RegisterPlayerPrefab(GameObject prefab)
        {
            if (prefab == null) return false;

            var manager = Object.FindAnyObjectByType<NetworkManager>(FindObjectsInactive.Include);
            if (manager == null)
            {
                Debug.LogError(k_Log + "No NetworkManager in the scene. Run 'Tools/CUBIA/Multiplayer/1 Setup Network' first, then run this menu again.");
                return false;
            }

            Undo.RecordObject(manager, "Setup Presence");
            manager.NetworkConfig.PlayerPrefab = prefab;
            EditorUtility.SetDirty(manager);
            return true;
        }

        static GameObject FindRoot(string name)
        {
            foreach (GameObject go in SceneManager.GetActiveScene().GetRootGameObjects())
                if (go.name == name) return go;
            return null;
        }

        static T GetOrAdd<T>(GameObject go) where T : Component
        {
            return go.TryGetComponent(out T existing) ? existing : Undo.AddComponent<T>(go);
        }
    }
}
