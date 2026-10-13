using System.Windows.Controls;
using System.Windows.Input;
using YoutubeMusicNative.ViewModels;

namespace YoutubeMusicNative.Views;

public partial class PlayerBar : UserControl
{
    public PlayerBar() => InitializeComponent();

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void OnSeekStart(object sender, MouseButtonEventArgs e)
    {
        if (Vm is { } vm) vm.Player.IsSeeking = true;
    }

    private void OnSeekEnd(object sender, MouseButtonEventArgs e)
    {
        if (Vm is not { } vm) return;
        vm.Player.Seek(Seek.Value);
        vm.Player.IsSeeking = false;
    }
}
