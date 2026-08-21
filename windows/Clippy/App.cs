using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Markup;

namespace Clippy;

/// <summary>
/// Application shell. The whole UI is built in code (see <c>Views/</c> and <c>Theme/</c>),
/// so there is no XAML markup to compile — only the one control template below, which is
/// parsed at runtime because templates have no practical code-only equivalent.
/// </summary>
public partial class App : Application
{
    public static MainWindow? MainWindow { get; private set; }

    private const string PlainButtonStyleXaml = """
        <Style
            xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
            x:Key="ClippyPlainButtonStyle"
            TargetType="Button">
            <Setter Property="Background" Value="Transparent" />
            <Setter Property="BorderThickness" Value="0" />
            <Setter Property="Padding" Value="0" />
            <Setter Property="UseSystemFocusVisuals" Value="False" />
            <Setter Property="HorizontalContentAlignment" Value="Stretch" />
            <Setter Property="VerticalContentAlignment" Value="Stretch" />
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="Button">
                        <Border
                            x:Name="Root"
                            Background="{TemplateBinding Background}"
                            BorderBrush="{TemplateBinding BorderBrush}"
                            BorderThickness="{TemplateBinding BorderThickness}"
                            CornerRadius="{TemplateBinding CornerRadius}"
                            Padding="{TemplateBinding Padding}">
                            <ContentPresenter
                                HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}"
                                VerticalAlignment="{TemplateBinding VerticalContentAlignment}" />
                        </Border>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>
        """;

    public App()
    {
        // Default WinUI control templates. Without this, stock controls render unstyled.
        Resources.MergedDictionaries.Add(new Microsoft.UI.Xaml.Controls.XamlControlsResources());
        Resources["ClippyPlainButtonStyle"] = (Style)XamlReader.Load(PlainButtonStyleXaml);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow = new MainWindow();
        MainWindow.Activate();
    }
}
