using SerialScout.App.ViewModels;
using SerialScout.Core.Sessions.Posix;

namespace SerialScout.App.Tests;

public sealed class AppPlatformTests
{
    [Fact]
    public void CreateSerialLinkFactorySelectsDarwinFactoryForMacOS()
    {
        var factory = AppPlatform.CreateSerialLinkFactory(isMacOS: true, isWindows: false);

        Assert.IsType<MacosSerialLinkFactory>(factory);
    }

    [Fact]
    public void MacOSBaudChoicesExcludeUnsupportedHighRates()
    {
        Assert.DoesNotContain("460800", ProfileEditorViewModel.MacosBaudRateChoices);
        Assert.DoesNotContain("921600", ProfileEditorViewModel.MacosBaudRateChoices);
        Assert.Contains("460800", ProfileEditorViewModel.SharedBaudRateChoices);
        Assert.Contains("921600", ProfileEditorViewModel.SharedBaudRateChoices);
    }
}
