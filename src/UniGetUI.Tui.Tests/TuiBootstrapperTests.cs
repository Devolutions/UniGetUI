using NUnit.Framework;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Tui.Infrastructure;
using NAssert = NUnit.Framework.Assert;

namespace UniGetUI.Tui.Tests;

[TestFixture, NonParallelizable]
public sealed class TuiBootstrapperTests
{
    [TestCase(false, "http://proxy.invalid:8123", false)]
    [TestCase(true, "", false)]
    [TestCase(true, "http://proxy.invalid:8123", false)]
    [TestCase(true, "https://proxy.invalid:8124/path", false)]
    [TestCase(true, "http://proxy.invalid:8123", true)]
    public void ApplyProxySettings_SetsOrClearsBothVariablesWithoutTouchingUnrelatedState(
        bool enabled, string url, bool authWithoutUsername)
    {
        var environment = new[] { "HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY", "UNIGETUI_REGRESSION_UNRELATED" }
            .ToDictionary(key => key, Environment.GetEnvironmentVariable);
        bool oldEnabled = Settings.Get(Settings.K.EnableProxy);
        bool oldAuth = Settings.Get(Settings.K.EnableProxyAuth);
        string oldUrl = Settings.GetValue(Settings.K.ProxyURL);
        string oldUsername = Settings.GetValue(Settings.K.ProxyUsername);
        string oldPreference = Settings.GetValue(Settings.K.WinGetCliToolPreference);
        try
        {
            Settings.Set(Settings.K.EnableProxy, enabled);
            Settings.Set(Settings.K.EnableProxyAuth, authWithoutUsername);
            Settings.SetValue(Settings.K.ProxyURL, url);
            // Empty username takes the approved no-credential path: never accesses PasswordVault.
            Settings.SetValue(Settings.K.ProxyUsername, "");
            Environment.SetEnvironmentVariable("HTTP_PROXY", "http://inherited-http.invalid:80");
            Environment.SetEnvironmentVariable("HTTPS_PROXY", "http://inherited-https.invalid:443");
            Environment.SetEnvironmentVariable("NO_PROXY", "localhost,.internal.invalid");
            Environment.SetEnvironmentVariable("UNIGETUI_REGRESSION_UNRELATED", "preserve-5447");

            TuiBootstrapper.ApplyProxySettingsToProcess();

            string expected = enabled && url.Length > 0 ? new Uri(url).ToString() : "";
            NAssert.That(Environment.GetEnvironmentVariable("HTTP_PROXY"), Is.EqualTo(expected));
            NAssert.That(Environment.GetEnvironmentVariable("HTTPS_PROXY"), Is.EqualTo(expected));
            NAssert.That(Environment.GetEnvironmentVariable("NO_PROXY"), Is.EqualTo("localhost,.internal.invalid"));
            NAssert.That(Environment.GetEnvironmentVariable("UNIGETUI_REGRESSION_UNRELATED"), Is.EqualTo("preserve-5447"));
            NAssert.That(Settings.GetValue(Settings.K.WinGetCliToolPreference), Is.EqualTo(oldPreference));
            NAssert.That(Settings.GetValue(Settings.K.ProxyURL), Is.EqualTo(url));
            NAssert.That(Settings.Get(Settings.K.EnableProxyAuth), Is.EqualTo(authWithoutUsername));
        }
        finally
        {
            foreach (var (key, value) in environment) Environment.SetEnvironmentVariable(key, value);
            Settings.Set(Settings.K.EnableProxy, oldEnabled);
            Settings.Set(Settings.K.EnableProxyAuth, oldAuth);
            Settings.SetValue(Settings.K.ProxyURL, oldUrl);
            Settings.SetValue(Settings.K.ProxyUsername, oldUsername);
        }
    }
}
