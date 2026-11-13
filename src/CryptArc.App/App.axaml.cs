using System.Globalization;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using CryptArc.App.Localization;
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

            // Before anything is resolved: view models read their first strings in
            // their constructors. The saved choice wins, then the system language.
            Localizer localizer = Localizer.Instance;
            string? saved = services.GetRequiredService<ILanguagePreference>().Load();
            localizer.Use(localizer.FindLanguage(saved) ?? localizer.Match(CultureInfo.CurrentUICulture));

            MainWindow window = services.GetRequiredService<MainWindow>();
            desktop.MainWindow = window;

            // `CryptArc.App <file>` opens that file straight away, which is also
            // what the OS passes for "Open with".
            if (desktop.Args is [string path, ..])
                window.Opened += async (_, _) => await window.OpenAsync(path);
            desktop.Exit += (_, _) => services.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }

    // The composition root: the only place that knows concrete types.
    // Everything else asks for its dependencies through the constructor.
    private static ServiceProvider ConfigureServices() =>
        new ServiceCollection()
            .AddSingleton<ICodecMeasurement, CodecMeasurement>()
            .AddSingleton<IBenchmarkResults, BenchmarkResults>()
            .AddSingleton<ILanguagePreference, LanguagePreference>()
            // One of each, for the life of the app: they subscribe to Localizer.LanguageChanged
            // and never unsubscribe, so a second instance would be kept alive by the first's event.
            .AddSingleton<AnalysePageViewModel>()
            .AddSingleton<ComparePageViewModel>()
            .AddSingleton<CompressPageViewModel>()
            .AddSingleton<TransformLabPageViewModel>()
            .AddSingleton<NaiveVsOptimisedPageViewModel>()
            .AddSingleton<ArchivePageViewModel>()
            .AddSingleton<MainWindowViewModel>()
            .AddTransient<MainWindow>()
            .BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });
}
