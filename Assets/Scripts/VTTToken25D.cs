using UnityEngine;

public class VTTToken25D : MonoBehaviour
{
    [Header("Identificação e Perfil")]
    public string profileId = "";
    public string characterName = "Investigador";
    public Texture2D tokenTexture;
    public Color baseColor = Color.cyan;
    public float tokenHeightMeters = 1.75f;

    [Header("Movimentação e Escalada")]
    public float maxClimbStepHeight = 1.6f;

    private Vector3 dragStartPos;
    private bool isDragging = false;
    private float currentDistanceMeters = 0f;
    private LineRenderer lineRenderer;
    private Transform artQuad;
    private Transform baseDisc;
    private Renderer[] allRenderers;

    void Awake()
    {
        EnsureHierarchyAndComponents();
    }

    void Start()
    {
        EnsureHierarchyAndComponents();
        ApplyVisuals(characterName, tokenTexture, baseColor);
    }

    private void EnsureHierarchyAndComponents()
    {
        // Garante que o objeto raiz tenha escala (1,1,1) para não distorcer a rotação 2.5D
        if (transform.localScale.y < 0.5f)
        {
            MeshFilter rootMf = GetComponent<MeshFilter>();
            MeshRenderer rootMr = GetComponent<MeshRenderer>();
            if (rootMf != null) Destroy(rootMf);
            if (rootMr != null) Destroy(rootMr);
            transform.localScale = Vector3.one;
        }

        baseDisc = transform.Find("Base_Pedestal");
        if (baseDisc == null)
        {
            GameObject disc = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            disc.name = "Base_Pedestal";
            disc.transform.SetParent(transform, false);
            disc.transform.localPosition = new Vector3(0f, 0.04f, 0f);
            disc.transform.localScale = new Vector3(1.05f, 0.04f, 1.05f);
            baseDisc = disc.transform;
        }

        artQuad = transform.Find("Arte_PNG_ArrasteAqui");
        if (artQuad == null)
        {
            GameObject quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = "Arte_PNG_ArrasteAqui";
            quad.transform.SetParent(transform, false);
            artQuad = quad.transform;
        }

        BoxCollider rootCol = GetComponent<BoxCollider>();
        if (rootCol == null) rootCol = gameObject.AddComponent<BoxCollider>();
        rootCol.center = new Vector3(0f, tokenHeightMeters * 0.5f, 0f);
        rootCol.size = new Vector3(1.05f, tokenHeightMeters, 1.05f);

        if (artQuad.GetComponent<Collider>() == null)
            artQuad.gameObject.AddComponent<BoxCollider>();

        allRenderers = GetComponentsInChildren<Renderer>(true);

        lineRenderer = gameObject.GetComponent<LineRenderer>();
        if (lineRenderer == null) lineRenderer = gameObject.AddComponent<LineRenderer>();

        lineRenderer.startWidth = 0.08f;
        lineRenderer.endWidth = 0.08f;
        lineRenderer.material = new Material(Shader.Find("Sprites/Default"));
        lineRenderer.startColor = Color.yellow;
        lineRenderer.endColor = Color.red;
        lineRenderer.positionCount = 2;
        lineRenderer.enabled = false;
    }

    public void ApplyVisuals(string charName, Texture2D tex, Color col)
    {
        EnsureHierarchyAndComponents();
        characterName = charName;
        tokenTexture = tex;
        baseColor = col;

        if (baseDisc != null)
        {
            MeshRenderer baseMr = baseDisc.GetComponent<MeshRenderer>();
            if (baseMr != null)
            {
                Material bMat = new Material(Shader.Find("Sprites/Default"));
                bMat.color = new Color(baseColor.r, baseColor.g, baseColor.b, 0.92f);
                baseMr.sharedMaterial = bMat;
            }
        }

        if (artQuad != null)
        {
            float aspect = 1f;
            if (tokenTexture != null && tokenTexture.height > 0)
            {
                aspect = Mathf.Clamp((float)tokenTexture.width / tokenTexture.height, 0.4f, 2.2f);
            }

            float h = tokenHeightMeters;
            float w = h * aspect;
            artQuad.localScale = new Vector3(w, h, 1f);
            artQuad.localPosition = new Vector3(0f, (h * 0.5f) + 0.06f, 0f);

            MeshRenderer artMr = artQuad.GetComponent<MeshRenderer>();
            if (artMr != null)
            {
                Material aMat = new Material(Shader.Find("Sprites/Default"));
                if (tokenTexture != null)
                {
                    aMat.mainTexture = tokenTexture;
                    aMat.color = Color.white;
                }
                else
                {
                    aMat.color = baseColor;
                }
                artMr.sharedMaterial = aMat;
            }
        }

        allRenderers = GetComponentsInChildren<Renderer>(true);
    }

    public void SetVisibleToPlayer(bool visible)
    {
        if (allRenderers == null) allRenderers = GetComponentsInChildren<Renderer>(true);
        foreach (var r in allRenderers)
        {
            if (r != null && r != lineRenderer) r.enabled = visible;
        }
    }

    void LateUpdate()
    {
        if (artQuad != null && Camera.main != null)
        {
            Vector3 lookDir = Camera.main.transform.position - artQuad.position;
            lookDir.y = 0f;
            if (lookDir.sqrMagnitude > 0.001f)
                artQuad.rotation = Quaternion.LookRotation(-lookDir);
        }
    }

    public void StartDrag()
    {
        isDragging = true;
        dragStartPos = transform.position;
        lineRenderer.enabled = true;
        lineRenderer.SetPosition(0, dragStartPos + Vector3.up * 0.15f);
        lineRenderer.SetPosition(1, dragStartPos + Vector3.up * 0.15f);
    }

    public void UpdateDrag(Vector3 targetWorldPos, Collider hitSurfaceCollider = null)
    {
        Vector3 currentPos = transform.position;
        float targetY = currentPos.y;

        if (hitSurfaceCollider != null)
        {
            bool isClimbable = VTTTagSystem.HasTagInHierarchy(hitSurfaceCollider.transform, VTTTagSystem.TAG_SUBIVEL);
            bool isFloor = VTTTagSystem.HasTagInHierarchy(hitSurfaceCollider.transform, VTTTagSystem.TAG_CHAO);

            if (isClimbable)
            {
                float stepDiff = (targetWorldPos.y + 0.02f) - currentPos.y;
                if (stepDiff <= maxClimbStepHeight)
                    targetY = targetWorldPos.y + 0.02f;
            }
            else if (isFloor || !VTTTagSystem.HasTagInHierarchy(hitSurfaceCollider.transform, VTTTagSystem.TAG_PAREDE))
            {
                if (Mathf.Abs((targetWorldPos.y + 0.02f) - currentPos.y) <= maxClimbStepHeight)
                    targetY = targetWorldPos.y + 0.02f;
            }
        }

        Vector3 desiredXZ = new Vector3(targetWorldPos.x, targetY, targetWorldPos.z);

        bool ignoreWalls = Input.GetKey(KeyCode.LeftShift) &&
                           (VTTNetworkManager.Instance == null || VTTNetworkManager.Instance.isMasterUnlocked);

        if (!ignoreWalls)
        {
            Vector3 chestOrigin = new Vector3(currentPos.x, currentPos.y + 0.9f, currentPos.z);
            Vector3 chestTarget = new Vector3(desiredXZ.x, targetY + 0.9f, desiredXZ.z);
            Vector3 moveVec = chestTarget - chestOrigin;
            float moveDist = moveVec.magnitude;

            if (moveDist > 0.001f)
            {
                Vector3 moveDir = moveVec / moveDist;
                RaycastHit[] hits = Physics.RaycastAll(chestOrigin, moveDir, moveDist + 0.35f);
                System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

                foreach (var h in hits)
                {
                    if (h.collider.transform.IsChildOf(transform)) continue;

                    bool isWall = VTTTagSystem.HasTagInHierarchy(h.collider.transform, VTTTagSystem.TAG_PAREDE);
                    bool isSubivel = VTTTagSystem.HasTagInHierarchy(h.collider.transform, VTTTagSystem.TAG_SUBIVEL);

                    if (isSubivel && hitSurfaceCollider != null && h.collider == hitSurfaceCollider)
                        continue;

                    if (isWall || isSubivel)
                    {
                        float safeDist = Mathf.Max(0f, h.distance - 0.38f);
                        Vector3 stoppedPos = chestOrigin + moveDir * safeDist;

                        Vector3 remaining = chestTarget - stoppedPos;
                        Vector3 slideNormal = new Vector3(h.normal.x, 0f, h.normal.z).normalized;
                        Vector3 slideVec = Vector3.ProjectOnPlane(remaining, slideNormal);

                        bool slideBlocked = false;
                        if (slideVec.magnitude > 0.001f)
                        {
                            RaycastHit[] slideHits = Physics.RaycastAll(stoppedPos, slideVec.normalized, slideVec.magnitude + 0.35f);
                            foreach (var sh in slideHits)
                            {
                                if (sh.collider.transform.IsChildOf(transform)) continue;
                                if (VTTTagSystem.HasTagInHierarchy(sh.collider.transform, VTTTagSystem.TAG_PAREDE) ||
                                    VTTTagSystem.HasTagInHierarchy(sh.collider.transform, VTTTagSystem.TAG_SUBIVEL))
                                {
                                    slideBlocked = true;
                                    break;
                                }
                            }
                        }

                        Vector3 finalChest = slideBlocked ? stoppedPos : (stoppedPos + slideVec);
                        desiredXZ = new Vector3(finalChest.x, targetY, finalChest.z);
                        break;
                    }
                }
            }
        }

        transform.position = desiredXZ;
        lineRenderer.SetPosition(1, transform.position + Vector3.up * 0.15f);

        currentDistanceMeters = Vector3.Distance(
            new Vector3(dragStartPos.x, 0f, dragStartPos.z),
            new Vector3(transform.position.x, 0f, transform.position.z)
        );

        if (VTTNetworkManager.Instance != null)
            VTTNetworkManager.Instance.NotifyTokenMoved(this);
    }

    public void EndDrag()
    {
        isDragging = false;
        lineRenderer.enabled = false;
        if (VTTNetworkManager.Instance != null)
            VTTNetworkManager.Instance.NotifyTokenMoved(this);
    }

    void OnGUI()
    {
        if (Camera.main == null) return;
        if (VTTNetworkManager.Instance != null && VTTNetworkManager.Instance.IsMenuOpen()) return;

        Vector3 screenPos = Camera.main.WorldToScreenPoint(transform.position + Vector3.up * (tokenHeightMeters + 0.35f));
        if (screenPos.z <= 0) return;

        if (isDragging)
        {
            GUIStyle style = new GUIStyle(GUI.skin.box)
            {
                fontSize = 15,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter
            };
            style.normal.textColor = Color.yellow;
            GUI.Box(new Rect(screenPos.x - 55, Screen.height - screenPos.y - 32, 110, 28), $"{currentDistanceMeters:F1} m", style);
        }
        else if (!string.IsNullOrEmpty(characterName))
        {
            GUIStyle bgStyle = new GUIStyle(GUI.skin.box)
            {
                fontSize = 13,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter
            };
            bgStyle.normal.textColor = Color.white;
            GUI.Box(new Rect(screenPos.x - 70, Screen.height - screenPos.y - 24, 140, 24), characterName, bgStyle);
        }
    }
}