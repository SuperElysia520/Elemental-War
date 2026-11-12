using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class InitialNameSetup
{
    private const string Prefab = "Assets/Resource/UI/Prefab/Mvc/InitialNamePanel.prefab";
    private const string Login = "Assets/Scenes/Login.unity";
    private const string Output = "tmp/NameRules20261112";

    [MenuItem("Tools/FPS/Login/Bind Initial Name Panel")]
    public static void Apply()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Exit Play Mode before binding the initial name panel.");
        Directory.CreateDirectory(Output);
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            var open = SceneManager.GetSceneAt(i);
            string path = Output + "/" + open.name + ".open.before.unity";
            if (!File.Exists(path) && !EditorSceneManager.SaveScene(open, path, true)) throw new IOException("Could not back up " + open.name);
        }
        if (!File.Exists(Output + "/InitialNamePanel.before.prefab")) File.Copy(Prefab, Output + "/InitialNamePanel.before.prefab");
        if (!File.Exists(Output + "/Login.disk.before.unity")) File.Copy(Login, Output + "/Login.disk.before.unity");
        var contents = PrefabUtility.LoadPrefabContents(Prefab);
        try
        {
            var view = contents.GetComponent<InitialNamePanelView>() ?? contents.AddComponent<InitialNamePanelView>();
            view.nameInput = contents.transform.Find("bc/NameBc/NameInput").GetComponent<UnityEngine.UI.InputField>();
            view.confirmButton = contents.transform.Find("bc/NameBc/Confirm").GetComponent<UnityEngine.UI.Button>();
            view.nameInput.characterLimit = DisplayNameRules.MaxLength;
            view.nameInput.lineType = UnityEngine.UI.InputField.LineType.SingleLine;
            view.nameInput.contentType = UnityEngine.UI.InputField.ContentType.Standard;
            view.nameInput.textComponent.supportRichText = false;
            // Status is the author's static rule hint, not an output for login state.
            var hint = contents.transform.Find("bc/NameBc/Status")?.GetComponent<UnityEngine.UI.Text>();
            if (hint != null) hint.raycastTarget = false;
            PrefabUtility.SaveAsPrefabAsset(contents, Prefab);
        }
        finally { PrefabUtility.UnloadPrefabContents(contents); }

        const string profilePath = "Assets/Resource/UI/Prefab/Mvc/ProfilePanel.prefab";
        if (!File.Exists(Output + "/ProfilePanel.before.prefab")) File.Copy(profilePath, Output + "/ProfilePanel.before.prefab");
        var profileContents = PrefabUtility.LoadPrefabContents(profilePath);
        try
        {
            var profile = profileContents.GetComponent<FPS.UI.ProfilePanelView>();
            profile.nameInput.characterLimit = DisplayNameRules.MaxLength;
            profile.nameInput.textComponent.supportRichText = false;
            PrefabUtility.SaveAsPrefabAsset(profileContents, profilePath);
        }
        finally { PrefabUtility.UnloadPrefabContents(profileContents); }

        var active = SceneManager.GetActiveScene();
        var login = SceneManager.GetSceneByPath(Login);
        bool opened = !login.isLoaded;
        if (opened) login = EditorSceneManager.OpenScene(Login, OpenSceneMode.Additive);
        try
        {
            var ui = login.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<LoginUI>(true)).Single();
            var instance = login.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<InitialNamePanelView>(true)).SingleOrDefault();
            if (instance == null)
            {
                var go = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(Prefab), login);
                go.transform.SetParent(ui.loadingPanel.transform.parent, false);
                go.transform.SetAsLastSibling(); instance = go.GetComponent<InitialNamePanelView>();
            }
            ui.initialNamePanel = instance; instance.gameObject.SetActive(false);
            EditorUtility.SetDirty(ui); EditorSceneManager.MarkSceneDirty(login);
            if (!EditorSceneManager.SaveScene(login)) throw new IOException("Could not save Login bindings.");
        }
        finally
        {
            if (opened) EditorSceneManager.CloseScene(login, true);
            if (active.isLoaded) SceneManager.SetActiveScene(active);
        }
        File.WriteAllText(Output + "/setup.json", "{\"success\":true,\"scene\":\"Login\"}");
    }
}
