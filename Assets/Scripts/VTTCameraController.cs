using UnityEngine;
using UnityEngine.Rendering;

public class VTTCameraController : MonoBehaviour
{
    [Header("Modos da Câmera (TAB para alternar)")]
    public bool isTopDownBG3 = false;
    public float flySpeed = 16f;
    public float fastFlyMultiplier = 2.5f;
    public float slowFlyMultiplier = 0.35f;
    public float movementSmoothing = 14f;
    public float topDownDistance = 16f;
    public float mouseSensitivity = 2.4f;

    [Header("Visão do Jogador e Limite de Raio")]
    public bool isPlayerVisionMode = false;
    public Transform myPlayerToken;
    public Transform viewedVisionToken;
    public float playerCameraLeash = 18f;

    [Header("Referências")]
    public Transform cursor3D;
    public GameObject roofObject;
    private bool roofHidden = false;

    private float yaw = 180f;
    private float pitch = 35f;
    private Vector3 bg3FocusPoint = new Vector3(0f, 0f, -5f);
    private Vector3 currentVelocity;

    private VTTToken25D draggedToken = null;
    private Camera cam;
    private VTTFogOfWar fogOfWar;

    // Editor de Tags em Runtime (Alt + Clique Esquerdo)
    private GameObject selectedObjectForTag;
    private bool showTagPopup = false;

    void Start()
    {
        cam = GetComponent<Camera>();
        Vector3 angles = transform.eulerAngles;
        yaw = angles.y;
        pitch = angles.x;

        if (cursor3D != null)
        {
            Collider cCol = cursor3D.GetComponent<Collider>();
            if (cCol != null) Destroy(cCol);
        }

        fogOfWar = FindFirstObjectByType<VTTFogOfWar>();
        if (fogOfWar == null)
        {
            GameObject fogObj = new GameObject("VTT_FogOfWar_System");
            fogOfWar = fogObj.AddComponent<VTTFogOfWar>();
        }
    }

    private bool IsAnyMenuBlocking()
    {
        if (VTTMapManager.Instance != null && VTTMapManager.Instance.showMapMenu) return true;
        if (VTTNetworkManager.Instance != null && VTTNetworkManager.Instance.IsMenuOpen()) return true;
        return false;
    }

    void Update()
    {
        if (IsAnyMenuBlocking())
        {
            if (Cursor.lockState != CursorLockMode.None)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
            return;
        }

        HandleShortcuts();
        HandleCameraMovement();
        HandleMouseAndTokens();
    }

    void HandleShortcuts()
    {
        bool isMaster = VTTNetworkManager.Instance == null || VTTNetworkManager.Instance.isMasterUnlocked;

        if (Input.GetKeyDown(KeyCode.Tab))
        {
            isTopDownBG3 = !isTopDownBG3;
            if (isTopDownBG3)
            {
                pitch = 55f;
                Transform refT = GetCurrentVisionReferenceToken();
                if (refT != null) bg3FocusPoint = refT.position;
                else if (cursor3D != null) bg3FocusPoint = cursor3D.position;
            }
        }

        // Apenas o Mestre pode ligar/desligar a Fog manualmente na tecla V
        if (isMaster && Input.GetKeyDown(KeyCode.V))
        {
            SetPlayerVisionMode(!isPlayerVisionMode);
        }

        if (Input.GetKeyDown(KeyCode.F) || Input.GetKeyDown(KeyCode.Space))
        {
            FocusOnActiveToken();
        }

        if (Input.GetKeyDown(KeyCode.T))
        {
            roofHidden = !roofHidden;
            ToggleRoofMeshes(roofHidden);
        }
    }

    private void ToggleRoofMeshes(bool hide)
    {
        MeshRenderer[] all = FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None);
        foreach (var r in all)
        {
            if (VTTTagSystem.HasTagInHierarchy(r.transform, VTTTagSystem.TAG_TELHADO) ||
                (roofObject != null && r.transform.IsChildOf(roofObject.transform)))
            {
                r.shadowCastingMode = hide ? ShadowCastingMode.ShadowsOnly : ShadowCastingMode.On;
            }
        }
    }

    public void SetPlayerVisionMode(bool enabled)
    {
        isPlayerVisionMode = enabled;
        if (fogOfWar != null)
        {
            Transform target = GetCurrentVisionReferenceToken();
            if (target != null) fogOfWar.activeToken = target;
            fogOfWar.SetPlayerView(isPlayerVisionMode);
        }
        if (isPlayerVisionMode) FocusOnActiveToken();
    }

    public Transform GetCurrentVisionReferenceToken()
    {
        if (viewedVisionToken != null) return viewedVisionToken;
        if (myPlayerToken != null) return myPlayerToken;
        if (fogOfWar != null && fogOfWar.activeToken != null) return fogOfWar.activeToken;
        return null;
    }

    public void FocusOnActiveToken()
    {
        Transform target = GetCurrentVisionReferenceToken();
        if (target == null) return;

        bg3FocusPoint = target.position;
        if (!isTopDownBG3)
        {
            Quaternion rot = Quaternion.Euler(30f, yaw, 0f);
            transform.position = target.position + Vector3.up * 2.2f - (rot * Vector3.forward * 4.5f);
            pitch = 30f;
        }
    }

    void HandleCameraMovement()
    {
        if (Input.GetMouseButtonDown(1))
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
        if (Input.GetMouseButtonUp(1))
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        if (Input.GetMouseButton(1))
        {
            yaw += Input.GetAxis("Mouse X") * mouseSensitivity * 3f;
            if (!isTopDownBG3)
            {
                pitch -= Input.GetAxis("Mouse Y") * mouseSensitivity * 3f;
                pitch = Mathf.Clamp(pitch, -85f, 85f);
            }
        }

        float h = (Input.GetKey(KeyCode.D) ? 1f : 0f) - (Input.GetKey(KeyCode.A) ? 1f : 0f);
        float v = (Input.GetKey(KeyCode.W) ? 1f : 0f) - (Input.GetKey(KeyCode.S) ? 1f : 0f);
        float upDown = (Input.GetKey(KeyCode.E) ? 1f : 0f) - (Input.GetKey(KeyCode.Q) ? 1f : 0f);

        float speedMult = Input.GetKey(KeyCode.LeftShift) ? fastFlyMultiplier :
                          (Input.GetKey(KeyCode.LeftControl) ? slowFlyMultiplier : 1f);
        float scroll = Input.mouseScrollDelta.y;

        if (!isTopDownBG3)
        {
            if (Input.GetMouseButton(1) && Mathf.Abs(scroll) > 0.01f)
                flySpeed = Mathf.Clamp(flySpeed + scroll * 2.5f, 2f, 80f);
            else if (!Input.GetMouseButton(1) && Mathf.Abs(scroll) > 0.01f)
                transform.position += transform.forward * (scroll * 1.8f);

            if (Input.GetMouseButton(2))
            {
                Vector3 pan = (-transform.right * Input.GetAxis("Mouse X") - transform.up * Input.GetAxis("Mouse Y")) * 0.45f;
                transform.position += pan;
            }

            transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
            Vector3 desiredVel = (transform.forward * v + transform.right * h + Vector3.up * upDown).normalized * (flySpeed * speedMult);
            currentVelocity = Vector3.Lerp(currentVelocity, desiredVel, Time.deltaTime * movementSmoothing);
            transform.position += currentVelocity * Time.deltaTime;
        }
        else
        {
            if (Mathf.Abs(scroll) > 0.01f)
                topDownDistance = Mathf.Clamp(topDownDistance - scroll * 2f, 3f, 65f);

            Quaternion flatRot = Quaternion.Euler(0f, yaw, 0f);
            if (Input.GetMouseButton(2))
            {
                Vector3 panMove = (-(flatRot * Vector3.right) * Input.GetAxis("Mouse X") - (flatRot * Vector3.forward) * Input.GetAxis("Mouse Y")) * (topDownDistance * 0.04f);
                bg3FocusPoint += panMove;
            }

            Vector3 flatMove = (flatRot * Vector3.forward * v + flatRot * Vector3.right * h).normalized;
            bg3FocusPoint += flatMove * (flySpeed * speedMult) * Time.deltaTime;

            Quaternion camRot = Quaternion.Euler(55f, yaw, 0f);
            transform.rotation = camRot;
            transform.position = bg3FocusPoint - (camRot * Vector3.forward * topDownDistance);
        }

        // Coleira de raio da câmera quando na Visão do Player (atravessa paredes livremente dentro do raio)
        if (isPlayerVisionMode)
        {
            Transform refToken = GetCurrentVisionReferenceToken();
            if (refToken != null)
            {
                if (fogOfWar != null)
                {
                    fogOfWar.activeToken = refToken;
                    playerCameraLeash = Mathf.Max(8f, fogOfWar.visionRadius * 0.9f);
                }

                Vector3 center = refToken.position + Vector3.up * 1.2f;
                if (!isTopDownBG3)
                {
                    Vector3 offset = transform.position - center;
                    if (offset.magnitude > playerCameraLeash)
                        transform.position = center + offset.normalized * playerCameraLeash;
                }
                else
                {
                    Vector3 offsetXZ = bg3FocusPoint - refToken.position;
                    offsetXZ.y = 0f;
                    if (offsetXZ.magnitude > playerCameraLeash)
                        bg3FocusPoint = refToken.position + offsetXZ.normalized * playerCameraLeash;
                }
            }
        }
    }

    void HandleMouseAndTokens()
    {
        if (cam == null || Input.GetMouseButton(1)) return;

        bool isMaster = VTTNetworkManager.Instance == null || VTTNetworkManager.Instance.isMasterUnlocked;
        Ray ray = cam.ScreenPointToRay(Input.mousePosition);
        RaycastHit[] hits = Physics.RaycastAll(ray, 500f);
        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

        // Alt + Clique Esquerdo (Apenas Mestre): Abre Editor Rápido de Tags
        if (isMaster && Input.GetMouseButtonDown(0) && (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt)))
        {
            foreach (var h in hits)
            {
                if (cursor3D != null && h.transform.IsChildOf(cursor3D)) continue;
                if (h.collider.GetComponent<VTTFogOfWar>() != null) continue;
                selectedObjectForTag = h.collider.gameObject;
                showTagPopup = true;
                return;
            }
        }

        if (Input.GetMouseButtonDown(0))
        {
            foreach (var hit in hits)
            {
                VTTToken25D token = hit.collider.GetComponentInParent<VTTToken25D>();
                if (token != null)
                {
                    // Jogador só pode arrastar o próprio token (não pode arrastar o token do amigo que está assistindo)
                    if (!isMaster && myPlayerToken != null && token.transform != myPlayerToken)
                        break;

                    draggedToken = token;
                    draggedToken.StartDrag();

                    if (isMaster && fogOfWar != null)
                    {
                        fogOfWar.activeToken = token.transform;
                        myPlayerToken = token.transform;
                        viewedVisionToken = token.transform;
                    }
                    break;
                }
            }
        }

        bool foundSurface = false;
        foreach (var hit in hits)
        {
            if (cursor3D != null && hit.transform.IsChildOf(cursor3D)) continue;
            if (hit.collider.GetComponentInParent<VTTToken25D>() != null) continue;
            if (hit.collider.GetComponent<VTTFogOfWar>() != null) continue;

            if (VTTTagSystem.HasTagInHierarchy(hit.transform, VTTTagSystem.TAG_PAREDE) ||
                VTTTagSystem.HasTagInHierarchy(hit.transform, VTTTagSystem.TAG_TELHADO) ||
                VTTTagSystem.HasTagInHierarchy(hit.transform, VTTTagSystem.TAG_SEM_COLISAO))
                continue;

            if (VTTTagSystem.HasTagInHierarchy(hit.transform, VTTTagSystem.TAG_SUBIVEL) && hit.normal.y < 0.55f)
                continue;

            if (!VTTTagSystem.HasTagInHierarchy(hit.transform, VTTTagSystem.TAG_CHAO) &&
                !VTTTagSystem.HasTagInHierarchy(hit.transform, VTTTagSystem.TAG_SUBIVEL) &&
                hit.normal.y < 0.55f)
                continue;

            if (cursor3D != null)
                cursor3D.position = hit.point + Vector3.up * 0.03f;

            if (draggedToken != null && Input.GetMouseButton(0))
                draggedToken.UpdateDrag(hit.point, hit.collider);

            foundSurface = true;
            break;
        }

        if (!foundSurface && draggedToken != null && Input.GetMouseButton(0))
        {
            Plane floorPlane = new Plane(Vector3.up, new Vector3(0f, draggedToken.transform.position.y, 0f));
            if (floorPlane.Raycast(ray, out float enter))
                draggedToken.UpdateDrag(ray.GetPoint(enter), null);
        }

        if (Input.GetMouseButtonUp(0) && draggedToken != null)
        {
            draggedToken.EndDrag();
            draggedToken = null;
        }
    }

    void OnGUI()
    {
        if (IsAnyMenuBlocking()) return;

        bool isMaster = VTTNetworkManager.Instance == null || VTTNetworkManager.Instance.isMasterUnlocked;

        if (isMaster)
        {
            GUI.Box(new Rect(10, 10, 480, 72), "PAINEL RÁPIDO DO MESTRE (ESC: Menu | M: Mapas)");
            string modo = isTopDownBG3 ? "TÁTICO BG3" : $"LIVRE ({flySpeed:F0}m/s)";
            GUI.Label(new Rect(20, 30, 460, 22), $"Câmera [TAB]: {modo} | Visão Player [V]: {(isPlayerVisionMode ? "ON" : "OFF")} | Telhado [T]: {(roofHidden ? "Oculto" : "Visível")}");
            GUI.Label(new Rect(20, 50, 460, 22), "Alt + Clique: Definir Tag (Parede/Subivel/SemColisao) | Shift + Arrastar: Ignorar Parede");
        }

        if (isMaster && showTagPopup && selectedObjectForTag != null)
        {
            Rect r = new Rect(10, 90, 310, 220);
            GUI.Box(r, $"Tag de: {selectedObjectForTag.name}");
            if (GUI.Button(new Rect(20, 118, 290, 28), "Tag: Parede (Bloqueia Token e Visão)"))
                VTTTagSystem.SetTagRecursive(selectedObjectForTag, VTTTagSystem.TAG_PAREDE);
            if (GUI.Button(new Rect(20, 150, 290, 28), "Tag: Subivel (Mesas / Caixas Escaláveis)"))
                VTTTagSystem.SetTagRecursive(selectedObjectForTag, VTTTagSystem.TAG_SUBIVEL);
            if (GUI.Button(new Rect(20, 182, 290, 28), "Tag: Chao (Piso e Rampas)"))
                VTTTagSystem.SetTagRecursive(selectedObjectForTag, VTTTagSystem.TAG_CHAO);
            if (GUI.Button(new Rect(20, 214, 290, 28), "Tag: SemColisao (Trilhos / Enfeites)"))
                VTTTagSystem.SetTagRecursive(selectedObjectForTag, VTTTagSystem.TAG_SEM_COLISAO);
            if (GUI.Button(new Rect(20, 246, 290, 28), "Tag: Telhado (Ocultável na Tecla T)"))
                VTTTagSystem.SetTagRecursive(selectedObjectForTag, VTTTagSystem.TAG_TELHADO);
            if (GUI.Button(new Rect(20, 278, 290, 24), "Fechar"))
                showTagPopup = false;
        }
    }
}