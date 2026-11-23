using UnityEngine;

namespace FPS.Settings
{
    /// <summary>Legacy uGUI dropdown with explicit open state for the existing Escape/back handling.</summary>
    [AddComponentMenu("UI/Settings/Dropdown (Legacy)")]
    public sealed class SettingsDropdown : UnityEngine.UI.Dropdown
    {
        private GameObject openList;
        public bool IsExpanded => openList != null;

        protected override GameObject CreateDropdownList(GameObject template)
        { openList = base.CreateDropdownList(template); return openList; }

        protected override void DestroyDropdownList(GameObject list)
        { if(openList==list)openList=null; base.DestroyDropdownList(list); }
    }
}
