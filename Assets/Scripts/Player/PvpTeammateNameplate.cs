using UnityEngine;
using UnityEngine.UI;

/// <summary>仅在本机为远端队友创建的世界空间头顶编号与玩家名。</summary>
[DisallowMultipleComponent]
public class PvpTeammateNameplate : MonoBehaviour
{
    [Tooltip("标签相对角色根节点的高度")]
    public float height = 2.15f;

    private static Font s_NameplateFont;
    private RectTransform m_NameplateRoot;
    private Camera m_LocalCamera;

    public void Initialize(int teamNumber, string displayName)
    {
        if (m_NameplateRoot != null)
            return;

        GameObject root = new GameObject(
            "__PvpTeammateNameplate",
            typeof(RectTransform),
            typeof(Canvas),
            typeof(CanvasScaler));
        root.layer = gameObject.layer;
        root.transform.SetParent(transform, false);

        m_NameplateRoot = root.GetComponent<RectTransform>();
        m_NameplateRoot.anchorMin = m_NameplateRoot.anchorMax = new Vector2(0.5f, 0.5f);
        m_NameplateRoot.pivot = new Vector2(0.5f, 0.5f);
        m_NameplateRoot.localPosition = Vector3.up * height;
        m_NameplateRoot.sizeDelta = new Vector2(320f, 64f);
        m_NameplateRoot.localScale = Vector3.one * 0.006f;

        Canvas canvas = root.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.overrideSorting = true;
        canvas.sortingOrder = 30;

        GameObject labelObject = new GameObject(
            "Label",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(Text));
        labelObject.layer = root.layer;
        labelObject.transform.SetParent(root.transform, false);

        RectTransform labelRect = labelObject.GetComponent<RectTransform>();
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = Vector2.zero;
        labelRect.offsetMax = Vector2.zero;

        Text label = labelObject.GetComponent<Text>();
        if (s_NameplateFont == null)
        {
            s_NameplateFont = Font.CreateDynamicFontFromOSFont(
                new[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "Arial" },
                34);
            if (s_NameplateFont == null)
                s_NameplateFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        }
        label.font = s_NameplateFont;
        label.text = "[" + Mathf.Clamp(teamNumber, 1, LobbyState.PvpTeamCapacity) + "] " + displayName;
        label.fontSize = 34;
        label.fontStyle = FontStyle.Bold;
        label.alignment = TextAnchor.MiddleCenter;
        label.color = new Color(0.35f, 0.9f, 1f, 1f);
        label.raycastTarget = false;
        label.horizontalOverflow = HorizontalWrapMode.Overflow;
        label.verticalOverflow = VerticalWrapMode.Overflow;

        Outline textOutline = labelObject.AddComponent<Outline>();
        textOutline.effectColor = new Color(0f, 0f, 0f, 0.95f);
        textOutline.effectDistance = new Vector2(2f, -2f);
    }

    private void LateUpdate()
    {
        if (m_NameplateRoot == null)
            return;

        if (m_LocalCamera == null)
            m_LocalCamera = Camera.main;
        if (m_LocalCamera == null)
            return;

        m_NameplateRoot.position = transform.position + Vector3.up * height;
        Vector3 lookDirection = m_NameplateRoot.position - m_LocalCamera.transform.position;
        if (lookDirection.sqrMagnitude > 0.0001f)
            m_NameplateRoot.rotation = Quaternion.LookRotation(lookDirection, m_LocalCamera.transform.up);
    }
}
