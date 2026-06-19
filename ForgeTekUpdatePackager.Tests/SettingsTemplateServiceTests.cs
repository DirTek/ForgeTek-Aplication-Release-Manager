using System.IO;
using System.Linq;
using Xunit;
using NSubstitute;
using ForgeTekUpdatePackager.Models;
using ForgeTekUpdatePackager.Services;

namespace ForgeTekUpdatePackager.Tests;

public class SettingsTemplateServiceTests : IDisposable
{
    private readonly string _root;
    private readonly SettingsTemplateService _service;

    public SettingsTemplateServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ForgeTekTplTest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var settings = Substitute.For<ISettingsService>();
        settings.RootFolder.Returns(_root);
        _service = new SettingsTemplateService(settings);
    }

    [Fact]
    public void GetAll_IncludesBuiltInPresets()
    {
        var all = _service.GetAll();
        Assert.Contains(all, t => t.IsBuiltIn && t.Name == "WPF (.NET)");
        Assert.All(all.Where(t => t.IsBuiltIn), t => Assert.False(string.IsNullOrWhiteSpace(t.GitHubBuildCommand)));
    }

    [Fact]
    public void Save_ThenGetAll_RoundTripsUserTemplate()
    {
        var tpl = new SettingsTemplate { Name = "Company Standard", PublishProvider = "S3", S3Bucket = "releases" };
        _service.Save(tpl);

        var loaded = _service.GetAll().FirstOrDefault(t => !t.IsBuiltIn && t.Name == "Company Standard");
        Assert.NotNull(loaded);
        Assert.Equal("S3", loaded!.PublishProvider);
        Assert.Equal("releases", loaded.S3Bucket);
    }

    [Fact]
    public void Delete_RemovesUserTemplate()
    {
        var tpl = new SettingsTemplate { Name = "Temp" };
        _service.Save(tpl);
        _service.Delete(tpl.Id);
        Assert.DoesNotContain(_service.GetAll(), t => t.Id == tpl.Id);
    }

    [Fact]
    public void Save_IgnoresBuiltIn()
    {
        var builtIn = new SettingsTemplate { Name = "Fake", IsBuiltIn = true };
        _service.Save(builtIn);
        Assert.False(File.Exists(Path.Combine(_root, "templates.json")));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
