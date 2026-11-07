using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using CryptArc.App.Services;
using CryptArc.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace CryptArc.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            ServiceProvider services = ConfigureServices();
            desktop.MainWindow = services.GetRequiredService<MainWindow>();
            desktop.Exit += (_, _) => services.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }

    // The composition root: the only place that knows concrete types.
    // Everything else asks for its dependencies through the constructor.
    private static ServiceProvider ConfigureServices() =>
        new ServiceCollection()
            .AddSingleton<ICodecMeasurement, CodecMeasurement>()
            .AddTransient<MainWindowViewModel>()
            .AddTransient<MainWindow>()
            .BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });
}
