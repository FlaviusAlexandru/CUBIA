using System.Linq;
using DataViz.Flow;
using DataViz.Networking;
using DataViz.Networking.EditorTools;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DataViz.Lobby.EditorTools
{
    /// <summary>
    /// Tools > CUBIA > Scenes > Build Lobby Scene: (re)creates Assets/Scenes/Lobby.unity, a calm room
    /// for the multiplayer lobby. The XR rig, event system, hand permissions, light and studio floor
    /// are copied from the data scene (opened additively, read only, if it isn't open), the sky and
    /// ambient light from its render settings. Adds a table with TableFrame + TableAlignTool (same
    /// size as the data scene's, so a co-located alignment carries over) and the LobbyPanel.
    ///
    /// Idempotent: the lobby scene is generated from scratch every time, in an additive scene that is
    /// saved and closed again; the data scene is never modified or saved.
    /// </summary>
    static class LobbySceneBuilder
    {
        const string k_LobbyPath = "Assets/Scenes/Lobby.unity";
        const string k_DataPath = "Assets/Scenes/SampleScene.unity";

        static readonly Vector3 k_TablePosition = new(0f, 0.68f, 1.05f);
        static readonly Vector3 k_PanelPosition = new(0f, 1.35f, 1.8f);
        const float k_PanelLeanBack = 8f; // degrees

        [MenuItem("Tools/CUBIA/Scenes/Build Lobby Scene")]
        static void Build()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogError("[LobbySceneBuilder] Exit Play mode first.");
                return;
            }

            Scene data = SceneManager.GetSceneByPath(k_DataPath);
            bool openedData = !data.isLoaded;
            if (openedData)
                data = EditorSceneManager.OpenScene(k_DataPath, OpenSceneMode.Additive);
            bool dataWasDirty = data.isDirty;
            Scene previousActive = SceneManager.GetActiveScene();

            Scene oldLobby = SceneManager.GetSceneByPath(k_LobbyPath);
            Scene lobby = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            if (oldLobby.isLoaded)
                EditorSceneManager.CloseScene(oldLobby, true); // generated content; rebuilt below

            try
            {
                Populate(data, lobby);
                CopyRenderSettings(lobby);
                EditorSceneManager.SaveScene(lobby, k_LobbyPath);
                Debug.Log($"[LobbySceneBuilder] Built {k_LobbyPath}. Add it to Build Settings (scene name \"{SceneNames.Lobby}\").");
            }
            finally
            {
                if (previousActive.IsValid() && previousActive.isLoaded) SceneManager.SetActiveScene(previousActive);
                EditorSceneManager.CloseScene(lobby, true);
                if (openedData) EditorSceneManager.CloseScene(data, true);
                else if (data.isDirty && !dataWasDirty)
                    Debug.LogWarning("[LobbySceneBuilder] The data scene became dirty while copying from it; don't save it.");
            }
        }

        static void Populate(Scene data, Scene lobby)
        {
            CopyRoot(data, lobby, "XR Origin Hands (XR Rig)", Vector3.zero, Quaternion.identity);
            CopyRoot(data, lobby, "EventSystem");
            CopyRoot(data, lobby, "Hands Permissions Manager");

            var lighting = NewRoot(lobby, "Lighting");
            CopyChild(data, "Lighting/Directional Light", lighting.transform);
            var environment = NewRoot(lobby, "Environment");
            CopyChild(data, "Environment/Studio/Floor", environment.transform);

            BuildTable(data, lobby);
            BuildPanel(lobby);
        }

        static void BuildTable(Scene data, Scene lobby)
        {
            GameObject sourceRoot = FindInScene(data, "DataVisualizationTable");
            var table = NewRoot(lobby, "LobbyTable");
            table.transform.SetPositionAndRotation(k_TablePosition, Quaternion.identity);

            GameObject top = CopyChild(data, "DataVisualizationTable/Table", table.transform);
            var frame = table.AddComponent<TableFrame>();
            var frameSo = new SerializedObject(frame);
            frameSo.FindProperty("m_TableTop").objectReferenceValue = top != null ? top.GetComponent<BoxCollider>() : null;
            frameSo.ApplyModifiedPropertiesWithoutUndo();

            var align = table.AddComponent<TableAlignTool>();
            TableAlignTool sourceAlign = sourceRoot != null ? sourceRoot.GetComponent<TableAlignTool>() : null;
            if (sourceAlign != null)
            {
                var alignSo = new SerializedObject(align);
                alignSo.FindProperty("m_MarkerMaterial").objectReferenceValue =
                    new SerializedObject(sourceAlign).FindProperty("m_MarkerMaterial").objectReferenceValue;
                alignSo.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        static void BuildPanel(Scene lobby)
        {
            var panel = NewRoot(lobby, "LobbyPanel");
            panel.transform.SetPositionAndRotation(k_PanelPosition, Quaternion.Euler(k_PanelLeanBack, 0f, 0f));
            var component = panel.AddComponent<LobbyPanel>();
            var so = new SerializedObject(component);
            SerializedProperty icons = so.FindProperty("m_Icons");
            UiIcons loaded = UiIconsLoader.Load();
            foreach (var field in typeof(UiIcons).GetFields())
                icons.FindPropertyRelative(field.Name).objectReferenceValue = (Object)field.GetValue(loaded);
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static void CopyRenderSettings(Scene lobby)
        {
            // RenderSettings belong to the active scene: read them from the data scene, write them into the lobby.
            Scene data = SceneManager.GetSceneByPath(k_DataPath);
            SceneManager.SetActiveScene(data);
            Material skybox = RenderSettings.skybox;
            var ambientMode = RenderSettings.ambientMode;
            Color sky = RenderSettings.ambientSkyColor, equator = RenderSettings.ambientEquatorColor, ground = RenderSettings.ambientGroundColor;
            float ambientIntensity = RenderSettings.ambientIntensity;
            var reflectionMode = RenderSettings.defaultReflectionMode;
            Texture reflection = RenderSettings.customReflectionTexture;
            float reflectionIntensity = RenderSettings.reflectionIntensity;
            bool fog = RenderSettings.fog;

            SceneManager.SetActiveScene(lobby);
            RenderSettings.skybox = skybox;
            RenderSettings.ambientMode = ambientMode;
            RenderSettings.ambientSkyColor = sky;
            RenderSettings.ambientEquatorColor = equator;
            RenderSettings.ambientGroundColor = ground;
            RenderSettings.ambientIntensity = ambientIntensity;
            RenderSettings.defaultReflectionMode = reflectionMode;
            RenderSettings.customReflectionTexture = reflection;
            RenderSettings.reflectionIntensity = reflectionIntensity;
            RenderSettings.fog = fog;
        }

        // ------------------------------------------------------------------ helpers

        static GameObject NewRoot(Scene scene, string name)
        {
            var go = new GameObject(name);
            SceneManager.MoveGameObjectToScene(go, scene);
            return go;
        }

        /// <summary>Clones a data-scene root object into the lobby (a plain copy, keeping its overrides).</summary>
        static void CopyRoot(Scene data, Scene lobby, string name, Vector3? position = null, Quaternion? rotation = null)
        {
            GameObject source = FindInScene(data, name);
            if (source == null)
            {
                Debug.LogWarning($"[LobbySceneBuilder] '{name}' not found in the data scene; skipped.");
                return;
            }
            GameObject copy = Object.Instantiate(source);
            copy.name = source.name;
            SceneManager.MoveGameObjectToScene(copy, lobby);
            if (position.HasValue) copy.transform.SetPositionAndRotation(position.Value, rotation ?? Quaternion.identity);
        }

        /// <summary>Clones a data-scene object (by "Root/Child/..." path) under parent, keeping its local pose.</summary>
        static GameObject CopyChild(Scene data, string path, Transform parent)
        {
            string[] parts = path.Split('/');
            GameObject root = FindInScene(data, parts[0]);
            Transform source = root != null ? root.transform.Find(string.Join("/", parts.Skip(1))) : null;
            if (source == null)
            {
                Debug.LogWarning($"[LobbySceneBuilder] '{path}' not found in the data scene; skipped.");
                return null;
            }
            GameObject copy = Object.Instantiate(source.gameObject, parent, false);
            copy.name = source.name;
            copy.transform.SetLocalPositionAndRotation(source.localPosition, source.localRotation);
            copy.transform.localScale = source.localScale;
            return copy;
        }

        static GameObject FindInScene(Scene scene, string rootName) =>
            scene.GetRootGameObjects().FirstOrDefault(r => r.name == rootName);
    }
}
