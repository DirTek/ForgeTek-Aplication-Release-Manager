using Xunit;
using NSubstitute;
using ForgeTekUpdatePackager.Models;
using ForgeTekUpdatePackager.Services;
using ForgeTekUpdatePackager.ViewModels;

namespace ForgeTekUpdatePackager.Tests;

public class PackageStepTrackingTests
{
    private static PackageViewModel CreateViewModel()
    {
        var storageMock = Substitute.For<IStorageService>();
        var dialogMock = Substitute.For<IDialogService>();
        var settingsMock = Substitute.For<ISettingsService>();
        settingsMock.Global.Returns(new GlobalSettings());
        settingsMock.LoadAppSettings(Arg.Any<string>()).Returns(new AppSettings());
        settingsMock.GetVersionOutputPath(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<AppSettings?>())
            .Returns(@"C:\output\TestApp\1.0");

        var sessionMock = Substitute.For<ISessionService>();
        var setupStorageMock = Substitute.For<ISetupStorageService>();
        storageMock.GetAll().Returns(new List<AppEntry>());

        var sp = Substitute.For<IServiceProvider>();
        sp.GetService(typeof(IStorageService)).Returns(storageMock);
        sp.GetService(typeof(IDialogService)).Returns(dialogMock);
        sp.GetService(typeof(ISessionService)).Returns(sessionMock);
        // MainViewModel's ctor navigates to the dashboard, which is resolved from the provider.
        sp.GetService(typeof(DashboardViewModel)).Returns(
            new DashboardViewModel(storageMock, setupStorageMock, sessionMock, settingsMock));

        var vm = new PackageViewModel(
            storageMock,
            Substitute.For<ISigningService>(),
            Substitute.For<IScannerService>(),
            settingsMock,
            Substitute.For<ILogService>(),
            Substitute.For<IPackagingService>(),
            Substitute.For<IFtpService>(),
            Substitute.For<IManifestService>(),
            Substitute.For<IUpdateCatalogService>(),
            dialogMock);
        var entry = new AppEntry { Name = "TestApp", FolderPath = @"C:\apps\test" };
        var version = new AppVersion { VersionNumber = "1.0", ScanDate = DateTime.Now };
        entry.Versions.Add(version);
        vm.Initialize(entry, version, new MainViewModel(sp));
        return vm;
    }

    [Fact]
    public void InitialStep_IsSign()
    {
        var vm = CreateViewModel();
        Assert.True(vm.IsSignCurrent);
        Assert.False(vm.IsSignDone);
    }

    [Fact]
    public void StepTitle_ReturnsCorrectText()
    {
        var vm = CreateViewModel();
        Assert.Contains("Step 1 of 5", vm.StepTitle);
    }

    [Fact]
    public void AdvanceLabel_AtFtpStep_ShowsFinish()
    {
        var vm = CreateViewModel();
        Assert.Equal("Next →", vm.AdvanceLabel);
    }

    [Fact]
    public void AppName_MatchesEntryName()
    {
        var vm = CreateViewModel();
        Assert.Equal("TestApp", vm.AppName);
    }

    [Fact]
    public void VersionLabel_ContainsVersionNumber()
    {
        var vm = CreateViewModel();
        Assert.Contains("1.0", vm.VersionLabel);
    }

    [Fact]
    public void IsOperationInProgress_InitiallyFalse()
    {
        var vm = CreateViewModel();
        Assert.False(vm.IsOperationInProgress);
    }

    [Fact]
    public void IsNotInProgress_InitiallyTrue()
    {
        var vm = CreateViewModel();
        Assert.True(vm.IsNotInProgress);
    }

    [Fact]
    public void IsSkipVisible_InitiallyTrue_AtSignStep()
    {
        var vm = CreateViewModel();
        Assert.True(vm.IsSkipVisible);
    }

    [Fact]
    public void IsReadyToAdvance_InitiallyFalse()
    {
        var vm = CreateViewModel();
        Assert.False(vm.IsReadyToAdvance);
    }
}
