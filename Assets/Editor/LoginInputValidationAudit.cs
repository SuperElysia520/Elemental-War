using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Read-only UI and sanitized service-error audit; never stores credential values.</summary>
public static class LoginInputValidationAudit
{
    [Serializable] private sealed class Report
    {
        public bool playing, abcIsValid, registrationBindingsCorrect, errorTextClipped;
        public string scene, usernameInput, passwordInput, confirmationInput;
        public float messageWidth, messageHeight, parameterErrorPreferredHeight;
        public float serviceErrorPreferredHeight, providerErrorPreferredHeight;
        public float environmentErrorPreferredHeight;
        public float cloudCodeErrorPreferredHeight;
        public bool serviceErrorsFit;
        public int errorMappingChecks;
        public int pvpDiagnosticChecks;
        public string verticalOverflow;
        public List<string> serviceErrorCategories = new List<string>();
        public string consoleScanStatus;
        public int consoleEntryCount, authenticationFailureCount;
        public List<string> serviceErrorTitles = new List<string>();
        public List<string> serviceErrorHints = new List<string>();
    }

    [MenuItem("Tools/FPS/Login/Check Input Bindings")]
    public static void Run()
    {
        var report = new Report { playing = Application.isPlaying, scene = SceneManager.GetActiveScene().path,
            abcIsValid = UgsCredentialRules.IsValidUsername("abc") };
        report.errorMappingChecks = ValidateErrorMapping();
        report.pvpDiagnosticChecks = ValidatePvpDiagnostics();
        foreach (GameObject root in SceneManager.GetActiveScene().GetRootGameObjects())
        foreach (LoginUI ui in root.GetComponentsInChildren<LoginUI>(true))
        {
            report.usernameInput = PathOf(ui.registrationUsername);
            report.passwordInput = PathOf(ui.registrationPassword);
            report.confirmationInput = PathOf(ui.confirmPassword);
            report.registrationBindingsCorrect = ui.registrationUsername != null && ui.registrationPassword != null &&
                ui.confirmPassword != null && ui.registrationUsername != ui.loginUsername &&
                ui.registrationUsername != ui.registrationPassword && ui.registrationPassword != ui.confirmPassword &&
                ui.registrationPanel != null && ui.registrationUsername.transform.IsChildOf(ui.registrationPanel.transform) &&
                ui.registrationPassword.transform.IsChildOf(ui.registrationPanel.transform) && ui.confirmPassword.transform.IsChildOf(ui.registrationPanel.transform);
            if (ui.messageText != null)
            {
                UnityEngine.UI.Text label = ui.messageText;
                Vector2 size = label.rectTransform.rect.size;
                report.messageWidth = size.x;
                report.messageHeight = size.y;
                report.verticalOverflow = label.verticalOverflow.ToString();
                using (var generator = new TextGenerator())
                {
                    report.parameterErrorPreferredHeight = generator.GetPreferredHeight(
                        UgsCredentialRules.UsernameHelp + UgsCredentialRules.PasswordHelp, label.GetGenerationSettings(size));
                    report.errorTextClipped = !label.resizeTextForBestFit && label.verticalOverflow == VerticalWrapMode.Truncate &&
                        report.parameterErrorPreferredHeight > size.y;
                    string genericError = UgsAccountGateway.FriendlyError(Unity.Services.Authentication.AuthenticationException.Create(
                        Unity.Services.Authentication.AuthenticationErrorCodes.InvalidParameters, "synthetic"));
                    string providerError = UgsAccountGateway.FriendlyError(Unity.Services.Authentication.AuthenticationException.Create(
                        Unity.Services.Authentication.AuthenticationErrorCodes.InvalidParameters, "UsernamePassword provider is not enabled"));
                    report.serviceErrorPreferredHeight = generator.GetPreferredHeight(genericError, label.GetGenerationSettings(size));
                    report.providerErrorPreferredHeight = generator.GetPreferredHeight(providerError, label.GetGenerationSettings(size));
                    string environmentError = UgsAccountGateway.FriendlyError(Unity.Services.Authentication.AuthenticationException.Create(
                        Unity.Services.Authentication.AuthenticationErrorCodes.InvalidParameters, "Invalid environment name provided"));
                    report.environmentErrorPreferredHeight = generator.GetPreferredHeight(environmentError, label.GetGenerationSettings(size));
                    report.cloudCodeErrorPreferredHeight = generator.GetPreferredHeight(
                        UgsAccountGateway.FriendlyError(MissingScriptError()), label.GetGenerationSettings(size));
                    report.serviceErrorsFit = report.serviceErrorPreferredHeight <= size.y && report.providerErrorPreferredHeight <= size.y &&
                        report.environmentErrorPreferredHeight <= size.y && report.cloudCodeErrorPreferredHeight <= size.y;
                }
            }
        }
        // Inspect only UGS authentication failure entries. Export fixed categories, never log bodies,
        // exception text, player names, password inputs, request payloads, or authorization headers.
        Type entries = FindEditorType("UnityEditor.LogEntries");
        Type entry = FindEditorType("UnityEditor.LogEntry");
        report.consoleScanStatus = "log_api_unavailable";
        if (entries != null && entry != null)
        {
            var flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            MethodInfo countMethod = entries.GetMethod("GetCount", flags);
            MethodInfo readMethod = entries.GetMethod("GetEntryInternal", flags);
            MethodInfo begin = entries.GetMethod("StartGettingEntries", flags);
            MethodInfo end = entries.GetMethod("EndGettingEntries", flags);
            FieldInfo message = entry.GetField("message", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (countMethod != null && readMethod != null && message != null)
            {
                bool locked = false;
                try
                {
                    if (begin != null && end != null) { begin.Invoke(null, null); locked = true; }
                    int count = (int)countMethod.Invoke(null, null);
                    report.consoleEntryCount = count;
                    for (int i = Math.Max(0, count - 1000); i < count; i++)
                    {
                        object item = Activator.CreateInstance(entry);
                        readMethod.Invoke(null, new[] { (object)i, item });
                        string text = (message.GetValue(item) as string ?? "").ToLowerInvariant();
                        if (!text.Contains("authentication") ||
                            (!text.Contains("request failed") && !text.Contains("request completed with error"))) continue;
                        report.authenticationFailureCount++;
                        string category = text.Contains("invalid environment name provided") || text.Contains("category=invalid_environment") ?
                            "authentication_invalid_environment" :
                            text.Contains("category=invalid_credentials") ||
                            Regex.IsMatch(text, "\"title\"\\s*:\\s*\"wrong_username_password\"") ? "authentication_invalid_credentials" :
                            text.Contains("not enabled") || text.Contains("not configured") || text.Contains("disabled") ?
                            "authentication_provider_not_enabled_or_configured" :
                            text.Contains("invalid_parameters") ? "authentication_invalid_parameters" :
                            text.Contains("entity_exists") ? "authentication_username_taken" : "authentication_request_failed";
                        AddUnique(report.serviceErrorCategories, category);
                        // Only fixed, known protocol codes and diagnostic categories leave this method.
                        foreach (string title in new[] { "INVALID_PARAMETERS", "PERMISSION_DENIED", "ENTITY_EXISTS", "INVALID_USERNAME", "INVALID_PASSWORD", "WRONG_USERNAME_PASSWORD", "UNAUTHORIZED_REQUEST" })
                            if (Regex.IsMatch(text, "\"title\"\\s*:\\s*\"" + title.ToLowerInvariant() + "\""))
                                AddUnique(report.serviceErrorTitles, title);
                        foreach (string hint in new[] { "password", "username", "project", "environment", "provider", "disabled", "not enabled", "not configured", "requirements", "complexity", "length" })
                            if (Regex.IsMatch(text, @"\b" + Regex.Escape(hint) + @"\b"))
                                AddUnique(report.serviceErrorHints, hint);
                    }
                    report.consoleScanStatus = "read_ok";
                }
                finally { if (locked) end.Invoke(null, null); }
            }
        }
        Directory.CreateDirectory("tmp/UgsIntegration");
        File.WriteAllText("tmp/UgsIntegration/login-input-audit.json", JsonUtility.ToJson(report, true));
    }

    private static int ValidateErrorMapping()
    {
        int checks = 0;
        CheckError(Unity.Services.Authentication.AuthenticationErrorCodes.InvalidParameters, "Invalid environment name provided",
            "UGS 环境名无效，请检查环境配置。", ref checks);
        CheckError(Unity.Services.Authentication.AuthenticationErrorCodes.InvalidParameters, "INVALID ENVIRONMENT NAME PROVIDED",
            "UGS 环境名无效，请检查环境配置。", ref checks);
        CheckError(Unity.Services.Authentication.AuthenticationErrorCodes.InvalidParameters, "Access denied",
            "UGS 请求参数或服务配置无效（10002）。", ref checks);
        CheckError(Unity.Services.Authentication.AuthenticationErrorCodes.InvalidParameters, "synthetic private input must not be shown",
            "UGS 请求参数或服务配置无效（10002）。", ref checks);
        CheckError(Unity.Services.Authentication.AuthenticationErrorCodes.InvalidParameters, "UsernamePassword provider is not enabled",
            "请在 UGS 控制台启用用户名密码登录。", ref checks);
        CheckError(Unity.Services.Authentication.AuthenticationErrorCodes.InvalidProvider, "synthetic",
            "请在 UGS 控制台启用用户名密码登录。", ref checks);
        CheckError(Unity.Services.Authentication.AuthenticationErrorCodes.AccountAlreadyLinked, "synthetic",
            "该用户名已被使用，请换一个用户名，或返回登录。", ref checks);
        var missingScript = MissingScriptError();
        if (UgsAccountGateway.FriendlyError(missingScript) != "找不到云存档脚本，请检查名称与发布环境（9002）。" ||
            UgsAccountGateway.IsSessionInvalid(missingScript))
            throw new InvalidOperationException("UGS missing-script error mapping/session regression");
        checks++;
        if (UgsAccountGateway.FriendlyError(new Unity.Services.Core.RequestFailedException(9002, "synthetic")) ==
            UgsAccountGateway.FriendlyError(missingScript))
            throw new InvalidOperationException("UGS error code must be interpreted with its service type");
        checks++;
        ValidateWrongCredentialsMapping(ref checks);
        return checks;
    }

    private const string WrongCredentialsMessage = "用户名或密码不正确，请核对后重试。";

    private static void ValidateWrongCredentialsMapping(ref int checks)
    {
        const string detail = "Invalid username or password";
        const string body = "{\"title\":\"WRONG_USERNAME_PASSWORD\",\"detail\":\"Invalid username or password\",\"status\":400}";
        Exception webException;
        var converted = ConvertSdkAuthenticationFailure(body, 400, out webException);
        if (converted.GetType() != typeof(Unity.Services.Core.RequestFailedException) ||
            converted.ErrorCode != Unity.Services.Core.CommonErrorCodes.Unknown ||
            converted.Message != detail || !ReferenceEquals(converted.InnerException, webException))
            throw new InvalidOperationException("UGS SDK wrong-credentials conversion contract changed");
        checks++;
        CheckWrongCredentials(converted, true, ref checks);

        // The service title/status, rather than the numeric zero or shared detail,
        // must distinguish this observed authentication response from other failures.
        foreach (string otherBody in new[] {
            body.Replace("WRONG_USERNAME_PASSWORD", "UNRECOGNIZED_AUTH_TITLE"),
            body.Replace("WRONG_USERNAME_PASSWORD", "wrong_username_password"),
            body.Replace("\"status\":400", "\"status\":500"),
            body.Replace(",\"status\":400", ""),
            body.Replace("\"status\":400", "\"status\":\"400\"")
        })
            CheckWrongCredentials(ConvertSdkAuthenticationFailure(otherBody,
                otherBody.Contains("\"status\":500") ? 500 : 400, out webException), false, ref checks);

        var matchingInner = CreateSdkAuthenticationWebException(body, 400);
        CheckWrongCredentials(new Unity.Services.Core.RequestFailedException(0, detail), false, ref checks);
        CheckWrongCredentials(new Unity.Services.Core.RequestFailedException(0, detail, new Exception(body)), false, ref checks);
        CheckWrongCredentials(Unity.Services.Authentication.AuthenticationException.Create(
            Unity.Services.Core.CommonErrorCodes.TransportError, detail, matchingInner), false, ref checks);
        CheckWrongCredentials(Unity.Services.Authentication.AuthenticationException.Create(
            Unity.Services.Authentication.AuthenticationErrorCodes.InvalidParameters, detail, matchingInner), false, ref checks);
        CheckWrongCredentials(new Unity.Services.Core.RequestFailedException(0, detail,
            CreateSdkAuthenticationWebException("[]", 400)), false, ref checks);

        // Preserve the service boundary even when another SDK wraps an auth-shaped inner exception.
        CheckWrongCredentials((Exception)Activator.CreateInstance(typeof(Unity.Services.CloudCode.CloudCodeException),
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            new object[] { Unity.Services.CloudCode.CloudCodeExceptionReason.Unknown, 0, detail, matchingInner }, null), false, ref checks);
        CheckWrongCredentials((Exception)Activator.CreateInstance(typeof(Unity.Services.CloudSave.CloudSaveException),
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            new object[] { Unity.Services.CloudSave.CloudSaveExceptionReason.Unknown, 0, detail, matchingInner }, null), false, ref checks);
        CheckWrongCredentials((Exception)Activator.CreateInstance(typeof(Unity.Services.Authentication.AuthenticationException),
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            new object[] { 0, detail, matchingInner, null }, null), true, ref checks);
    }

    private static Unity.Services.Core.RequestFailedException ConvertSdkAuthenticationFailure(string json, long status,
        out Exception webException)
    {
        Assembly assembly = typeof(Unity.Services.Authentication.AuthenticationException).Assembly;
        webException = CreateSdkAuthenticationWebException(json, status);
        Type handlerType = assembly.GetType("Unity.Services.Authentication.AuthenticationExceptionHandler", true);
        object handler = Activator.CreateInstance(handlerType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, new object[] { null }, null);
        MethodInfo convert = handlerType.GetMethod("ConvertException", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, new[] { webException.GetType() }, null);
        if (convert == null) throw new InvalidOperationException("UGS SDK authentication converter is unavailable");
        bool loggingWasEnabled = Debug.unityLogger.logEnabled;
        try
        {
            // The real SDK converter logs the JSON response. Keep synthetic protocol
            // fixtures out of Console and restore the logger before scanning real evidence.
            Debug.unityLogger.logEnabled = false;
            return (Unity.Services.Core.RequestFailedException)convert.Invoke(handler, new object[] { webException });
        }
        catch (TargetInvocationException)
        {
            throw new InvalidOperationException("UGS SDK synthetic authentication conversion failed");
        }
        finally { Debug.unityLogger.logEnabled = loggingWasEnabled; }
    }

    private static Exception CreateSdkAuthenticationWebException(string json, long status)
    {
        Type type = typeof(Unity.Services.Authentication.AuthenticationException).Assembly.GetType(
            "Unity.Services.Authentication.WebRequestException", true);
        return (Exception)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, new object[] { false, true, false, status, json, null }, null);
    }

    private static void CheckWrongCredentials(Exception error, bool expected, ref int checks)
    {
        string actual = UgsAccountGateway.FriendlyError(error);
        if ((actual == WrongCredentialsMessage) != expected || UgsAccountGateway.IsSessionInvalid(error) ||
            actual.Contains("WRONG_USERNAME_PASSWORD") || actual.Contains("Invalid username or password") || actual.Contains("\"title\""))
            throw new InvalidOperationException("UGS wrong-credentials mapping/session regression; check=" + (checks + 1));
        checks++;
    }

    private static Unity.Services.CloudCode.CloudCodeException MissingScriptError()
    {
        // The SDK exception constructor is internal; build a synthetic instance without calling a service.
        return (Unity.Services.CloudCode.CloudCodeException)Activator.CreateInstance(
            typeof(Unity.Services.CloudCode.CloudCodeException), BindingFlags.Instance | BindingFlags.NonPublic, null,
            new object[] { Unity.Services.CloudCode.CloudCodeExceptionReason.NotFound, 9002, "synthetic", null }, null);
    }

    private static int ValidatePvpDiagnostics()
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        MethodInfo classify = typeof(AccountClient).GetMethod("ClassifyUgsPvpFailure", flags);
        MethodInfo log = typeof(AccountClient).GetMethod("LogUgsPvpFailure", flags);
        MethodInfo business = typeof(AccountClient).GetMethod("PvpBusinessError", flags);
        Type kind = typeof(AccountClient).Assembly.GetType("UgsPvpFailureKind", true);
        Type failure = typeof(AccountClient).Assembly.GetType("UgsPvpRequestException", true);
        if (classify == null || log == null || business == null)
            throw new InvalidOperationException("UGS PvP diagnostic helpers are unavailable");
        int checks = 0;
        CheckPvpFailure(classify, MissingScriptError(), "ScriptMissing", 404, ref checks);
        foreach (var test in new[] {
            new { Kind = "BridgeUnavailable", Status = 503 },
            new { Kind = "BridgeRestarted", Status = 409 },
            new { Kind = "RequestExpired", Status = 408 },
            new { Kind = "ResponseTimeout", Status = 0 }
        })
        {
            Exception error = (Exception)Activator.CreateInstance(failure,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                new object[] { Enum.Parse(kind, test.Kind), test.Status, "synthetic private PvP detail" }, null);
            CheckPvpFailure(classify, error, test.Kind, test.Status, ref checks);
        }

        ILogHandler original = Debug.unityLogger.logHandler;
        bool loggingWasEnabled = Debug.unityLogger.logEnabled;
        var capture = new PvpDiagnosticLogCapture(original);
        try
        {
            // Capture only these synthetic warnings, so the audit neither pollutes
            // the Console nor hides unrelated Editor/SDK diagnostics.
            Debug.unityLogger.logHandler = capture;
            Debug.unityLogger.logEnabled = true;
            const string privateMarker = "PRIVATE_PVP_FIXTURE";
            business.Invoke(null, new object[] { "create", new UgsPvpReply {
                ok = true, state = "done", status = 409,
                body = "{\"code\":\"" + privateMarker + "\",\"message\":\"" + privateMarker + "\"}"
            } });
            if (capture.Messages.Count != 1 || capture.Messages[0] !=
                "[UGS PvP] Request failed; operation=create; status=409; category=BusinessRejected" ||
                capture.Messages[0].Contains(privateMarker))
                throw new InvalidOperationException("UGS PvP business diagnostic privacy regression");
            checks++;
            capture.Messages.Clear();
            log.Invoke(null, new object[] { privateMarker, 503, Enum.Parse(kind, "BridgeUnavailable") });
            if (capture.Messages.Count != 0) throw new InvalidOperationException("UGS PvP operation whitelist regression");
            checks++;
            foreach (string operation in new[] { "current", "rooms" })
                log.Invoke(null, new object[] { operation, 503, Enum.Parse(kind, "BridgeUnavailable") });
            if (capture.Messages.Count != 0) throw new InvalidOperationException("UGS PvP background polling diagnostic regression");
            checks++;
            log.Invoke(null, new object[] { "create", 9999, Enum.ToObject(kind, 9999) });
            if (capture.Messages.Count != 1 || capture.Messages[0] !=
                "[UGS PvP] Request failed; operation=create; status=0; category=Unknown")
                throw new InvalidOperationException("UGS PvP diagnostic bounds regression");
            checks++;
        }
        finally
        {
            Debug.unityLogger.logHandler = original;
            Debug.unityLogger.logEnabled = loggingWasEnabled;
        }
        return checks;
    }

    private static void CheckPvpFailure(MethodInfo classify, Exception error, string expected, int status, ref int checks)
    {
        object[] args = { error, 0 };
        object actual = classify.Invoke(null, args);
        if (actual.ToString() != expected || (int)args[1] != status)
            throw new InvalidOperationException("UGS PvP diagnostic classification regression");
        checks++;
    }

    private sealed class PvpDiagnosticLogCapture : ILogHandler
    {
        internal readonly List<string> Messages = new List<string>();
        private readonly ILogHandler original;
        internal PvpDiagnosticLogCapture(ILogHandler original) => this.original = original;
        public void LogFormat(LogType logType, UnityEngine.Object context, string format, params object[] args)
        {
            string message = args != null && args.Length == 1 ? args[0] as string : null;
            if (logType == LogType.Warning && message != null && message.StartsWith("[UGS PvP] Request failed;", StringComparison.Ordinal))
            { Messages.Add(message); return; }
            original.LogFormat(logType, context, format, args);
        }
        public void LogException(Exception exception, UnityEngine.Object context) => original.LogException(exception, context);
    }

    private static void CheckError(int code, string detail, string expected, ref int checks)
    {
        string actual = UgsAccountGateway.FriendlyError(Unity.Services.Authentication.AuthenticationException.Create(code, detail));
        if (actual != expected) throw new InvalidOperationException("UGS error mapping regression; check=" + (checks + 1));
        checks++;
    }

    private static Type FindEditorType(string name)
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type type = assembly.GetType(name, false);
            if (type != null) return type;
        }
        return null;
    }

    private static void AddUnique(List<string> values, string value)
    {
        if (!values.Contains(value)) values.Add(value);
    }

    private static string PathOf(Component component)
    {
        if (component == null) return "MISSING";
        Transform item = component.transform;
        string path = item.name;
        while (item.parent != null) { item = item.parent; path = item.name + "/" + path; }
        return path;
    }
}
