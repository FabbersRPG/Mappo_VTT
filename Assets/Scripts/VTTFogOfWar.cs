using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
#if UNITY_EDITOR
using UnityEditor;
#endif

// ============================================================================
// SISTEMA GLOBAL DE TAGS DO MUNDO (Inclui a nova Tag "SemColisao")
// ============================================================================
public static class VTTTagSystem
{
    public const string TAG_PAREDE = "Parede";
    public const string TAG_SUBIVEL = "Subivel";
    public const string TAG_CHAO = "Chao";
    public const string TAG_TELHADO = "Telhado";
    public const string TAG_SEM_COLISAO = "SemColisao";

#if UNITY_EDITOR
    [InitializeOnLoadMethod]
    private static void RegisterVTTTagsInEditor()
    {
        SerializedObject tagManager = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
        SerializedProperty tagsProp = tagManager.FindProperty("tags");

        string[] requiredTags = { TAG_PAREDE, TAG_SUBIVEL, TAG_CHAO, TAG_TELHADO, TAG_SEM_COLISAO };
        bool changed = false;

        foreach (string reqTag in requiredTags)
        {
            bool found = false;
            for (int i = 0; i < tagsProp.arraySize; i++)
            {
                if (tagsProp.GetArrayElementAtIndex(i).stringValue == reqTag)
                {
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                tagsProp.InsertArrayElementAtIndex(tagsProp.arraySize);
                tagsProp.GetArrayElementAtIndex(tagsProp.arraySize - 1).stringValue = reqTag;
                changed = true;
            }
        }

        if (changed) tagManager.ApplyModifiedProperties();
    }
#endif

    public static bool HasTagInHierarchy(Transform t, string tagName)
    {
        Transform curr = t;
        while (curr != null)
        {
            if (curr.tag == tagName) return true;
            curr = curr.parent;
        }
        return false;
    }

    // Troca a Tag em Runtime e já liga/desliga o Collider automaticamente se for SemColisao
    public static void SetTagRecursive(GameObject obj, string newTag)
    {
        if (obj == null) return;
        Transform[] all = obj.GetComponentsInChildren<Transform>(true);
        foreach (Transform t in all)
        {
            t.tag = newTag;

            if (newTag == TAG_SEM_COLISAO || newTag == TAG_TELHADO)
            {
                Collider col = t.GetComponent<Collider>();
                if (col != null) Object.Destroy(col);
            }
            else
            {
                MeshFilter mf = t.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null && t.GetComponent<Collider>() == null)
                    t.gameObject.AddComponent<MeshCollider>();
            }
        }
    }
}

[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class VTTFogOfWar : MonoBehaviour
{
    [Header("Configuração da Visão do Token")]
    public Transform activeToken;
    public bool playerViewActive = false;
    [Range(3f, 80f)]
    public float visionRadius = 22f;
    public float eyeHeight = 1.2f;
    public float wallVisibleThickness = 0.25f;
    public float curtainHeight = 5.0f;
    public int rayCount = 720;
    public float outerFogRadius = 250f;

    [Header("Iluminação do Token no Modo Player")]
    public float lightIntensity = 3.5f;

    private Mesh fogMesh;
    private MeshRenderer fogRenderer;
    private Light tokenLight;
    private Light[] sceneLights;
    private Color savedAmbientLight;
    private float savedAmbientIntensity;

    private Vector3[] vertices;
    private int[] triangles;

    void Start()
    {
        Physics.queriesHitBackfaces = true;
        EnsureMapColliders();

        fogMesh = new Mesh();
        fogMesh.name = "DynamicFogMesh";
        fogMesh.MarkDynamic();
        GetComponent<MeshFilter>().mesh = fogMesh;

        fogRenderer = GetComponent<MeshRenderer>();
        Shader unlitShader = Shader.Find("Universal Render Pipeline/Unlit");
        if (unlitShader == null) unlitShader = Shader.Find("Unlit/Color");
        if (unlitShader == null) unlitShader = Shader.Find("Sprites/Default");

        Material fogMat = new Material(unlitShader);
        fogMat.color = Color.black;
        if (fogMat.HasProperty("_BaseColor")) fogMat.SetColor("_BaseColor", Color.black);
        fogRenderer.material = fogMat;
        fogRenderer.shadowCastingMode = ShadowCastingMode.Off;

        Collider col = GetComponent<Collider>();
        if (col != null) Destroy(col);

        GameObject lightObj = new GameObject("TokenVisionLight");
        lightObj.transform.SetParent(transform);
        tokenLight = lightObj.AddComponent<Light>();
        tokenLight.type = LightType.Point;
        tokenLight.range = visionRadius * 1.35f;
        tokenLight.intensity = lightIntensity;
        tokenLight.shadows = LightShadows.Soft;
        tokenLight.enabled = false;

        savedAmbientLight = RenderSettings.ambientLight;
        savedAmbientIntensity = RenderSettings.ambientIntensity;
        sceneLights = FindObjectsByType<Light>(FindObjectsSortMode.None);

        InitMeshArrays();
    }

    public void EnsureMapColliders()
    {
        MeshFilter[] allMeshes = FindObjectsByType<MeshFilter>(FindObjectsSortMode.None);
        foreach (MeshFilter mf in allMeshes)
        {
            if (mf.gameObject == gameObject || mf.sharedMesh == null) continue;
            if (mf.GetComponentInParent<VTTToken25D>() != null) continue;

            // Se tiver a Tag "SemColisao" (ex: Trilhos) ou "Telhado", remove qualquer colisor existente!
            if (VTTTagSystem.HasTagInHierarchy(mf.transform, VTTTagSystem.TAG_SEM_COLISAO) ||
                VTTTagSystem.HasTagInHierarchy(mf.transform, VTTTagSystem.TAG_TELHADO) ||
                mf.gameObject.name.ToLower().Contains("cursor"))
            {
                Collider existingCol = mf.GetComponent<Collider>();
                if (existingCol != null) Destroy(existingCol);
                continue;
            }

            if (mf.GetComponent<Collider>() == null)
                mf.gameObject.AddComponent<MeshCollider>();
        }
    }

    void InitMeshArrays()
    {
        vertices = new Vector3[rayCount * 3];
        triangles = new int[rayCount * 12 * 3];
    }

    void Update()
    {
        if (Input.GetKey(KeyCode.RightBracket) || Input.GetKey(KeyCode.Equals) || Input.GetKey(KeyCode.KeypadPlus))
            visionRadius = Mathf.Clamp(visionRadius + 10f * Time.deltaTime, 3f, 80f);

        if (Input.GetKey(KeyCode.LeftBracket) || Input.GetKey(KeyCode.Minus) || Input.GetKey(KeyCode.KeypadMinus))
            visionRadius = Mathf.Clamp(visionRadius - 10f * Time.deltaTime, 3f, 80f);
    }

    void LateUpdate()
    {
        if (activeToken == null)
        {
            VTTToken25D firstToken = FindFirstObjectByType<VTTToken25D>();
            if (firstToken != null) activeToken = firstToken.transform;
        }

        if (vertices == null || vertices.Length != rayCount * 3)
            InitMeshArrays();

        fogRenderer.enabled = playerViewActive && (activeToken != null);
        tokenLight.enabled = playerViewActive && (activeToken != null);

        if (!playerViewActive || activeToken == null)
            return;

        UpdateFogGeometry();
        UpdateLightsAndTokensVisibility();
    }

    public void SetPlayerView(bool enabled)
    {
        playerViewActive = enabled;
        EnsureMapColliders();
        sceneLights = FindObjectsByType<Light>(FindObjectsSortMode.None);

        if (playerViewActive)
        {
            RenderSettings.ambientLight = Color.black;
            RenderSettings.ambientIntensity = 0f;
            if (Camera.main != null)
            {
                Camera.main.clearFlags = CameraClearFlags.SolidColor;
                Camera.main.backgroundColor = Color.black;
            }
        }
        else
        {
            RenderSettings.ambientLight = savedAmbientLight;
            RenderSettings.ambientIntensity = savedAmbientIntensity;
            foreach (var l in sceneLights)
            {
                if (l != null && l != tokenLight) l.enabled = true;
            }
            VTTToken25D[] allTokens = FindObjectsByType<VTTToken25D>(FindObjectsSortMode.None);
            foreach (var t in allTokens) t.SetVisibleToPlayer(true);
        }
    }

    void UpdateFogGeometry()
    {
        transform.position = Vector3.zero;
        transform.rotation = Quaternion.identity;

        Vector3 origin = activeToken.position + Vector3.up * eyeHeight;
        tokenLight.transform.position = origin + Vector3.up * 0.5f;
        tokenLight.range = visionRadius * 1.35f;
        tokenLight.intensity = lightIntensity;

        float floorY = activeToken.position.y - 1.5f;
        float topY = activeToken.position.y + curtainHeight;

        float angleStep = 360f / rayCount;

        for (int i = 0; i < rayCount; i++)
        {
            float angle = i * angleStep * Mathf.Deg2Rad;
            Vector3 dir = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));

            float dist = visionRadius;
            RaycastHit[] hits = Physics.RaycastAll(origin, dir, visionRadius);
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

            foreach (var h in hits)
            {
                if (h.collider.transform == transform || h.collider.GetComponentInParent<VTTToken25D>() != null)
                    continue;

                if (!VTTTagSystem.HasTagInHierarchy(h.collider.transform, VTTTagSystem.TAG_PAREDE))
                    continue;

                dist = Mathf.Min(visionRadius, h.distance + wallVisibleThickness);
                break;
            }

            Vector3 boundaryXZ = activeToken.position + dir * dist;
            Vector3 outerXZ = activeToken.position + dir * outerFogRadius;

            vertices[i] = new Vector3(boundaryXZ.x, floorY, boundaryXZ.z);
            vertices[i + rayCount] = new Vector3(boundaryXZ.x, topY, boundaryXZ.z);
            vertices[i + rayCount * 2] = new Vector3(outerXZ.x, topY, outerXZ.z);
        }

        int t = 0;
        for (int i = 0; i < rayCount; i++)
        {
            int next = (i + 1) % rayCount;

            int b0 = i;
            int b1 = next;
            int m0 = i + rayCount;
            int m1 = next + rayCount;
            int o0 = i + rayCount * 2;
            int o1 = next + rayCount * 2;

            triangles[t++] = b0; triangles[t++] = m0; triangles[t++] = m1;
            triangles[t++] = b0; triangles[t++] = m1; triangles[t++] = b1;
            triangles[t++] = b0; triangles[t++] = m1; triangles[t++] = m0;
            triangles[t++] = b0; triangles[t++] = b1; triangles[t++] = m1;

            triangles[t++] = m0; triangles[t++] = o0; triangles[t++] = o1;
            triangles[t++] = m0; triangles[t++] = o1; triangles[t++] = m1;
            triangles[t++] = m0; triangles[t++] = o1; triangles[t++] = o0;
            triangles[t++] = m0; triangles[t++] = m1; triangles[t++] = o1;
        }

        fogMesh.Clear();
        fogMesh.vertices = vertices;
        fogMesh.triangles = triangles;
        fogMesh.RecalculateBounds();
    }

    void UpdateLightsAndTokensVisibility()
    {
        Vector3 origin = activeToken.position + Vector3.up * eyeHeight;

        foreach (var l in sceneLights)
        {
            if (l == null || l == tokenLight || l.type == LightType.Directional) continue;

            float dist = Vector3.Distance(origin, l.transform.position);
            if (dist > visionRadius * 1.4f)
            {
                l.enabled = false;
                continue;
            }

            bool blocked = false;
            RaycastHit[] hits = Physics.RaycastAll(origin, (l.transform.position - origin).normalized, dist);
            foreach (var h in hits)
            {
                if (VTTTagSystem.HasTagInHierarchy(h.collider.transform, VTTTagSystem.TAG_PAREDE))
                {
                    blocked = true;
                    break;
                }
            }
            l.enabled = !blocked;
        }

        VTTToken25D[] allTokens = FindObjectsByType<VTTToken25D>(FindObjectsSortMode.None);
        foreach (var t in allTokens)
        {
            if (t.transform == activeToken)
            {
                t.SetVisibleToPlayer(true);
                continue;
            }

            Vector3 targetPos = t.transform.position + Vector3.up * eyeHeight;
            float dist = Vector3.Distance(origin, targetPos);
            if (dist > visionRadius)
            {
                t.SetVisibleToPlayer(false);
                continue;
            }

            bool blocked = false;
            RaycastHit[] hits = Physics.RaycastAll(origin, (targetPos - origin).normalized, dist);
            foreach (var h in hits)
            {
                if (VTTTagSystem.HasTagInHierarchy(h.collider.transform, VTTTagSystem.TAG_PAREDE))
                {
                    blocked = true;
                    break;
                }
            }
            t.SetVisibleToPlayer(!blocked);
        }
    }
}