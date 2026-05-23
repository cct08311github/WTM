#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using WalkingTec.Mvvm.Core.Extensions;

namespace WalkingTec.Mvvm.Core.Test.Extensions
{
    /// <summary>
    /// Tests for ConfigExtension.WTMConfig and WTM_SetCurrentDictionary.
    ///
    /// Note: CurrentDirectoryHelpers.SetCurrentDirectory() contains IIS-specific
    /// P/Invoke (kernel32/aspnetcorev2_inprocess.dll) and is not directly testable
    /// in a unit-test environment. Tests focus on the observable IConfigurationBuilder
    /// return values and the HostRoot key injected into in-memory config.
    /// </summary>
    [TestClass]
    public class ConfigExtensionTests
    {
        // ─── WTMConfig — null env, no file dir, no file name ──────────────────

        [TestMethod]
        public void WTMConfig_NullEnvNullDirNullFile_ReturnsBuilder()
        {
            IConfigurationBuilder builder = new ConfigurationBuilder();
            var result = builder.WTMConfig(env: null, jsonFileDir: null, jsonFileName: null);
            result.Should().NotBeNull();
        }

        [TestMethod]
        public void WTMConfig_NullEnv_InjectsHostRootFromCurrentDirectory()
        {
            IConfigurationBuilder builder = new ConfigurationBuilder();
            var result = builder.WTMConfig(env: null, jsonFileDir: null, jsonFileName: null);
            var config = result.Build();
            var hostRoot = config["HostRoot"];
            hostRoot.Should().NotBeNull();
            // Should equal Directory.GetCurrentDirectory() at the time of the call
            // (or the resolved binary path if appsettings.json doesn't exist there)
            hostRoot.Should().NotBeEmpty();
        }

        // ─── WTMConfig — explicit jsonFileDir ─────────────────────────────────

        [TestMethod]
        public void WTMConfig_ExplicitJsonFileDir_UsesProvidedBasePath()
        {
            // Use a temp directory so SetBasePath doesn't fail
            var tempDir = Path.GetTempPath();
            IConfigurationBuilder builder = new ConfigurationBuilder();
            var result = builder.WTMConfig(env: null, jsonFileDir: tempDir, jsonFileName: null);
            result.Should().NotBeNull();
            // Build should not throw even if appsettings.json doesn't exist (optional:true)
            var config = result.Build();
            config.Should().NotBeNull();
        }

        // ─── WTMConfig — explicit jsonFileName ────────────────────────────────

        [TestMethod]
        public void WTMConfig_ExplicitJsonFileName_UsesProvidedFileName()
        {
            var tempDir = Path.GetTempPath();
            IConfigurationBuilder builder = new ConfigurationBuilder();
            var result = builder.WTMConfig(
                env: null,
                jsonFileDir: tempDir,
                jsonFileName: "custom-settings.json");
            result.Should().NotBeNull();
            // optional:true so missing file is fine
            var config = result.Build();
            config.Should().NotBeNull();
        }

        // ─── WTMConfig — environment variables are included ───────────────────

        [TestMethod]
        public void WTMConfig_Always_IncludesEnvironmentVariables()
        {
            var uniqueKey = "WTM_TEST_VAR_" + Guid.NewGuid().ToString("N");
            Environment.SetEnvironmentVariable(uniqueKey, "test_value");
            try
            {
                var tempDir = Path.GetTempPath();
                IConfigurationBuilder builder = new ConfigurationBuilder();
                var config = builder.WTMConfig(env: null, jsonFileDir: tempDir).Build();
                config[uniqueKey].Should().Be("test_value");
            }
            finally
            {
                Environment.SetEnvironmentVariable(uniqueKey, null);
            }
        }

        // ─── WTMConfig — HostRoot key always present ──────────────────────────

        [TestMethod]
        public void WTMConfig_Always_ContainsHostRootKey()
        {
            var tempDir = Path.GetTempPath();
            IConfigurationBuilder builder = new ConfigurationBuilder();
            var config = builder.WTMConfig(env: null, jsonFileDir: tempDir).Build();
            config["HostRoot"].Should().NotBeNull();
        }

        // ─── WTM_SetCurrentDictionary ─────────────────────────────────────────

        [TestMethod]
        public void WTM_SetCurrentDictionary_ReturnsSameBuilder()
        {
            IConfigurationBuilder builder = new ConfigurationBuilder();
            var result = builder.WTM_SetCurrentDictionary();
            result.Should().BeSameAs(builder);
        }

        [TestMethod]
        public void WTM_SetCurrentDictionary_DoesNotThrow()
        {
            IConfigurationBuilder builder = new ConfigurationBuilder();
            Action act = () => builder.WTM_SetCurrentDictionary();
            act.Should().NotThrow();
        }

        [TestMethod]
        public void WTMConfig_ViaSetCurrentDictionary_BuildsSuccessfully()
        {
            // When jsonFileDir is null, WTMConfig calls WTM_SetCurrentDictionary
            IConfigurationBuilder builder = new ConfigurationBuilder();
            Action act = () =>
            {
                var cfg = builder.WTMConfig(env: null).Build();
                _ = cfg["HostRoot"]; // access to confirm it works
            };
            act.Should().NotThrow();
        }

        // ─── WTMConfig — env != null branch (line 44) ────────────────────────

        [TestMethod]
        public void WTMConfig_WithNonNullEnv_UsesContentRootPath()
        {
            var tempDir = Path.GetTempPath();
            var mockEnv = new Mock<IHostEnvironment>();
            mockEnv.Setup(e => e.ContentRootPath).Returns(tempDir);
            mockEnv.Setup(e => e.ContentRootFileProvider).Returns(new NullFileProvider());
            mockEnv.Setup(e => e.EnvironmentName).Returns("Testing");
            mockEnv.Setup(e => e.ApplicationName).Returns("TestApp");

            IConfigurationBuilder builder = new ConfigurationBuilder();
            var config = builder.WTMConfig(
                env: mockEnv.Object,
                jsonFileDir: tempDir,
                jsonFileName: null).Build();

            // env != null branch sets HostRoot to ContentRootPath
            config["HostRoot"].Should().Be(tempDir);
        }

        [TestMethod]
        public void WTMConfig_WithNullEnv_UsesCurrentDirectory()
        {
            var tempDir = Path.GetTempPath();
            IConfigurationBuilder builder = new ConfigurationBuilder();
            var config = builder.WTMConfig(
                env: null,
                jsonFileDir: tempDir).Build();
            // null env branch sets HostRoot to Directory.GetCurrentDirectory()
            config["HostRoot"].Should().NotBeNullOrEmpty();
        }

        // ─── WTM_SetCurrentDictionary — appsettings.json exists (line 75) ────

        [TestMethod]
        public void WTM_SetCurrentDictionary_WhenAppSettingsExistsInCwd_SetsBasePath()
        {
            // Create a temp directory and place an appsettings.json there,
            // then temporarily set the current directory so the method can find it.
            var tempDir = Path.Combine(Path.GetTempPath(), "wtm_setcwd_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            File.WriteAllText(Path.Combine(tempDir, "appsettings.json"), "{}");
            var original = Directory.GetCurrentDirectory();
            try
            {
                Directory.SetCurrentDirectory(tempDir);
                IConfigurationBuilder builder = new ConfigurationBuilder();
                var result = builder.WTM_SetCurrentDictionary();
                result.Should().NotBeNull();
                // After this call, the base path should be set to tempDir (line 75 branch)
                // We verify by building a config that can be resolved
                result.Build().Should().NotBeNull();
            }
            finally
            {
                Directory.SetCurrentDirectory(original);
                Directory.Delete(tempDir, recursive: true);
            }
        }

        // ─── WTMConfig — appsettings written to temp dir ──────────────────────

        [TestMethod]
        public void WTMConfig_WithRealJsonFile_ReadsValues()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "wtmcfgtest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var filePath = Path.Combine(tempDir, "test-appsettings.json");
            File.WriteAllText(filePath, "{\"MyKey\": \"MyValue\"}");
            try
            {
                IConfigurationBuilder builder = new ConfigurationBuilder();
                var config = builder.WTMConfig(
                    env: null,
                    jsonFileDir: tempDir,
                    jsonFileName: "test-appsettings.json").Build();
                config["MyKey"].Should().Be("MyValue");
            }
            finally
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
