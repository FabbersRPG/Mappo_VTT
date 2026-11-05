using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

[Serializable]
public class VTTPlayerProfile
{
    public string profileId = Guid.NewGuid().ToString("N").Substring(0, 6);
    public string playerName = "Novo Investigador";
    public Color tokenColor = Color.cyan;
    public float visionRadius = 22f;
    public float tokenHeight = 1.75f;
    public string imageFilePath = "";
    public bool isSpawnedInScene = true;
    public Vector3 lastKnownPosition = new Vector3(0f, 0.02f, -4f);

    [NonSerialized] public Texture2D portraitTexture;
    [NonSerialized] public string cachedPngBase64 = "";
    [NonSerialized] public VTTToken25D spawnedToken;
}

[Serializable]
public class VTTSaveData
{
    public string savedMasterPassword = "mestre";
    public List<VTTPlayerProfile> savedProfiles = new List<VTTPlayerProfile>();
    public List<string> excludedMapNames = new List<string>();
}

public class VTTNetworkManager : MonoBehaviour
{
    public static VTTNetworkManager Instance { get; private set; }

    private enum MenuScreen
    {
        Closed,
        InitialRoleSelect,
        MasterPasswordCheck,
        MasterLobbySetup,
        MasterProfileManager,
        FileBrowserFallback,
        PlayerEnterRoomCode,
        PlayerSelectCharacter
    }

    [Header("Segurança e Acesso do Mestre")]
    public string masterPassword = "mestre";
    public bool isMasterUnlocked = false;
    public bool openInitialMenuOnStart = true;

    [Header("Sala Online na Nuvem (Sem IP)")]
    public string roomCode = "";
    public bool isServerRunning = false;
    public bool isClientConnected = false;
    public string connectionStatus = "Offline";

    // Servidores gratuitos de Relay em Nuvem (com fallback automático)
    private readonly string[] cloudRelayBrokers = { "broker.emqx.io", "broker.hivemq.com", "test.mosquitto.org" };
    private int currentBrokerIndex = 0;
    private const int CloudRelayPort = 1883;

    [Header("Perfis de Jogadores (Salvos Automaticamente)")]
    public List<VTTPlayerProfile> profiles = new List<VTTPlayerProfile>();
    public List<string> excludedMaps = new List<string>();
    public int activeLocalProfileIndex = -1;

    private MenuScreen currentScreen = MenuScreen.InitialRoleSelect;
    private string passwordInput = "";
    private string passwordError = "";
    private int selectedStartingMapIndex = 0;
    private string saveStatusMessage = "";
    private float saveStatusTimer = 0f;

    private int editingProfileIndex = 0;
    private string currentBrowseDir = "";
    private string[] browserDirs = Array.Empty<string>();
    private string[] browserPngFiles = Array.Empty<string>();
    private Vector2 profileScrollPos;
    private Vector2 browserScrollPos;

    // Cliente Cloud Relay TCP (MQTT 3.1.1 puro, zero dependências externas)
    private TcpClient relayClient;
    private NetworkStream relayStream;
    private Thread relayReceiveThread;
    private volatile bool relayRunning = false;
    private string localPeerId = "";
    private string subscribedTopic = "";
    private float lastMoveSendTime = 0f;
    private float pingTimer = 0f;
    private readonly object streamWriteLock = new object();
    private readonly ConcurrentQueue<string> incomingMessages = new ConcurrentQueue<string>();

    private VTTCameraController camCtrl;
    private VTTFogOfWar fogOfWar;
    private Texture2D darkBgTex;

    private string SaveFilePath => Path.Combine(Application.persistentDataPath, "vtt_config_save.json");

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);
        Application.runInBackground = true;

        localPeerId = "PEER_" + Guid.NewGuid().ToString("N").Substring(0, 8);

        if (string.IsNullOrEmpty(roomCode))
            GenerateFiveCharRoomCode();

        LoadSavedData();
    }

    void Start()
    {
        RefreshSceneComponents();
        darkBgTex = new Texture2D(1, 1);
        darkBgTex.SetPixel(0, 0, new Color(0.05f, 0.06f, 0.09f, 0.90f));
        darkBgTex.Apply();

        ApplyExcludedMapsFilter();
        LinkExistingSceneTokens();

        currentScreen = openInitialMenuOnStart ? MenuScreen.InitialRoleSelect : MenuScreen.Closed;
    }

    // ========================================================================
    // SALVAMENTO LOCAL EM JSON + COMPRESSÃO DE PNG PARA ENVIO NA NUVEM
    // ========================================================================
    public void SaveDataToDisk(string feedbackMsg = "Alterações salvas automaticamente!")
    {
        try
        {
            VTTSaveData data = new VTTSaveData
            {
                savedMasterPassword = masterPassword,
                savedProfiles = profiles,
                excludedMapNames = excludedMaps
            };

            string json = JsonUtility.ToJson(data, true);
            File.WriteAllText(SaveFilePath, json, Encoding.UTF8);
            ShowFeedback(feedbackMsg);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[VTT Save] Erro ao salvar: {e.Message}");
        }
    }

    public void LoadSavedData()
    {
        if (File.Exists(SaveFilePath))
        {
            try
            {
                string json = File.ReadAllText(SaveFilePath, Encoding.UTF8);
                VTTSaveData data = JsonUtility.FromJson<VTTSaveData>(json);
                if (data != null)
                {
                    if (!string.IsNullOrEmpty(data.savedMasterPassword))
                        masterPassword = data.savedMasterPassword;
                    if (data.savedProfiles != null)
                        profiles = data.savedProfiles;
                    if (data.excludedMapNames != null)
                        excludedMaps = data.excludedMapNames;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[VTT Save] Erro ao carregar save: {e.Message}");
            }
        }

        if (profiles.Count == 0)
        {
            profiles.Add(new VTTPlayerProfile { profileId = "P1", playerName = "Investigador 1", tokenColor = Color.green, visionRadius = 22f, isSpawnedInScene = true, lastKnownPosition = new Vector3(0f, 0.02f, -4f) });
            profiles.Add(new VTTPlayerProfile { profileId = "P2", playerName = "Investigador 2", tokenColor = Color.yellow, visionRadius = 22f, isSpawnedInScene = true, lastKnownPosition = new Vector3(1.5f, 0.02f, -4f) });
        }

        for (int i = 0; i < profiles.Count; i++)
        {
            var p = profiles[i];
            if (p.tokenHeight <= 0.2f) p.tokenHeight = 1.75f;
            if (p.lastKnownPosition == Vector3.zero)
                p.lastKnownPosition = new Vector3((i % 3) * 1.5f, 0.02f, -4f);

            if (!string.IsNullOrEmpty(p.imageFilePath) && File.Exists(p.imageFilePath))
                LoadPngIntoProfile(p, p.imageFilePath, false);
        }
    }

    public void ResetAllSavedData()
    {
        if (File.Exists(SaveFilePath))
            File.Delete(SaveFilePath);

        foreach (var p in profiles)
        {
            if (p.spawnedToken != null)
                Destroy(p.spawnedToken.gameObject);
        }

        profiles.Clear();
        excludedMaps.Clear();
        profiles.Add(new VTTPlayerProfile { profileId = "P1", playerName = "Investigador 1", tokenColor = Color.green, visionRadius = 22f, isSpawnedInScene = true, lastKnownPosition = new Vector3(0f, 0.02f, -4f) });
        profiles.Add(new VTTPlayerProfile { profileId = "P2", playerName = "Investigador 2", tokenColor = Color.yellow, visionRadius = 22f, isSpawnedInScene = true, lastKnownPosition = new Vector3(1.5f, 0.02f, -4f) });
        SaveDataToDisk("Save resetado para o padrão de fábrica!");
    }

    private void ApplyExcludedMapsFilter()
    {
        if (VTTMapManager.Instance == null) return;
        VTTMapManager.Instance.CleanInvalidMenuEntries();
        if (excludedMaps.Count > 0)
            VTTMapManager.Instance.maps.RemoveAll(m => excludedMaps.Contains(m.mapName));
    }

    private void ShowFeedback(string msg)
    {
        saveStatusMessage = msg;
        saveStatusTimer = 3.5f;
    }

    private void RefreshSceneComponents()
    {
        camCtrl = FindFirstObjectByType<VTTCameraController>();
        fogOfWar = FindFirstObjectByType<VTTFogOfWar>();
    }

    public bool IsMenuOpen()
    {
        return currentScreen != MenuScreen.Closed;
    }

    void Update()
    {
        if (saveStatusTimer > 0f)
            saveStatusTimer -= Time.deltaTime;

        if (Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.F1))
        {
            if (currentScreen == MenuScreen.Closed)
            {
                currentScreen = isMasterUnlocked ? MenuScreen.MasterLobbySetup : MenuScreen.InitialRoleSelect;
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
            else if (currentScreen != MenuScreen.InitialRoleSelect)
            {
                SaveDataToDisk();
                currentScreen = MenuScreen.Closed;
            }
        }

        // Mantém a conexão com o servidor em nuvem ativa
        if (relayRunning && relayClient != null && relayClient.Connected)
        {
            pingTimer += Time.unscaledDeltaTime;
            if (pingTimer >= 20f)
            {
                pingTimer = 0f;
                SendMqttPing();
            }
        }

        ProcessNetworkQueue();
    }

    public void GenerateFiveCharRoomCode()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        char[] buffer = new char[5];
        for (int i = 0; i < 5; i++)
            buffer[i] = chars[UnityEngine.Random.Range(0, chars.Length)];
        roomCode = new string(buffer);
    }

    private void OpenPngFileSelector(int profileIdx)
    {
        editingProfileIndex = profileIdx;
#if UNITY_EDITOR
        string path = EditorUtility.OpenFilePanel("Selecionar Arte do Token (.PNG / .JPG)", "", "png,jpg,jpeg");
        if (!string.IsNullOrEmpty(path))
        {
            LoadPngIntoProfile(profiles[profileIdx], path, true);
        }
#else
        currentBrowseDir = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        RefreshFileBrowserEntries(currentBrowseDir);
        currentScreen = MenuScreen.FileBrowserFallback;
#endif
    }

    private void RefreshFileBrowserEntries(string dir)
    {
        try
        {
            currentBrowseDir = dir;
            browserDirs = Directory.GetDirectories(dir);
            List<string> imgs = new List<string>();
            foreach (string f in Directory.GetFiles(dir))
            {
                string ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext == ".png" || ext == ".jpg" || ext == ".jpeg")
                    imgs.Add(f);
            }
            browserPngFiles = imgs.ToArray();
        }
        catch
        {
            currentBrowseDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }
    }

    public void LoadPngIntoProfile(VTTPlayerProfile profile, string filePath, bool autoSave = true)
    {
        if (!File.Exists(filePath)) return;
        byte[] bytes = File.ReadAllBytes(filePath);
        Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        if (tex.LoadImage(bytes))
        {
            profile.imageFilePath = filePath;
            profile.portraitTexture = tex;
            profile.cachedPngBase64 = CompressTextureToBase64Png(tex, 180);

            if (profile.spawnedToken != null)
            {
                profile.spawnedToken.tokenHeightMeters = profile.tokenHeight;
                profile.spawnedToken.ApplyVisuals(profile.playerName, profile.portraitTexture, profile.tokenColor);
            }
            if (autoSave)
            {
                SaveDataToDisk($"Imagem de '{profile.playerName}' salva!");
                if (isServerRunning)
                    BroadcastSingleProfileImage(profile);
            }
        }
    }

    // Reduz a textura mantendo a transparência (.PNG) para enviar rápido pela nuvem aos jogadores
    private string CompressTextureToBase64Png(Texture2D source, int maxSize)
    {
        if (source == null) return "";
        try
        {
            int w = source.width;
            int h = source.height;
            if (w > maxSize || h > maxSize)
            {
                if (w > h) { h = Mathf.Max(1, (h * maxSize) / w); w = maxSize; }
                else { w = Mathf.Max(1, (w * maxSize) / h); h = maxSize; }
            }

            RenderTexture rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;
            GL.Clear(true, true, Color.clear);
            Graphics.Blit(source, rt);

            Texture2D small = new Texture2D(w, h, TextureFormat.RGBA32, false);
            small.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            small.Apply();

            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);

            byte[] pngBytes = small.EncodeToPNG();
            Destroy(small);
            return Convert.ToBase64String(pngBytes);
        }
        catch
        {
            return "";
        }
    }

    public void LinkExistingSceneTokens()
    {
        VTTToken25D[] all = FindObjectsByType<VTTToken25D>(FindObjectsSortMode.None);
        for (int i = 0; i < profiles.Count; i++)
        {
            foreach (var t in all)
            {
                if (t.profileId == profiles[i].profileId || t.gameObject.name == $"Token_{profiles[i].playerName}")
                {
                    t.profileId = profiles[i].profileId;
                    t.tokenHeightMeters = profiles[i].tokenHeight;
                    profiles[i].spawnedToken = t;
                    profiles[i].lastKnownPosition = t.transform.position;
                    t.ApplyVisuals(profiles[i].playerName, profiles[i].portraitTexture, profiles[i].tokenColor);
                    break;
                }
            }
        }
    }

    public VTTToken25D SpawnOrMoveProfileToken(VTTPlayerProfile profile, Vector3 spawnPos, bool broadcast = true)
    {
        RefreshSceneComponents();
        LinkExistingSceneTokens();

        profile.isSpawnedInScene = true;
        profile.lastKnownPosition = spawnPos;

        if (profile.spawnedToken == null)
        {
            GameObject rootObj = new GameObject($"Token_{profile.playerName}");
            rootObj.transform.position = spawnPos;
            rootObj.transform.localScale = Vector3.one;

            VTTToken25D tok = rootObj.AddComponent<VTTToken25D>();
            tok.profileId = profile.profileId;
            tok.tokenHeightMeters = profile.tokenHeight;
            profile.spawnedToken = tok;
        }
        else
        {
            profile.spawnedToken.transform.position = spawnPos;
            profile.spawnedToken.tokenHeightMeters = profile.tokenHeight;
        }

        profile.spawnedToken.ApplyVisuals(profile.playerName, profile.portraitTexture, profile.tokenColor);
        if (broadcast)
            NotifyTokenMoved(profile.spawnedToken, true);

        return profile.spawnedToken;
    }

    // ========================================================================
    // SERVIDOR DE RELAY EM NUVEM (ZERO IP / SALA POR CÓDIGO DE 5 CARACTERES)
    // ========================================================================
    public void StartHostServer()
    {
        SaveDataToDisk();
        isServerRunning = true;
        isClientConnected = false;

        ConnectToCloudRelayRoom(roomCode.Trim().ToUpperInvariant());

        if (VTTMapManager.Instance != null && VTTMapManager.Instance.maps.Count > 0)
        {
            selectedStartingMapIndex = Mathf.Clamp(selectedStartingMapIndex, 0, VTTMapManager.Instance.maps.Count - 1);
            VTTMapManager.Instance.SwitchToMap(selectedStartingMapIndex, true);
        }
    }

    public void ConnectPlayerToRoom()
    {
        string cleanCode = roomCode.Trim().ToUpperInvariant();
        if (cleanCode.Length < 3)
        {
            ShowFeedback("Digite o código de 5 caracteres da sala!");
            return;
        }

        roomCode = cleanCode;
        isServerRunning = false;
        isClientConnected = true;

        ConnectToCloudRelayRoom(roomCode);
        currentScreen = MenuScreen.PlayerSelectCharacter;
    }

    private void ConnectToCloudRelayRoom(string code)
    {
        CloseRelayConnection();

        subscribedTopic = $"vtt3d_mappo_room/{code}";
        relayRunning = true;
        connectionStatus = "Conectando ao servidor em nuvem...";

        relayReceiveThread = new Thread(CloudRelayThreadLoop) { IsBackground = true };
        relayReceiveThread.Start();
    }

    private void CloudRelayThreadLoop()
    {
        for (int attempt = 0; attempt < cloudRelayBrokers.Length && relayRunning; attempt++)
        {
            string brokerHost = cloudRelayBrokers[(currentBrokerIndex + attempt) % cloudRelayBrokers.Length];
            try
            {
                relayClient = new TcpClient();
                relayClient.NoDelay = true;
                relayClient.ReceiveTimeout = 0;
                relayClient.SendTimeout = 5000;
                relayClient.Connect(brokerHost, CloudRelayPort);
                relayStream = relayClient.GetStream();

                // 1. Envia pacote MQTT CONNECT
                SendMqttConnect($"VTT_{localPeerId}_{UnityEngine.Random.Range(100, 999)}");

                // 2. Lê confirmação CONNACK (4 bytes)
                int b1 = relayStream.ReadByte();
                int b2 = relayStream.ReadByte();
                if (b1 != 0x20 || b2 < 2) throw new Exception("Handshake inválido");
                byte[] connAckRest = ReadExactBytes(relayStream, b2);
                if (connAckRest[1] != 0) throw new Exception($"Código MQTT: {connAckRest[1]}");

                // 3. Assina o tópico exclusivo do Código de 5 Caracteres da Sala
                SendMqttSubscribe(subscribedTopic);

                currentBrokerIndex = (currentBrokerIndex + attempt) % cloudRelayBrokers.Length;
                incomingMessages.Enqueue($"SYS_RELAY_CONNECTED|{brokerHost}");

                // 4. Loop de leitura contínua em tempo real
                while (relayRunning && relayClient.Connected)
                {
                    int header = relayStream.ReadByte();
                    if (header < 0) break;

                    int remainingLength = ReadMqttRemainingLength(relayStream);
                    byte[] payloadBuffer = remainingLength > 0 ? ReadExactBytes(relayStream, remainingLength) : Array.Empty<byte>();

                    int packetType = (header >> 4) & 0x0F;
                    if (packetType == 3 && payloadBuffer.Length >= 2) // PUBLISH
                    {
                        int topicLen = (payloadBuffer[0] << 8) | payloadBuffer[1];
                        int offset = 2 + topicLen;
                        int qos = (header >> 1) & 0x03;
                        if (qos > 0) offset += 2;

                        if (offset < payloadBuffer.Length)
                        {
                            string message = Encoding.UTF8.GetString(payloadBuffer, offset, payloadBuffer.Length - offset);
                            incomingMessages.Enqueue(message);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                incomingMessages.Enqueue($"SYS_RELAY_ERROR|{e.Message}");
                Thread.Sleep(600);
            }
        }
    }

    private void SendMqttConnect(string clientId)
    {
        byte[] clientBytes = Encoding.UTF8.GetBytes(clientId);
        List<byte> body = new List<byte>
        {
            0x00, 0x04, (byte)'M', (byte)'Q', (byte)'T', (byte)'T',
            0x04,       // Protocol Level 4 (MQTT 3.1.1)
            0x02,       // Clean Session
            0x00, 0x3C, // Keep Alive 60s
            (byte)((clientBytes.Length >> 8) & 0xFF),
            (byte)(clientBytes.Length & 0xFF)
        };
        body.AddRange(clientBytes);
        WriteMqttPacket(0x10, body.ToArray());
    }

    private void SendMqttSubscribe(string topic)
    {
        byte[] topicBytes = Encoding.UTF8.GetBytes(topic);
        List<byte> body = new List<byte>
        {
            0x00, 0x01, // Packet ID
            (byte)((topicBytes.Length >> 8) & 0xFF),
            (byte)(topicBytes.Length & 0xFF)
        };
        body.AddRange(topicBytes);
        body.Add(0x00); // QoS 0
        WriteMqttPacket(0x82, body.ToArray());
    }

    private void PublishToCloudRoom(string rawPayload)
    {
        if (!relayRunning || relayStream == null || relayClient == null || !relayClient.Connected)
            return;

        try
        {
            string fullMsg = $"{localPeerId}|{rawPayload}";
            byte[] topicBytes = Encoding.UTF8.GetBytes(subscribedTopic);
            byte[] msgBytes = Encoding.UTF8.GetBytes(fullMsg);

            List<byte> body = new List<byte>(2 + topicBytes.Length + msgBytes.Length)
            {
                (byte)((topicBytes.Length >> 8) & 0xFF),
                (byte)(topicBytes.Length & 0xFF)
            };
            body.AddRange(topicBytes);
            body.AddRange(msgBytes);

            WriteMqttPacket(0x30, body.ToArray());
        }
        catch { }
    }

    private void SendMqttPing()
    {
        WriteMqttPacket(0xC0, Array.Empty<byte>());
    }

    private void WriteMqttPacket(byte headerByte, byte[] body)
    {
        if (relayStream == null) return;
        lock (streamWriteLock)
        {
            try
            {
                List<byte> packet = new List<byte>(5 + body.Length) { headerByte };
                int x = body.Length;
                do
                {
                    byte encoded = (byte)(x % 128);
                    x /= 128;
                    if (x > 0) encoded |= 128;
                    packet.Add(encoded);
                } while (x > 0);

                packet.AddRange(body);
                byte[] bytes = packet.ToArray();
                relayStream.Write(bytes, 0, bytes.Length);
                relayStream.Flush();
            }
            catch { }
        }
    }

    private static int ReadMqttRemainingLength(NetworkStream stream)
    {
        int multiplier = 1;
        int value = 0;
        int encodedByte;
        do
        {
            encodedByte = stream.ReadByte();
            if (encodedByte < 0) throw new EndOfStreamException();
            value += (encodedByte & 127) * multiplier;
            multiplier *= 128;
        } while ((encodedByte & 128) != 0);
        return value;
    }

    private static byte[] ReadExactBytes(NetworkStream stream, int count)
    {
        byte[] buffer = new byte[count];
        int offset = 0;
        while (offset < count)
        {
            int read = stream.Read(buffer, offset, count - offset);
            if (read <= 0) throw new EndOfStreamException();
            offset += read;
        }
        return buffer;
    }

    // ========================================================================
    // PROCESSAMENTO DAS MENSAGENS DE SINCRONIA NA THREAD PRINCIPAL DA UNITY
    // ========================================================================
    private void ProcessNetworkQueue()
    {
        while (incomingMessages.TryDequeue(out string raw))
        {
            if (raw.StartsWith("SYS_RELAY_CONNECTED|"))
            {
                connectionStatus = $"Online na Sala [{roomCode}]";
                ShowFeedback($"Conectado à Sala Online [{roomCode}]!");

                if (isServerRunning)
                    BroadcastFullRoomStateFromMaster();
                else if (isClientConnected)
                    PublishToCloudRoom("JOIN_REQ");
                continue;
            }

            if (raw.StartsWith("SYS_RELAY_ERROR|"))
            {
                connectionStatus = "Reconectando ao servidor...";
                continue;
            }

            int firstBar = raw.IndexOf('|');
            if (firstBar <= 0) continue;

            string senderId = raw.Substring(0, firstBar);
            if (senderId == localPeerId) continue; // Ignora pacotes enviados por mim mesmo

            string payload = raw.Substring(firstBar + 1);
            string[] parts = payload.Split('|');
            if (parts.Length == 0) continue;

            string cmd = parts[0];

            // Quando um Player entra na sala, o Mestre envia todos os Perfis, Posições, Mapa Ativo e PNGs
            if (cmd == "JOIN_REQ" && isServerRunning)
            {
                BroadcastFullRoomStateFromMaster();
            }
            else if (cmd == "MOVE" && parts.Length >= 5)
            {
                string profId = parts[1];
                float x = float.Parse(parts[2], CultureInfo.InvariantCulture);
                float y = float.Parse(parts[3], CultureInfo.InvariantCulture);
                float z = float.Parse(parts[4], CultureInfo.InvariantCulture);
                Vector3 newPos = new Vector3(x, y, z);

                foreach (var p in profiles)
                {
                    if (p.profileId == profId)
                    {
                        p.lastKnownPosition = newPos;
                        p.isSpawnedInScene = true;
                        if (p.spawnedToken != null)
                            p.spawnedToken.transform.position = newPos;
                        break;
                    }
                }

                VTTToken25D[] all = FindObjectsByType<VTTToken25D>(FindObjectsSortMode.None);
                foreach (var t in all)
                {
                    if (t.profileId == profId)
                    {
                        t.transform.position = newPos;
                        break;
                    }
                }
            }
            else if (cmd == "SPAWN" && parts.Length >= 6)
            {
                string profId = parts[1];
                bool active = parts[2] == "1";
                float x = float.Parse(parts[3], CultureInfo.InvariantCulture);
                float y = float.Parse(parts[4], CultureInfo.InvariantCulture);
                float z = float.Parse(parts[5], CultureInfo.InvariantCulture);
                Vector3 pos = new Vector3(x, y, z);

                foreach (var p in profiles)
                {
                    if (p.profileId == profId)
                    {
                        p.isSpawnedInScene = active;
                        p.lastKnownPosition = pos;
                        if (active)
                        {
                            SpawnOrMoveProfileToken(p, pos, false);
                        }
                        else if (p.spawnedToken != null)
                        {
                            Destroy(p.spawnedToken.gameObject);
                            p.spawnedToken = null;
                        }
                        break;
                    }
                }
            }
            else if (cmd == "MAP" && parts.Length >= 2)
            {
                int mapIdx = int.Parse(parts[1]);
                if (VTTMapManager.Instance != null)
                    VTTMapManager.Instance.SwitchToMap(mapIdx, false);
            }
            else if (cmd == "PROF_META" && parts.Length >= 11 && !isServerRunning)
            {
                string profId = parts[1];
                string pName = parts[2];
                float r = float.Parse(parts[3], CultureInfo.InvariantCulture);
                float g = float.Parse(parts[4], CultureInfo.InvariantCulture);
                float b = float.Parse(parts[5], CultureInfo.InvariantCulture);
                float vis = float.Parse(parts[6], CultureInfo.InvariantCulture);
                float h = float.Parse(parts[7], CultureInfo.InvariantCulture);
                bool spawned = parts[8] == "1";
                float px = float.Parse(parts[9], CultureInfo.InvariantCulture);
                float py = float.Parse(parts[10], CultureInfo.InvariantCulture);
                float pz = parts.Length >= 12 ? float.Parse(parts[11], CultureInfo.InvariantCulture) : 0f;

                VTTPlayerProfile existing = profiles.Find(pr => pr.profileId == profId);
                if (existing == null)
                {
                    existing = new VTTPlayerProfile { profileId = profId };
                    profiles.Add(existing);
                }

                existing.playerName = pName;
                existing.tokenColor = new Color(r, g, b, 1f);
                existing.visionRadius = vis;
                existing.tokenHeight = h;
                existing.isSpawnedInScene = spawned;
                existing.lastKnownPosition = new Vector3(px, py, pz);

                if (spawned && currentScreen == MenuScreen.Closed)
                    SpawnOrMoveProfileToken(existing, existing.lastKnownPosition, false);
                else if (existing.spawnedToken != null)
                    existing.spawnedToken.ApplyVisuals(existing.playerName, existing.portraitTexture, existing.tokenColor);
            }
            else if (cmd == "PROF_IMG" && parts.Length >= 3 && !isServerRunning)
            {
                string profId = parts[1];
                string base64 = parts[2];
                VTTPlayerProfile existing = profiles.Find(pr => pr.profileId == profId);
                if (existing != null && !string.IsNullOrEmpty(base64))
                {
                    try
                    {
                        byte[] pngBytes = Convert.FromBase64String(base64);
                        Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                        if (tex.LoadImage(pngBytes))
                        {
                            existing.portraitTexture = tex;
                            existing.cachedPngBase64 = base64;
                            if (existing.spawnedToken != null)
                                existing.spawnedToken.ApplyVisuals(existing.playerName, existing.portraitTexture, existing.tokenColor);
                        }
                    }
                    catch { }
                }
            }
        }
    }

    public void BroadcastFullRoomStateFromMaster()
    {
        if (!isServerRunning) return;

        LinkExistingSceneTokens();

        if (VTTMapManager.Instance != null)
            PublishToCloudRoom($"MAP|{VTTMapManager.Instance.currentMapIndex}");

        foreach (var p in profiles)
        {
            if (p.spawnedToken != null)
                p.lastKnownPosition = p.spawnedToken.transform.position;

            string meta = string.Format(
                CultureInfo.InvariantCulture,
                "PROF_META|{0}|{1}|{2:F2}|{3:F2}|{4:F2}|{5:F1}|{6:F2}|{7}|{8:F3}|{9:F3}|{10:F3}",
                p.profileId,
                p.playerName.Replace("|", " "),
                p.tokenColor.r, p.tokenColor.g, p.tokenColor.b,
                p.visionRadius,
                p.tokenHeight,
                p.isSpawnedInScene ? 1 : 0,
                p.lastKnownPosition.x, p.lastKnownPosition.y, p.lastKnownPosition.z
            );
            PublishToCloudRoom(meta);

            BroadcastSingleProfileImage(p);
        }
    }

    private void BroadcastSingleProfileImage(VTTPlayerProfile p)
    {
        if (p.portraitTexture == null) return;
        if (string.IsNullOrEmpty(p.cachedPngBase64))
            p.cachedPngBase64 = CompressTextureToBase64Png(p.portraitTexture, 180);

        if (!string.IsNullOrEmpty(p.cachedPngBase64))
            PublishToCloudRoom($"PROF_IMG|{p.profileId}|{p.cachedPngBase64}");
    }

    public void NotifyTokenMoved(VTTToken25D token, bool forceImmediate = false)
    {
        if (token == null) return;

        Vector3 p = token.transform.position;
        foreach (var prof in profiles)
        {
            if (prof.profileId == token.profileId)
            {
                prof.lastKnownPosition = p;
                break;
            }
        }

        if (!forceImmediate && Time.unscaledTime - lastMoveSendTime < 0.035f)
            return;

        lastMoveSendTime = Time.unscaledTime;
        string msg = string.Format(CultureInfo.InvariantCulture, "MOVE|{0}|{1:F3}|{2:F3}|{3:F3}", token.profileId, p.x, p.y, p.z);
        PublishToCloudRoom(msg);
    }

    public void BroadcastMapChange(int mapIndex)
    {
        if (!isServerRunning) return;
        PublishToCloudRoom($"MAP|{mapIndex}");
    }

    private void CloseRelayConnection()
    {
        relayRunning = false;
        try { relayStream?.Close(); } catch { }
        try { relayClient?.Close(); } catch { }
        relayStream = null;
        relayClient = null;
    }

    void OnDestroy()
    {
        CloseRelayConnection();
    }

    // ========================================================================
    // INTERFACE ESCALADA EM 1920x1080 (SEM CAMPO DE IP — SÓ CÓDIGO DA SALA)
    // ========================================================================
    void OnGUI()
    {
        RefreshSceneComponents();

        Matrix4x4 oldMatrix = GUI.matrix;
        float scaleX = Screen.width / 1920f;
        float scaleY = Screen.height / 1080f;
        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scaleX, scaleY, 1f));

        if (currentScreen == MenuScreen.Closed && !isMasterUnlocked && activeLocalProfileIndex >= 0)
        {
            DrawSpectatorFriendBar();
            GUI.matrix = oldMatrix;
            return;
        }

        if (currentScreen == MenuScreen.Closed)
        {
            GUI.matrix = oldMatrix;
            return;
        }

        if (darkBgTex != null)
            GUI.DrawTexture(new Rect(0, 0, 1920f, 1080f), darkBgTex);

        DrawResolutionBarTopRight();

        switch (currentScreen)
        {
            case MenuScreen.InitialRoleSelect:
                DrawInitialRoleSelect();
                break;
            case MenuScreen.MasterPasswordCheck:
                DrawMasterPasswordCheck();
                break;
            case MenuScreen.MasterLobbySetup:
                DrawMasterLobbySetup();
                break;
            case MenuScreen.MasterProfileManager:
                DrawMasterProfileManager();
                break;
            case MenuScreen.FileBrowserFallback:
                DrawFileBrowserFallback();
                break;
            case MenuScreen.PlayerEnterRoomCode:
                DrawPlayerEnterRoomCode();
                break;
            case MenuScreen.PlayerSelectCharacter:
                DrawPlayerSelectCharacter();
                break;
        }

        if (saveStatusTimer > 0f && !string.IsNullOrEmpty(saveStatusMessage))
        {
            GUI.backgroundColor = new Color(0.15f, 0.85f, 0.35f);
            GUI.Box(new Rect((1920f - 520f) * 0.5f, 1015f, 520f, 42f), saveStatusMessage);
            GUI.backgroundColor = Color.white;
        }

        GUI.matrix = oldMatrix;
    }

    private void DrawResolutionBarTopRight()
    {
        Rect resBox = new Rect(1920f - 430f, 20f, 410f, 48f);
        GUI.Box(resBox, "");
        GUI.Label(new Rect(resBox.x + 12f, resBox.y + 13f, 145f, 24f), $"Status: {(relayRunning ? "Nuvem ON" : "Local")}");

        if (GUI.Button(new Rect(resBox.x + 150f, resBox.y + 8f, 120f, 32f), "Tela Cheia"))
            Screen.SetResolution(1920, 1080, FullScreenMode.FullScreenWindow);
        if (GUI.Button(new Rect(resBox.x + 278f, resBox.y + 8f, 120f, 32f), "Modo Janela"))
            Screen.SetResolution(1600, 900, FullScreenMode.Windowed);
    }

    private void DrawInitialRoleSelect()
    {
        float w = 560f, h = 380f;
        Rect r = new Rect((1920f - w) * 0.5f, (1080f - h) * 0.5f, w, h);
        GUI.Box(r, "BEM-VINDO AO VTT 3D — MENU PRINCIPAL");

        GUIStyle sub = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 16 };
        GUI.Label(new Rect(r.x + 30, r.y + 48, w - 60, 30), "Escolha como deseja entrar na sessão:", sub);

        GUI.backgroundColor = new Color(0.25f, 0.75f, 1f);
        if (GUI.Button(new Rect(r.x + 55, r.y + 100, w - 110, 64), "ENTRAR COMO MESTRE (Requer Senha)"))
        {
            passwordInput = "";
            passwordError = "";
            currentScreen = MenuScreen.MasterPasswordCheck;
        }

        GUI.backgroundColor = new Color(0.3f, 0.95f, 0.5f);
        if (GUI.Button(new Rect(r.x + 55, r.y + 184, w - 110, 64), "ENTRAR COMO PLAYER (Código da Sala)"))
        {
            isMasterUnlocked = false;
            currentScreen = MenuScreen.PlayerEnterRoomCode;
        }
        GUI.backgroundColor = Color.white;

        if (GUI.Button(new Rect(r.x + 140, r.y + 295, w - 280, 44), "Sair do Aplicativo"))
            Application.Quit();
    }

    private void DrawMasterPasswordCheck()
    {
        float w = 480f, h = 290f;
        Rect r = new Rect((1920f - w) * 0.5f, (1080f - h) * 0.5f, w, h);
        GUI.Box(r, "AUTENTICAÇÃO DO MESTRE");

        GUI.Label(new Rect(r.x + 40, r.y + 55, w - 80, 28), "Digite a Senha do Mestre:");
        passwordInput = GUI.PasswordField(new Rect(r.x + 40, r.y + 90, w - 80, 38), passwordInput, '*');

        if (!string.IsNullOrEmpty(passwordError))
        {
            GUI.color = Color.red;
            GUI.Label(new Rect(r.x + 40, r.y + 136, w - 80, 26), passwordError);
            GUI.color = Color.white;
        }

        if (GUI.Button(new Rect(r.x + 40, r.y + 185, 190, 46), "Desbloquear"))
        {
            if (passwordInput == masterPassword)
            {
                isMasterUnlocked = true;
                if (camCtrl != null) camCtrl.SetPlayerVisionMode(false);
                currentScreen = MenuScreen.MasterLobbySetup;
            }
            else
            {
                passwordError = "Senha incorreta!";
            }
        }

        if (GUI.Button(new Rect(r.x + 250, r.y + 185, 190, 46), "Voltar"))
            currentScreen = MenuScreen.InitialRoleSelect;
    }

    private void DrawMasterLobbySetup()
    {
        ApplyExcludedMapsFilter();

        float w = 680f, h = 580f;
        Rect r = new Rect((1920f - w) * 0.5f, (1080f - h) * 0.5f, w, h);
        GUI.Box(r, "PAINEL DO MESTRE — SALA NA NUVEM, MAPAS E PERFIS");

        GUI.Label(new Rect(r.x + 35, r.y + 48, 340, 28), "Código da Sala Online (5 Caracteres):");
        GUIStyle codeStyle = new GUIStyle(GUI.skin.box) { fontSize = 24, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        GUI.Box(new Rect(r.x + 35, r.y + 80, 210, 46), roomCode, codeStyle);

        if (GUI.Button(new Rect(r.x + 260, r.y + 80, 185, 46), "Copiar Código"))
        {
            GUIUtility.systemCopyBuffer = roomCode;
            ShowFeedback("Código copiado! Envie para seus jogadores no Discord.");
        }
        if (GUI.Button(new Rect(r.x + 460, r.y + 80, 185, 46), "Gerar Novo Código"))
        {
            GenerateFiveCharRoomCode();
        }

        GUI.Label(new Rect(r.x + 35, r.y + 145, w - 70, 28), "Escolher Mapa Inicial (Clique no [X] para ocultar cenas extras):");

        if (VTTMapManager.Instance != null && VTTMapManager.Instance.maps.Count > 0)
        {
            int mapCount = VTTMapManager.Instance.maps.Count;
            selectedStartingMapIndex = Mathf.Clamp(selectedStartingMapIndex, 0, mapCount - 1);

            for (int i = 0; i < mapCount; i++)
            {
                bool sel = (i == selectedStartingMapIndex);
                float bx = r.x + 35 + (i % 2) * 310f;
                float by = r.y + 180 + (i / 2) * 44f;

                GUI.backgroundColor = sel ? Color.cyan : Color.white;
                if (GUI.Button(new Rect(bx, by, 255f, 36f), (sel ? ">> " : "") + VTTMapManager.Instance.maps[i].mapName))
                    selectedStartingMapIndex = i;

                GUI.backgroundColor = new Color(1f, 0.35f, 0.35f);
                if (GUI.Button(new Rect(bx + 260f, by, 38f, 36f), "X"))
                {
                    string removedName = VTTMapManager.Instance.maps[i].mapName;
                    if (!excludedMaps.Contains(removedName))
                        excludedMaps.Add(removedName);
                    VTTMapManager.Instance.maps.RemoveAt(i);
                    SaveDataToDisk($"Cena '{removedName}' removida da lista!");
                    break;
                }
            }
            GUI.backgroundColor = Color.white;
        }
        else
        {
            GUI.Label(new Rect(r.x + 35, r.y + 185, w - 70, 32), "Nenhum mapa listado.");
        }

        if (excludedMaps.Count > 0)
        {
            if (GUI.Button(new Rect(r.x + 35, r.y + 315, 260, 32), "Restaurar Cenas Ocultadas"))
            {
                excludedMaps.Clear();
                SaveDataToDisk("Cenas restauradas! Reinicie o Play para recarregar todas.");
            }
        }

        GUI.backgroundColor = Color.green;
        if (GUI.Button(new Rect(r.x + 35, r.y + 360, w - 70, 54), isServerRunning ? $"SALA [{roomCode}] ATIVA NA NUVEM (Clique para Recarregar Mapa)" : "CRIAR SALA ONLINE NA NUVEM E CARREGAR MAPA"))
        {
            StartHostServer();
            currentScreen = MenuScreen.Closed;
        }
        GUI.backgroundColor = Color.white;

        if (GUI.Button(new Rect(r.x + 35, r.y + 426, w - 70, 48), "GERENCIAR PERFIS E IMAGENS DE TOKENS (.PNG)"))
        {
            LinkExistingSceneTokens();
            currentScreen = MenuScreen.MasterProfileManager;
        }

        if (GUI.Button(new Rect(r.x + 35, r.y + 490, 295, 44), "Fechar Menu (Continuar no Mapa)"))
        {
            SaveDataToDisk();
            currentScreen = MenuScreen.Closed;
        }
        if (GUI.Button(new Rect(r.x + 350, r.y + 490, 295, 44), "Voltar à Tela Inicial (Player/Mestre)"))
        {
            SaveDataToDisk();
            currentScreen = MenuScreen.InitialRoleSelect;
        }
    }

    private void DrawMasterProfileManager()
    {
        float w = 920f, h = 680f;
        Rect r = new Rect((1920f - w) * 0.5f, (1080f - h) * 0.5f, w, h);
        GUI.Box(r, "GERENCIADOR DE PERFIS E TOKENS 2.5D (MESTRE)");

        GUI.backgroundColor = new Color(0.3f, 0.95f, 0.5f);
        if (GUI.Button(new Rect(r.x + 30, r.y + 42, 260, 40), "+ Criar Novo Perfil de Player"))
        {
            profiles.Add(new VTTPlayerProfile
            {
                profileId = Guid.NewGuid().ToString("N").Substring(0, 6),
                playerName = $"Investigador {profiles.Count + 1}",
                tokenColor = Color.cyan,
                visionRadius = 22f,
                tokenHeight = 1.75f,
                isSpawnedInScene = true,
                lastKnownPosition = new Vector3((profiles.Count % 3) * 1.5f, 0.02f, -4f)
            });
            SaveDataToDisk("Novo perfil criado e salvo!");
            if (isServerRunning) BroadcastFullRoomStateFromMaster();
        }

        GUI.backgroundColor = new Color(0.4f, 0.8f, 1f);
        if (GUI.Button(new Rect(r.x + 305, r.y + 42, 260, 40), "Salvar e Sincronizar com Players"))
        {
            SaveDataToDisk("Perfis salvos e enviados para a sala online!");
            if (isServerRunning) BroadcastFullRoomStateFromMaster();
        }

        GUI.backgroundColor = new Color(1f, 0.45f, 0.45f);
        if (GUI.Button(new Rect(r.x + w - 260, r.y + 42, 230, 40), "Resetar Save (Padrão)"))
            ResetAllSavedData();
        GUI.backgroundColor = Color.white;

        Rect scrollView = new Rect(r.x + 30, r.y + 96, w - 60, h - 175);
        Rect contentRect = new Rect(0, 0, w - 90, profiles.Count * 135f + 20f);
        profileScrollPos = GUI.BeginScrollView(scrollView, profileScrollPos, contentRect);

        Color[] palette = { Color.green, Color.yellow, Color.cyan, Color.magenta, Color.red, new Color(1f, 0.5f, 0f), Color.white };

        for (int i = 0; i < profiles.Count; i++)
        {
            VTTPlayerProfile p = profiles[i];
            float y = i * 135f;
            GUI.Box(new Rect(0, y, contentRect.width, 124), "");

            if (p.portraitTexture != null)
                GUI.DrawTexture(new Rect(14, y + 14, 96, 96), p.portraitTexture, ScaleMode.ScaleToFit);
            else
                GUI.Box(new Rect(14, y + 14, 96, 96), "Sem\nPNG");

            GUI.Label(new Rect(125, y + 14, 60, 28), "Nome:");
            string newName = GUI.TextField(new Rect(185, y + 14, 230, 28), p.playerName);
            if (newName != p.playerName)
            {
                p.playerName = newName;
                if (p.spawnedToken != null)
                    p.spawnedToken.ApplyVisuals(p.playerName, p.portraitTexture, p.tokenColor);
            }

            GUI.Label(new Rect(435, y + 14, 110, 28), $"Visão: {p.visionRadius:F0}m");
            p.visionRadius = GUI.HorizontalSlider(new Rect(535, y + 20, 120, 20), p.visionRadius, 5f, 50f);

            GUI.Label(new Rect(670, y + 14, 150, 28), $"Altura: {p.tokenHeight:F2}m");
            float newH = GUI.HorizontalSlider(new Rect(775, y + 20, 95, 20), p.tokenHeight, 0.8f, 3.5f);
            if (Mathf.Abs(newH - p.tokenHeight) > 0.01f)
            {
                p.tokenHeight = newH;
                if (p.spawnedToken != null)
                {
                    p.spawnedToken.tokenHeightMeters = p.tokenHeight;
                    p.spawnedToken.ApplyVisuals(p.playerName, p.portraitTexture, p.tokenColor);
                }
            }

            if (GUI.Button(new Rect(125, y + 50, 290, 32), "Procurar Imagem (.PNG)..."))
                OpenPngFileSelector(i);

            GUI.Label(new Rect(435, y + 54, 45, 28), "Cor:");
            for (int c = 0; c < palette.Length; c++)
            {
                GUI.backgroundColor = palette[c];
                if (GUI.Button(new Rect(480 + c * 34, y + 50, 30, 30), ""))
                {
                    p.tokenColor = palette[c];
                    if (p.spawnedToken != null)
                        p.spawnedToken.ApplyVisuals(p.playerName, p.portraitTexture, p.tokenColor);
                    SaveDataToDisk();
                }
            }
            GUI.backgroundColor = Color.white;

            if (GUI.Button(new Rect(125, y + 88, 250, 28), "Colocar Token no Cursor 3D"))
            {
                Vector3 pos = (camCtrl != null && camCtrl.cursor3D != null) ? camCtrl.cursor3D.position : Vector3.zero;
                SpawnOrMoveProfileToken(p, pos, true);
                PublishToCloudRoom(string.Format(CultureInfo.InvariantCulture, "SPAWN|{0}|1|{1:F3}|{2:F3}|{3:F3}", p.profileId, pos.x, pos.y, pos.z));
            }

            if (p.spawnedToken != null)
            {
                GUI.backgroundColor = new Color(1f, 0.6f, 0.2f);
                if (GUI.Button(new Rect(390, y + 88, 190, 28), "Retirar Token da Cena"))
                {
                    p.isSpawnedInScene = false;
                    Destroy(p.spawnedToken.gameObject);
                    p.spawnedToken = null;
                    PublishToCloudRoom($"SPAWN|{p.profileId}|0|0|0|0");
                }
                GUI.backgroundColor = Color.white;
            }

            GUI.backgroundColor = new Color(1f, 0.3f, 0.3f);
            if (GUI.Button(new Rect(contentRect.width - 165, y + 88, 150, 28), "Excluir Perfil [X]"))
            {
                if (p.spawnedToken != null)
                    Destroy(p.spawnedToken.gameObject);
                profiles.RemoveAt(i);
                SaveDataToDisk("Perfil excluído!");
                break;
            }
            GUI.backgroundColor = Color.white;
        }

        GUI.EndScrollView();

        if (GUI.Button(new Rect(r.x + 280, r.y + h - 62, w - 560, 46), "Salvar e Voltar ao Painel do Mestre"))
        {
            SaveDataToDisk();
            if (isServerRunning) BroadcastFullRoomStateFromMaster();
            currentScreen = MenuScreen.MasterLobbySetup;
        }
    }

    private void DrawFileBrowserFallback()
    {
        float w = 780f, h = 580f;
        Rect r = new Rect((1920f - w) * 0.5f, (1080f - h) * 0.5f, w, h);
        GUI.Box(r, "SELECIONAR IMAGEM (.PNG / .JPG) DO COMPUTADOR");

        GUI.Label(new Rect(r.x + 25, r.y + 38, w - 50, 26), $"Pasta Atual: {currentBrowseDir}");

        if (GUI.Button(new Rect(r.x + 25, r.y + 68, 150, 34), "Pasta Acima"))
        {
            DirectoryInfo parent = Directory.GetParent(currentBrowseDir);
            if (parent != null) RefreshFileBrowserEntries(parent.FullName);
        }
        if (GUI.Button(new Rect(r.x + 185, r.y + 68, 160, 34), "Área de Trabalho"))
            RefreshFileBrowserEntries(Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
        if (GUI.Button(new Rect(r.x + 355, r.y + 68, 160, 34), "Meus Documentos"))
            RefreshFileBrowserEntries(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));

        int totalItems = browserDirs.Length + browserPngFiles.Length;
        Rect scrollRect = new Rect(r.x + 25, r.y + 115, w - 50, h - 185);
        Rect content = new Rect(0, 0, w - 80, totalItems * 36f + 10f);
        browserScrollPos = GUI.BeginScrollView(scrollRect, browserScrollPos, content);

        float y = 0f;
        foreach (string d in browserDirs)
        {
            if (GUI.Button(new Rect(0, y, content.width, 32), $"[PASTA] {Path.GetFileName(d)}"))
            {
                RefreshFileBrowserEntries(d);
                break;
            }
            y += 36f;
        }

        GUI.backgroundColor = Color.cyan;
        foreach (string f in browserPngFiles)
        {
            if (GUI.Button(new Rect(0, y, content.width, 32), $"[IMAGEM] {Path.GetFileName(f)}"))
            {
                LoadPngIntoProfile(profiles[editingProfileIndex], f, true);
                currentScreen = MenuScreen.MasterProfileManager;
                break;
            }
            y += 36f;
        }
        GUI.backgroundColor = Color.white;
        GUI.EndScrollView();

        if (GUI.Button(new Rect(r.x + 280, r.y + h - 55, 220, 40), "Cancelar"))
            currentScreen = MenuScreen.MasterProfileManager;
    }

    // TELA DO PLAYER: APENAS O CÓDIGO DE 5 CARACTERES (ZERO IP!)
    private void DrawPlayerEnterRoomCode()
    {
        float w = 520f, h = 300f;
        Rect r = new Rect((1920f - w) * 0.5f, (1080f - h) * 0.5f, w, h);
        GUI.Box(r, "ENTRAR EM UMA SALA ONLINE (PLAYER)");

        GUI.Label(new Rect(r.x + 35, r.y + 52, w - 70, 28), "Digite o Código de 5 Caracteres da Sala:");

        GUIStyle codeInputStyle = new GUIStyle(GUI.skin.textField) { fontSize = 24, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        roomCode = GUI.TextField(new Rect(r.x + 35, r.y + 88, 275, 48), roomCode.ToUpperInvariant(), 5, codeInputStyle);

        if (GUI.Button(new Rect(r.x + 325, r.y + 88, 160, 48), "Colar (Ctrl+V)"))
        {
            if (!string.IsNullOrEmpty(GUIUtility.systemCopyBuffer))
            {
                string clip = GUIUtility.systemCopyBuffer.Trim().ToUpperInvariant();
                if (clip.Length > 5) clip = clip.Substring(0, 5);
                roomCode = clip;
            }
        }

        GUI.backgroundColor = Color.green;
        if (GUI.Button(new Rect(r.x + 35, r.y + 160, w - 70, 54), "CONECTAR À SALA E ESCOLHER PERFIL"))
        {
            ConnectPlayerToRoom();
        }
        GUI.backgroundColor = Color.white;

        if (GUI.Button(new Rect(r.x + 160, r.y + 232, w - 320, 42), "Voltar"))
            currentScreen = MenuScreen.InitialRoleSelect;
    }

    private void DrawPlayerSelectCharacter()
    {
        LinkExistingSceneTokens();

        float w = 780f, h = 580f;
        Rect r = new Rect((1920f - w) * 0.5f, (1080f - h) * 0.5f, w, h);
        GUI.Box(r, $"SALA ONLINE [{roomCode}] — {connectionStatus}");

        int cols = 2;
        float cardW = 345f, cardH = 120f;

        for (int i = 0; i < profiles.Count; i++)
        {
            VTTPlayerProfile p = profiles[i];
            int col = i % cols;
            int row = i / cols;
            Rect card = new Rect(r.x + 30 + col * (cardW + 25), r.y + 55 + row * (cardH + 18), cardW, cardH);

            GUI.Box(card, "");
            if (p.portraitTexture != null)
                GUI.DrawTexture(new Rect(card.x + 12, card.y + 12, 96, 96), p.portraitTexture, ScaleMode.ScaleToFit);
            else
                GUI.Box(new Rect(card.x + 12, card.y + 12, 96, 96), "Token");

            GUI.Label(new Rect(card.x + 120, card.y + 16, 210, 28), $"<b>{p.playerName}</b>");
            GUI.Label(new Rect(card.x + 120, card.y + 44, 210, 24), $"Visão: {p.visionRadius:F0}m");

            GUI.backgroundColor = p.tokenColor;
            if (GUI.Button(new Rect(card.x + 120, card.y + 74, 205, 34), "JOIN (Entrar)"))
            {
                SelectPlayerProfileAndJoin(i);
            }
            GUI.backgroundColor = Color.white;
        }

        if (GUI.Button(new Rect(r.x + 160, r.y + h - 58, 220, 42), "Sincronizar Perfis"))
        {
            PublishToCloudRoom("JOIN_REQ");
        }

        if (GUI.Button(new Rect(r.x + 400, r.y + h - 58, 220, 42), "Voltar"))
            currentScreen = MenuScreen.PlayerEnterRoomCode;
    }

    private void SelectPlayerProfileAndJoin(int index)
    {
        activeLocalProfileIndex = index;
        VTTPlayerProfile chosen = profiles[index];

        // Se o Player ainda estiver na CenaMenu, carrega o mapa ativo da sessão
        if (VTTMapManager.Instance != null && VTTMapManager.Instance.maps.Count > 0)
        {
            VTTMapManager.Instance.SwitchToMap(VTTMapManager.Instance.currentMapIndex, false);
        }

        if (chosen.spawnedToken == null)
            SpawnOrMoveProfileToken(chosen, chosen.lastKnownPosition, true);

        if (fogOfWar != null)
            fogOfWar.visionRadius = chosen.visionRadius;

        if (camCtrl != null && chosen.spawnedToken != null)
        {
            camCtrl.myPlayerToken = chosen.spawnedToken.transform;
            camCtrl.viewedVisionToken = chosen.spawnedToken.transform;
            camCtrl.SetPlayerVisionMode(true);
            camCtrl.FocusOnActiveToken();
        }

        currentScreen = MenuScreen.Closed;
    }

    private void DrawSpectatorFriendBar()
    {
        float barW = 920f;
        Rect bar = new Rect((1920f - barW) * 0.5f, 14f, barW, 46f);
        GUI.Box(bar, "");

        GUI.Label(new Rect(bar.x + 16, bar.y + 12, 165, 26), "Ver pelos olhos de:");
        float x = bar.x + 180f;

        for (int i = 0; i < profiles.Count; i++)
        {
            VTTPlayerProfile p = profiles[i];
            if (p.spawnedToken == null) continue;

            bool isViewingThis = (camCtrl != null && camCtrl.viewedVisionToken == p.spawnedToken.transform);
            GUI.backgroundColor = isViewingThis ? Color.green : Color.white;

            string label = (i == activeLocalProfileIndex) ? $"{p.playerName} (Você)" : p.playerName;
            if (GUI.Button(new Rect(x, bar.y + 8, 165, 30), label))
            {
                if (camCtrl != null)
                {
                    camCtrl.viewedVisionToken = p.spawnedToken.transform;
                    if (fogOfWar != null) fogOfWar.visionRadius = p.visionRadius;
                    camCtrl.FocusOnActiveToken();
                }
            }
            x += 172f;
        }
        GUI.backgroundColor = Color.white;
    }
}