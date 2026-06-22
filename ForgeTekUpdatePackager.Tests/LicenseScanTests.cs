using System.Linq;
using Xunit;
using ForgeTekUpdatePackager.Models;
using ForgeTekUpdatePackager.Services;

namespace ForgeTekUpdatePackager.Tests;

public class LicenseScanTests
{
    private const string PackagesJson = """
    {
      "version": 1,
      "projects": [
        {
          "frameworks": [
            {
              "framework": "net8.0",
              "topLevelPackages": [
                { "id": "Serilog", "resolvedVersion": "3.1.1" },
                { "id": "Newtonsoft.Json", "resolvedVersion": "13.0.3" }
              ],
              "transitivePackages": [
                { "id": "System.Memory", "resolvedVersion": "4.5.5" }
              ]
            }
          ]
        }
      ]
    }
    """;

    [Fact]
    public void ParsePackages_DedupesAndFlagsTransitive()
    {
        var packages = LicenseScanService.ParsePackages(PackagesJson);
        Assert.Equal(3, packages.Count);

        var transitive = packages.Single(p => p.Id == "System.Memory");
        Assert.True(transitive.Transitive);
        Assert.Equal("4.5.5", transitive.Version);

        Assert.False(packages.Single(p => p.Id == "Serilog").Transitive);
    }

    [Fact]
    public void ParseLicenseFromNuspec_ReadsExpression()
    {
        var xml = """
        <?xml version="1.0"?>
        <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
          <metadata>
            <id>Serilog</id>
            <version>3.1.1</version>
            <license type="expression">Apache-2.0</license>
            <projectUrl>https://serilog.net</projectUrl>
          </metadata>
        </package>
        """;
        var (license, _, projectUrl) = LicenseScanService.ParseLicenseFromNuspec(xml);
        Assert.Equal("Apache-2.0", license);
        Assert.Equal("https://serilog.net", projectUrl);
    }

    [Fact]
    public void ParseLicenseFromNuspec_FallsBackToLicensesNugetOrgSpdx()
    {
        var xml = """
        <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
          <metadata>
            <id>Old.Package</id>
            <licenseUrl>https://licenses.nuget.org/MIT</licenseUrl>
          </metadata>
        </package>
        """;
        var (license, licenseUrl, _) = LicenseScanService.ParseLicenseFromNuspec(xml);
        Assert.Equal("MIT", license);
        Assert.Equal("https://licenses.nuget.org/MIT", licenseUrl);
    }

    [Fact]
    public void ParseLicenseFromNuspec_FileTypeIsCustom_UnknownWhenAbsent()
    {
        var fileXml = """<package><metadata><id>X</id><license type="file">LICENSE.txt</license></metadata></package>""";
        Assert.Equal("Custom (file)", LicenseScanService.ParseLicenseFromNuspec(fileXml).License);

        var noneXml = """<package><metadata><id>X</id></metadata></package>""";
        Assert.Equal("Unknown", LicenseScanService.ParseLicenseFromNuspec(noneXml).License);
    }

    [Fact]
    public void Policy_ClassifiesCopyleftCorrectly()
    {
        var p = new LicensePolicy();
        Assert.Equal(PolicyAction.Allow, p.ActionFor("MIT"));
        Assert.Equal(PolicyAction.Allow, p.ActionFor("Apache-2.0"));
        Assert.Equal(PolicyAction.Block, p.ActionFor("GPL-3.0-only"));
        Assert.Equal(PolicyAction.Block, p.ActionFor("AGPL-3.0"));
        Assert.Equal(PolicyAction.Warn, p.ActionFor("LGPL-3.0-only"));  // LGPL must NOT match GPL
        Assert.Equal(PolicyAction.Warn, p.ActionFor("MPL-2.0"));
        Assert.Equal(PolicyAction.Warn, p.ActionFor("Unknown"));        // unknown → default Warn
    }

    [Fact]
    public void Policy_MultiLicenseExpression_TakesWorstAction()
    {
        var p = new LicensePolicy();
        Assert.Equal(PolicyAction.Block, p.ActionFor("MIT OR GPL-3.0-only"));
        Assert.Equal(PolicyAction.Allow, p.ActionFor("MIT OR Apache-2.0"));
    }

    [Fact]
    public void BuildText_And_Html_IncludeComponents()
    {
        var report = new LicenseReport
        {
            Components =
            {
                new LicenseComponent("Serilog", "3.1.1", "Apache-2.0", "", "https://serilog.net", false),
                new LicenseComponent("SomePkg", "1.0.0", "GPL-3.0", "", "", true),
            },
        };
        var svc = new LicenseScanService();

        var text = svc.BuildText(report, "MyApp");
        Assert.Contains("THIRD-PARTY COMPONENTS", text);
        Assert.Contains("Serilog 3.1.1", text);
        Assert.Contains("Apache-2.0", text);

        var html = svc.BuildHtml(report, "MyApp");
        Assert.Contains("<table>", html);
        Assert.Contains("Serilog", html);
        Assert.Contains("GPL-3.0", html);
    }
}
