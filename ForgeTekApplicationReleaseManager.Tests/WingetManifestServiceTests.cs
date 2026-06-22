using System.Collections.Generic;
using System.Linq;
using Xunit;
using ForgeTekApplicationReleaseManager.Services;

namespace ForgeTekApplicationReleaseManager.Tests;

public class WingetManifestServiceTests
{
    private static readonly WingetManifestService Svc = new();

    private static WingetManifestInput Sample(
        string id = "AcmeCorp.STLOrganizer",
        string? silent = "/VERYSILENT",
        string? silentWithProgress = "/SILENT",
        IReadOnlyList<string>? tags = null)
        => new(
            PackageIdentifier: id,
            Version: "1.5.2",
            Publisher: "Acme Corp",
            PackageName: "STL Organizer",
            InstallerUrl: "https://example.com/STLOrganizerSetup.exe",
            InstallerSha256: "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08",
            Architecture: "x64",
            InstallerType: "exe",
            Moniker: "stlorganizer",
            ShortDescription: "Manage your STL files.",
            License: "MIT",
            Tags: tags,
            SilentSwitch: silent,
            SilentWithProgressSwitch: silentWithProgress);

    [Fact]
    public void BuildYaml_ProducesThreeFiles_WithCorrectNames()
    {
        var files = Svc.BuildYaml(Sample());
        Assert.Equal(3, files.Count);
        Assert.True(files.ContainsKey("AcmeCorp.STLOrganizer.yaml"));
        Assert.True(files.ContainsKey("AcmeCorp.STLOrganizer.installer.yaml"));
        Assert.True(files.ContainsKey("AcmeCorp.STLOrganizer.locale.en-US.yaml"));
    }

    [Fact]
    public void EachFile_HasSchemaHeader_AndManifestVersion()
    {
        foreach (var content in Svc.BuildYaml(Sample()).Values)
        {
            Assert.StartsWith("# yaml-language-server: $schema=https://aka.ms/winget-manifest", content);
            Assert.Contains("ManifestVersion: 1.6.0", content);
        }
    }

    [Fact]
    public void Files_HaveCorrectManifestTypes()
    {
        var f = Svc.BuildYaml(Sample());
        Assert.Contains("ManifestType: version", f["AcmeCorp.STLOrganizer.yaml"]);
        Assert.Contains("ManifestType: installer", f["AcmeCorp.STLOrganizer.installer.yaml"]);
        Assert.Contains("ManifestType: defaultLocale", f["AcmeCorp.STLOrganizer.locale.en-US.yaml"]);
    }

    [Fact]
    public void Installer_UppercasesSha256_AndEmitsSwitches()
    {
        var installer = Svc.BuildYaml(Sample())["AcmeCorp.STLOrganizer.installer.yaml"];
        Assert.Contains("InstallerSha256: 9F86D081884C7D659A2FEAA0C55AD015A3BF4F1B2B0B822CD15D6C15B0F00A08", installer);
        Assert.Contains("InstallerSwitches:", installer);
        Assert.Contains("Silent: /VERYSILENT", installer);
        Assert.Contains("SilentWithProgress: /SILENT", installer);
    }

    [Fact]
    public void Installer_OmitsSwitches_WhenNoneProvided()
    {
        var installer = Svc.BuildYaml(Sample(silent: null, silentWithProgress: null))
            ["AcmeCorp.STLOrganizer.installer.yaml"];
        Assert.DoesNotContain("InstallerSwitches", installer);
    }

    [Fact]
    public void Locale_HasRequiredFields_AndTags()
    {
        var locale = Svc.BuildYaml(Sample(tags: new[] { "stl", "3d-printing" }))
            ["AcmeCorp.STLOrganizer.locale.en-US.yaml"];
        Assert.Contains("Publisher: Acme Corp", locale);
        Assert.Contains("PackageName: STL Organizer", locale);
        Assert.Contains("License: MIT", locale);
        Assert.Contains("ShortDescription: Manage your STL files.", locale);
        Assert.Contains("Tags:", locale);
        Assert.Contains("  - stl", locale);
        Assert.Contains("  - 3d-printing", locale);
    }

    [Fact]
    public void Locale_FallsBack_WhenLicenseOrDescriptionBlank()
    {
        var input = Sample() with { License = null, ShortDescription = null };
        var locale = Svc.BuildYaml(input)["AcmeCorp.STLOrganizer.locale.en-US.yaml"];
        Assert.Contains("License: Proprietary", locale);
        Assert.Contains("ShortDescription: STL Organizer", locale); // falls back to package name
    }

    [Fact]
    public void DeriveIdentifier_StripsSpacesAndPunctuation()
    {
        Assert.Equal("AcmeCorp.STLOrganizer", Svc.DeriveIdentifier("Acme Corp", "STL Organizer"));
        Assert.Equal("App.App", Svc.DeriveIdentifier("", "!!!"));
    }

    [Fact]
    public void Write_UsesWingetFolderLayout()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "ftwinget_" + System.Guid.NewGuid().ToString("N"));
        try
        {
            var folder = Svc.Write(Sample(), root);
            var expected = System.IO.Path.Combine(root, "manifests", "a", "AcmeCorp", "STLOrganizer", "1.5.2");
            Assert.Equal(expected, folder);
            Assert.True(System.IO.File.Exists(System.IO.Path.Combine(folder, "AcmeCorp.STLOrganizer.yaml")));
            Assert.Equal(3, System.IO.Directory.GetFiles(folder).Length);
        }
        finally
        {
            if (System.IO.Directory.Exists(root)) System.IO.Directory.Delete(root, recursive: true);
        }
    }
}
