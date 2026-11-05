#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public class VTTDomingaoSetup : EditorWindow
{
    [MenuItem("VTT/1. Limpar e Preparar Cena de Mapa Atual")]
    public static void CleanAndPrepareCurrentMapScene()
    {
        // 1. Remove tokens de teste antigos da cena do mapa
        VTTToken25D[] oldTokens = Object.FindObjectsByType<VTTToken25D>(FindObjectsSortMode.None);
        foreach (var t in oldTokens)
        {
            Undo.DestroyObjectImmediate(t.gameObject);
        }

        // 2. Remove VTTNetworkManager e VTTMapManager soltos no mapa (pois virão da CenaMenu via PersistentCore)
        VTTNetworkManager[] netManagers = Object.FindObjectsByType<VTTNetworkManager>(FindObjectsSortMode.None);
        foreach (var nm in netManagers)
        {
            if (nm.gameObject.name == "VTT_PersistentCore")
                Undo.DestroyObjectImmediate(nm.gameObject);
            else
                Undo.DestroyObjectImmediate(nm);
        }

        VTTMapManager[] mapManagers = Object.FindObjectsByType<VTTMapManager>(FindObjectsSortMode.None);
        foreach (var mm in mapManagers)
        {
            Undo.DestroyObjectImmediate(mm);
        }

        // 3. Garante Main Camera + VTTCameraController + Cursor 3D
        Camera mainCam = Camera.main;
        if (mainCam == null)
        {
            GameObject camObj = new GameObject("Main Camera");
            camObj.tag = "MainCamera";
            mainCam = camObj.AddComponent<Camera>();
        }

        VTTCameraController camCtrl = mainCam.GetComponent<VTTCameraController>();
        if (camCtrl == null)
            camCtrl = mainCam.gameObject.AddComponent<VTTCameraController>();

        GameObject cursor = GameObject.Find("VTT_Cursor3D");
        if (cursor == null)
        {
            cursor = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            cursor.name = "VTT_Cursor3D";
            if (cursor.GetComponent<Collider>() != null)
                DestroyImmediate(cursor.GetComponent<Collider>());
            cursor.transform.localScale = new Vector3(1f, 0.03f, 1f);

            Material cursorMat = new Material(Shader.Find("Sprites/Default"));
            cursorMat.color = new Color(0f, 0.9f, 1f, 0.65f);
            cursor.GetComponent<MeshRenderer>().sharedMaterial = cursorMat;
        }
        camCtrl.cursor3D = cursor.transform;

        // 4. Aplica MeshCollider no mapa (ignorando SemColisao e Telhado)
        int collidersAdded = 0;
        MeshRenderer[] allRenderers = Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None);
        foreach (MeshRenderer mr in allRenderers)
        {
            if (mr.gameObject == cursor) continue;
            if (mr.gameObject.name.Contains("Telhado") ||
                VTTTagSystem.HasTagInHierarchy(mr.transform, VTTTagSystem.TAG_TELHADO) ||
                VTTTagSystem.HasTagInHierarchy(mr.transform, VTTTagSystem.TAG_SEM_COLISAO))
                continue;

            if (mr.GetComponent<Collider>() == null && mr.GetComponent<MeshFilter>() != null)
            {
                mr.gameObject.AddComponent<MeshCollider>();
                collidersAdded++;
            }
        }

        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        EditorSceneManager.SaveOpenScenes();

        Debug.Log($"[VTT] Mapa '{SceneManager.GetActiveScene().name}' limpo e salvo! ({collidersAdded} novos colisores aplicados). Agora clique em 'VTT > 2. Construir Cena de Menu Inicial'.");
    }

    [MenuItem("VTT/2. Construir Cena de Menu Inicial (CenaMenu) e Integrar Tudo")]
    public static void BuildInitialMenuSceneAndIntegrate()
    {
        // Salva a cena aberta antes de criar o Menu
        EditorSceneManager.SaveOpenScenes();

        if (!Directory.Exists("Assets/Scenes"))
            Directory.CreateDirectory("Assets/Scenes");

        string menuScenePath = "Assets/Scenes/CenaMenu.unity";

        // 1. Procura todas as cenas de mapa existentes no projeto para integrar automaticamente
        string[] sceneGuids = AssetDatabase.FindAssets("t:Scene");
        List<string> mapScenePaths = new List<string>();
        List<string> mapSceneNames = new List<string>();

        foreach (string guid in sceneGuids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (!path.StartsWith("Assets/")) continue;
            string sName = Path.GetFileNameWithoutExtension(path);
            if (sName == "CenaMenu") continue;

            mapScenePaths.Add(path);
            mapSceneNames.Add(sName);
        }

        // 2. Cria uma nova Cena limpa para o Menu Inicial
        Scene menuScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        // Cria a Câmera de Fundo do Menu Inicial
        GameObject camObj = new GameObject("Menu_Camera");
        camObj.tag = "MainCamera";
        Camera cam = camObj.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.06f, 0.07f, 0.10f, 1f);

        // 3. Cria o VTT_PersistentCore que sobrevive entre todas as cenas
        GameObject coreObj = new GameObject("VTT_PersistentCore");
        VTTNetworkManager netMgr = coreObj.AddComponent<VTTNetworkManager>();
        VTTMapManager mapMgr = coreObj.AddComponent<VTTMapManager>();

        netMgr.openInitialMenuOnStart = true;
        netMgr.isMasterUnlocked = false;

        // Preenche automaticamente a lista de Mapas no VTTMapManager com as cenas do projeto
        mapMgr.maps.Clear();
        for (int i = 0; i < mapSceneNames.Count; i++)
        {
            mapMgr.maps.Add(new VTTMapEntry
            {
                mapName = mapSceneNames[i],
                sceneNameToLoad = mapSceneNames[i]
            });
        }

        // 4. Salva a CenaMenu
        EditorSceneManager.SaveScene(menuScene, menuScenePath);

        // 5. Registra a CenaMenu (Índice 0) + todas as Cenas de Mapa no Build Settings da Unity
        List<EditorBuildSettingsScene> buildScenes = new List<EditorBuildSettingsScene>
        {
            new EditorBuildSettingsScene(menuScenePath, true)
        };

        foreach (string mapPath in mapScenePaths)
        {
            buildScenes.Add(new EditorBuildSettingsScene(mapPath, true));
        }

        EditorBuildSettings.scenes = buildScenes.ToArray();

        Debug.Log($"[VTT Menu Criado!] 'CenaMenu' configurada como Cena Inicial (Índice 0) e integrada com {mapSceneNames.Count} mapa(s): {string.Join(", ", mapSceneNames)}. Aperte PLAY para testar!");
    }
}
#endif