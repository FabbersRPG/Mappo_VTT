using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

[System.Serializable]
public class VTTMapEntry
{
    public string mapName = "Novo Mapa";
    public string sceneNameToLoad = "";
    public GameObject mapPrefab;
    public GameObject mapRootObject;
    public Transform spawnPoint;
}

public class VTTMapManager : MonoBehaviour
{
    public static VTTMapManager Instance { get; private set; }

    [Header("Lista de Mapas do Mappo_VTT (Tecla M no Jogo)")]
    public List<VTTMapEntry> maps = new List<VTTMapEntry>();
    public int currentMapIndex = 0;
    public bool showMapMenu = false;

    private GameObject currentInstantiatedPrefab;
    private VTTCameraController camController;
    private VTTFogOfWar fogOfWar;
    private Texture2D overlayTex;

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    void OnEnable()
    {
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    void OnDisable()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    void Start()
    {
        overlayTex = new Texture2D(1, 1);
        overlayTex.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.76f));
        overlayTex.Apply();
        CleanInvalidMenuEntries();
        RefreshReferences();
    }

    // Remove CenaMenu, SampleScene e cenas que não estão no Build Profiles
    public void CleanInvalidMenuEntries()
    {
        maps.RemoveAll(m =>
            m.sceneNameToLoad == "CenaMenu" ||
            m.mapName == "CenaMenu" ||
            m.sceneNameToLoad == "SampleScene" ||
            m.mapName == "SampleScene");

        // Se a lista ficou vazia, descobre automaticamente as cenas válidas registradas no Build Profiles
        if (maps.Count == 0)
        {
            int sceneCount = SceneManager.sceneCountInBuildSettings;
            for (int i = 0; i < sceneCount; i++)
            {
                string path = SceneUtility.GetScenePathByBuildIndex(i);
                string sName = System.IO.Path.GetFileNameWithoutExtension(path);
                if (sName != "CenaMenu" && sName != "SampleScene" && !string.IsNullOrEmpty(sName))
                {
                    maps.Add(new VTTMapEntry
                    {
                        mapName = sName,
                        sceneNameToLoad = sName
                    });
                }
            }
        }
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (scene.name == "CenaMenu") return;

        EnsureMapSceneComponents();
        RefreshReferences();

        if (fogOfWar != null)
            fogOfWar.EnsureMapColliders();

        if (VTTNetworkManager.Instance != null)
        {
            for (int i = 0; i < VTTNetworkManager.Instance.profiles.Count; i++)
            {
                var prof = VTTNetworkManager.Instance.profiles[i];
                if (!prof.isSpawnedInScene && i != VTTNetworkManager.Instance.activeLocalProfileIndex)
                    continue;

                Vector3 pos = prof.lastKnownPosition != Vector3.zero
                    ? prof.lastKnownPosition
                    : new Vector3((i % 3) * 1.5f, 0.02f, (i / 3) * 1.5f);

                VTTNetworkManager.Instance.SpawnOrMoveProfileToken(prof, pos, false);
            }

            if (!VTTNetworkManager.Instance.isMasterUnlocked && VTTNetworkManager.Instance.activeLocalProfileIndex >= 0)
            {
                int idx = VTTNetworkManager.Instance.activeLocalProfileIndex;
                var myProf = VTTNetworkManager.Instance.profiles[idx];
                if (camController != null && myProf.spawnedToken != null)
                {
                    camController.myPlayerToken = myProf.spawnedToken.transform;
                    camController.viewedVisionToken = myProf.spawnedToken.transform;
                    camController.SetPlayerVisionMode(true);
                }
            }

            if (VTTNetworkManager.Instance.isServerRunning)
                VTTNetworkManager.Instance.BroadcastFullRoomStateFromMaster();
        }

        if (camController != null)
            camController.FocusOnActiveToken();
    }

    public void EnsureMapSceneComponents()
    {
        Camera mainCam = Camera.main;
        if (mainCam == null)
        {
            GameObject camObj = new GameObject("Main Camera");
            camObj.tag = "MainCamera";
            mainCam = camObj.AddComponent<Camera>();
        }

        camController = mainCam.GetComponent<VTTCameraController>();
        if (camController == null)
            camController = mainCam.gameObject.AddComponent<VTTCameraController>();

        GameObject cursor = GameObject.Find("VTT_Cursor3D");
        if (cursor == null)
        {
            cursor = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            cursor.name = "VTT_Cursor3D";
            if (cursor.GetComponent<Collider>() != null)
                Destroy(cursor.GetComponent<Collider>());
            cursor.transform.localScale = new Vector3(1f, 0.03f, 1f);

            Material cMat = new Material(Shader.Find("Sprites/Default"));
            cMat.color = new Color(0f, 0.9f, 1f, 0.65f);
            cursor.GetComponent<MeshRenderer>().sharedMaterial = cMat;
        }
        camController.cursor3D = cursor.transform;

        fogOfWar = FindFirstObjectByType<VTTFogOfWar>();
        if (fogOfWar == null)
        {
            GameObject fogObj = new GameObject("VTT_FogOfWar_System");
            fogOfWar = fogObj.AddComponent<VTTFogOfWar>();
        }
    }

    public void RefreshReferences()
    {
        camController = FindFirstObjectByType<VTTCameraController>();
        fogOfWar = FindFirstObjectByType<VTTFogOfWar>();
    }

    void Update()
    {
        bool isMaster = VTTNetworkManager.Instance == null || VTTNetworkManager.Instance.isMasterUnlocked;
        if (isMaster && Input.GetKeyDown(KeyCode.M))
        {
            if (VTTNetworkManager.Instance != null && VTTNetworkManager.Instance.IsMenuOpen()) return;
            showMapMenu = !showMapMenu;
            if (showMapMenu)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
        }
    }

    public void SwitchToMap(int index, bool syncWithNetwork = true)
    {
        CleanInvalidMenuEntries();
        if (index < 0 || index >= maps.Count) return;

        currentMapIndex = index;
        VTTMapEntry selected = maps[index];

        if (!string.IsNullOrEmpty(selected.sceneNameToLoad))
        {
            showMapMenu = false;

            if (!Application.CanStreamedLevelBeLoaded(selected.sceneNameToLoad))
            {
                Debug.LogError($"[Mappo_VTT] A cena '{selected.sceneNameToLoad}' não está no Build Profiles! Vá em File > Build Profiles e adicione a cena na Scene List.");
                return;
            }

            if (SceneManager.GetActiveScene().name != selected.sceneNameToLoad)
                SceneManager.LoadScene(selected.sceneNameToLoad);

            if (syncWithNetwork && VTTNetworkManager.Instance != null)
                VTTNetworkManager.Instance.BroadcastMapChange(index);
            return;
        }

        RefreshReferences();

        if (currentInstantiatedPrefab != null)
        {
            Destroy(currentInstantiatedPrefab);
            currentInstantiatedPrefab = null;
        }

        for (int i = 0; i < maps.Count; i++)
        {
            if (maps[i].mapRootObject != null)
                maps[i].mapRootObject.SetActive(false);
        }

        Transform activeRoot = null;
        if (selected.mapPrefab != null)
        {
            currentInstantiatedPrefab = Instantiate(selected.mapPrefab, Vector3.zero, Quaternion.identity);
            currentInstantiatedPrefab.name = selected.mapPrefab.name;
            activeRoot = currentInstantiatedPrefab.transform;
        }
        else if (selected.mapRootObject != null)
        {
            selected.mapRootObject.SetActive(true);
            activeRoot = selected.mapRootObject.transform;
        }

        if (fogOfWar != null)
            fogOfWar.EnsureMapColliders();

        Vector3 baseSpawn = selected.spawnPoint != null
            ? selected.spawnPoint.position
            : (activeRoot != null ? activeRoot.position : Vector3.zero);

        VTTToken25D[] allTokens = FindObjectsByType<VTTToken25D>(FindObjectsSortMode.None);
        for (int t = 0; t < allTokens.Length; t++)
        {
            Vector3 offset = new Vector3((t % 3) * 1.5f, 0.02f, (t / 3) * 1.5f);
            allTokens[t].transform.position = baseSpawn + offset;
            if (VTTNetworkManager.Instance != null)
                VTTNetworkManager.Instance.NotifyTokenMoved(allTokens[t], true);
        }

        if (camController != null)
            camController.FocusOnActiveToken();

        if (syncWithNetwork && VTTNetworkManager.Instance != null)
            VTTNetworkManager.Instance.BroadcastMapChange(index);

        showMapMenu = false;
    }

    void OnGUI()
    {
        if (!showMapMenu) return;

        Matrix4x4 oldMatrix = GUI.matrix;
        float scaleX = Screen.width / 1920f;
        float scaleY = Screen.height / 1080f;
        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scaleX, scaleY, 1f));

        if (overlayTex != null)
            GUI.DrawTexture(new Rect(0, 0, 1920f, 1080f), overlayTex);

        float width = 560f;
        int count = Mathf.Max(1, maps.Count);
        float height = 160f + (count * 52f);
        Rect winRect = new Rect((1920f - width) * 0.5f, (1080f - height) * 0.5f, width, height);

        GUI.Box(winRect, "MAPPO_VTT — ALTERNAR MAPA DA SESSÃO (JOGO PAUSADO)");

        if (maps.Count == 0)
        {
            GUI.Label(new Rect(winRect.x + 25f, winRect.y + 50f, width - 50f, 50f),
                "Nenhum mapa encontrado no Build Profiles! Vá em File > Build Profiles e adicione suas cenas.");
        }
        else
        {
            for (int i = 0; i < maps.Count; i++)
            {
                string activeTag = (i == currentMapIndex) ? "  [MAPA ATUAL]" : "";
                GUI.backgroundColor = (i == currentMapIndex) ? Color.green : Color.white;
                if (GUI.Button(new Rect(winRect.x + 30f, winRect.y + 50f + (i * 50f), width - 60f, 42f), $"{maps[i].mapName}{activeTag}"))
                {
                    SwitchToMap(i, true);
                }
            }
            GUI.backgroundColor = Color.white;
        }

        if (GUI.Button(new Rect(winRect.x + 150f, winRect.y + height - 55f, width - 300f, 40f), "Voltar ao Jogo [M]"))
        {
            showMapMenu = false;
        }

        GUI.matrix = oldMatrix;
    }
}