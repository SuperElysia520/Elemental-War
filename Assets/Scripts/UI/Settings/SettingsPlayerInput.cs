using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace FPS.Settings
{
    /// <summary>Applies saved keyboard/mouse bindings while preserving existing gamepad bindings.</summary>
    public sealed class SettingsPlayerInput : IDisposable
    {
        private readonly MyInputSystem source;
        private readonly InputAction reload = new InputAction("ConfiguredReload", InputActionType.Button);
        private readonly InputAction crouch = new InputAction("ConfiguredCrouch", InputActionType.Button);
        private readonly InputAction switchWeapon = new InputAction("ConfiguredSwitch", InputActionType.Button);
        private bool enabled, aimLatch, sprintLatch, crouchLatch;
        public bool Aim { get; private set; }
        public bool Sprint { get; private set; }
        public bool Crouch { get; private set; }
        public bool ReloadPressed => enabled && reload.WasPressedThisFrame();
        public bool SwitchPressed => enabled && switchWeapon.WasPressedThisFrame();
        public SettingsPlayerInput(MyInputSystem input)
        {
            source = input;
            reload.AddBinding("<Keyboard>/r"); crouch.AddBinding("<Keyboard>/leftCtrl"); switchWeapon.AddBinding("<Keyboard>/q");
            Apply(GameSettingsService.Current);
            GameSettingsService.Changed += Apply;
        }
        public void Enable() { enabled = true; reload.Enable(); crouch.Enable(); switchWeapon.Enable(); }
        public void Disable() { enabled = false; reload.Disable(); crouch.Disable(); switchWeapon.Disable(); Reset(); }
        public void Tick(bool active)
        {
            if (!active) { Reset(); return; }
            var data = GameSettingsService.Current;
            Aim = Mode(source.Player.IsAiming, data.aimMode, ref aimLatch);
            Sprint = Mode(source.Player.IsSprint, data.sprintMode, ref sprintLatch);
            Crouch = Mode(crouch, data.crouchMode, ref crouchLatch);
        }
        private static bool Mode(InputAction action, int mode, ref bool latch)
        {
            if (mode == 0) { latch = false; return action.IsPressed(); }
            if (action.WasPressedThisFrame()) latch = !latch;
            return latch;
        }
        public void CancelAim() { aimLatch = false; Aim = false; }
        public void CancelSprint() { sprintLatch = false; Sprint = false; }
        public void Reset() { aimLatch = sprintLatch = crouchLatch = Aim = Sprint = Crouch = false; }
        private void Apply(GameSettingsData data)
        {
            bool active = source.Player.enabled;
            source.Player.Disable();
            var move = source.Player.Move;
            for (int i = 0; i < move.bindings.Count; i++)
            {
                var b = move.bindings[i];
                if (!b.isPartOfComposite || !b.path.StartsWith("<Keyboard>", StringComparison.OrdinalIgnoreCase)) continue;
                string path = b.name == "up" ? data.kb_forward : b.name == "down" ? data.kb_back : b.name == "left" ? data.kb_left : data.kb_right;
                move.ApplyBindingOverride(i, path);
            }
            Override(source.Player.Fire, data.kb_fire);
            Override(source.Player.IsAiming, data.kb_aim);
            Override(source.Player.IsSprint, data.kb_sprint);
            Override(source.Player.IsJumping, data.kb_jump);
            reload.ApplyBindingOverride(0, data.kb_reload);
            crouch.ApplyBindingOverride(0, data.kb_crouch);
            switchWeapon.ApplyBindingOverride(0, data.kb_switch);
            Reset();
            if (active) source.Player.Enable();
        }
        private static void Override(InputAction action, string path)
        {
            for (int i = 0; i < action.bindings.Count; i++)
                if (action.bindings[i].path.StartsWith("<Keyboard>", StringComparison.OrdinalIgnoreCase) || action.bindings[i].path.StartsWith("<Mouse>", StringComparison.OrdinalIgnoreCase))
                    action.ApplyBindingOverride(i, path);
        }
        public void Dispose()
        {
            GameSettingsService.Changed -= Apply;
            reload.Dispose(); crouch.Dispose(); switchWeapon.Dispose();
        }
        public static bool Reserved(string path) => path == "<Keyboard>/escape" || path == "<Keyboard>/tab" || path == "<Keyboard>/1" || path == "<Keyboard>/2" || path == "<Keyboard>/3" || path == "<Keyboard>/v";
        public static bool ValidBinding(string path)
        {
            if (string.IsNullOrEmpty(path) || Reserved(path)) return false;
            if (path.StartsWith("<Keyboard>/", StringComparison.Ordinal))
            {
                string name = path.Substring(11);
                // Validate names against the Input System's keyboard layout, even without an attached keyboard.
                var layout = InputSystem.LoadLayout("Keyboard");
                foreach (var control in layout.controls)
                    if (string.Equals(control.name.ToString(), name, StringComparison.OrdinalIgnoreCase)) return true;
                return false;
            }
            return path == "<Mouse>/leftButton" || path == "<Mouse>/rightButton" || path == "<Mouse>/middleButton" || path == "<Mouse>/forwardButton" || path == "<Mouse>/backButton";
        }
        public static string AvailableFallback(HashSet<string> used)
        {
            foreach (char c in "abcdefghijklmnopqrstuvwxyz")
            { string path = "<Keyboard>/" + c; if (!Reserved(path) && !used.Contains(path)) return path; }
            return "<Keyboard>/f12";
        }
        public static string DisplayName(string path)
        {
            switch (path)
            {
                case "<Mouse>/leftButton": return "LMB";
                case "<Mouse>/rightButton": return "RMB";
                case "<Mouse>/middleButton": return "MMB";
                case "<Mouse>/forwardButton": return "MOUSE 4";
                case "<Mouse>/backButton": return "MOUSE 5";
                case "<Keyboard>/leftShift": return "SHIFT";
                case "<Keyboard>/rightShift": return "R SHIFT";
                case "<Keyboard>/leftCtrl": return "CTRL";
                case "<Keyboard>/rightCtrl": return "R CTRL";
                case "<Keyboard>/leftAlt": return "ALT";
                case "<Keyboard>/rightAlt": return "R ALT";
                default: int slash = (path ?? "").LastIndexOf('/'); return slash >= 0 ? path.Substring(slash + 1).ToUpperInvariant() : "—";
            }
        }
    }

    /// <summary>Changes capsule height while keeping feet planted; a blocked ceiling prevents standing.</summary>
    public sealed class SettingsCrouch
    {
        private readonly CharacterController controller;
        private readonly float standingHeight, stepOffset;
        private readonly Vector3 standingCenter;
        private readonly Collider[] overlaps = new Collider[32];
        public bool IsCrouching { get; private set; }
        public float EyeDrop => IsCrouching ? standingHeight * 0.36f : 0;
        public SettingsCrouch(CharacterController controller)
        {
            this.controller = controller;
            if (controller == null) return;
            standingHeight = controller.height; standingCenter = controller.center; stepOffset = controller.stepOffset;
        }
        public void Apply(bool desired, bool checkCeiling = true)
        {
            if (controller == null || desired == IsCrouching) return;
            if (!desired && checkCeiling && !CanStand()) return;
            IsCrouching = desired;
            float height = desired ? Mathf.Max(controller.radius * 2 + 0.01f, standingHeight * 0.6f) : standingHeight;
            controller.stepOffset = Mathf.Min(stepOffset, height * 0.45f);
            controller.height = height;
            controller.center = standingCenter - Vector3.up * (standingHeight - height) * 0.5f;
        }
        private bool CanStand()
        {
            var t = controller.transform;
            float scale = Mathf.Max(Mathf.Abs(t.lossyScale.x), Mathf.Abs(t.lossyScale.z));
            float radius = Mathf.Max(0.01f, controller.radius * scale - 0.015f);
            float half = Mathf.Max(0, standingHeight * Mathf.Abs(t.lossyScale.y) * 0.5f - radius);
            Vector3 center = t.TransformPoint(standingCenter);
            int count = Physics.OverlapCapsuleNonAlloc(center - t.up * half, center + t.up * half, radius, overlaps, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
                if (overlaps[i] != null && overlaps[i] != controller && !overlaps[i].transform.IsChildOf(t)) return false;
            return count < overlaps.Length;
        }
    }
}
