using System;
using FluentAssertions;
using SwiftGet.Core.Localization;
using Xunit;

namespace SwiftGet.App.Tests;

public sealed class AccessibilityAndSecurityTests
{
    [Fact]
    public void LocalizationService_EnglishCatalog_ResolvesStandardStrings()
    {
        var localizer = new LocalizationService();

        localizer.GetString("App.Title").Should().Be("SwiftGet Download Manager");
        localizer.GetString("Tasks.AddDownload").Should().Be("Add new download...");
        localizer.GetString("Common.Cancel").Should().Be("Cancel");
        localizer.GetString("Common.OK").Should().Be("OK");
    }

    [Fact]
    public void LocalizationService_MissingKey_ReturnsDefaultOrKey()
    {
        var localizer = new LocalizationService();

        var withDefault = localizer.GetString("NonExistentKey", "Default Fallback");
        withDefault.Should().Be("Default Fallback");

        var withoutDefault = localizer.GetString("AnotherMissingKey");
        withoutDefault.Should().Be("AnotherMissingKey");
    }

    [Fact]
    public void LocalizationService_DynamicRegistration_ReturnsLocalizedValue()
    {
        var localizer = new LocalizationService();
        localizer.RegisterString("es-ES", "App.Title", "Administrador de Descargas SwiftGet");

        localizer.CurrentLanguage = "es-ES";
        localizer.GetString("App.Title").Should().Be("Administrador de Descargas SwiftGet");

        // Fallback to English for unlocalized key
        localizer.GetString("Common.OK").Should().Be("OK");
    }

    [Fact]
    public void LocalizationService_SupportedLanguages_ContainsPrimaryLocales()
    {
        var localizer = new LocalizationService();

        localizer.SupportedLanguages.Should().Contain("en-US");
        localizer.SupportedLanguages.Should().Contain("es-ES");
        localizer.SupportedLanguages.Should().Contain("de-DE");
        localizer.SupportedLanguages.Should().Contain("fr-FR");
        localizer.SupportedLanguages.Should().Contain("zh-CN");
        localizer.SupportedLanguages.Should().Contain("ja-JP");
    }
}
