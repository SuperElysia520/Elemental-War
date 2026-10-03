using UnityEngine;

/// <summary>把账号已保存的外观同步到编辑器中摆好的菜单或大厅展示角色。</summary>
[DisallowMultipleComponent]
public sealed class AccountCharacterPreview : MonoBehaviour
{
    [Tooltip("拖入场景里现有 RifleGirl 展示角色的 RifleGirlAppearance。此组件只同步已保存外观，不创建模型、相机或界面，也不改变角色姿态。")]
    [SerializeField] private RifleGirlAppearance appearance;

    private AccountClient account;

    private void OnEnable()
    {
        if (appearance == null)
        {
            Debug.LogWarning("[AccountCharacterPreview] 请在 Inspector 中绑定场景展示角色的 RifleGirlAppearance。", this);
            return;
        }

        account = AccountClient.EnsureInstance();
        account.ProfileChanged += ApplyProfile;
        ApplyProfile(account.Profile);
    }

    private void Start()
    {
        // 再应用一次，确保其他组件的 Awake 初始化不会覆盖进入场景时的账号外观。
        if (account != null)
            ApplyProfile(account.Profile);
    }

    private void OnDisable()
    {
        if (account != null)
            account.ProfileChanged -= ApplyProfile;
        account = null;
    }

    private void ApplyProfile(PlayerProfileData profile)
    {
        if (appearance == null)
            return;

        appearance.SetLocalFirstPerson(false);
        appearance.Apply(account != null && account.IsAuthenticated && profile != null
            ? profile.appearance
            : CharacterAppearanceData.Default);
    }
}
