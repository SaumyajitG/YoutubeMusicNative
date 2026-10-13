using System.Windows;
using YoutubeMusicNative.Infrastructure;

namespace YoutubeMusicNative.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        WindowTheming.Attach(this);
    }
}
