using KaneCode.Services;
using KaneCode.Theming;
using System.Windows;

namespace KaneCode;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Must register MSBuild before any MSBuild types are loaded
        MSBuildProjectLoader.EnsureMSBuildRegistered();

        ThemeManager.ApplyTheme(AppTheme.Dark);
    }
}
