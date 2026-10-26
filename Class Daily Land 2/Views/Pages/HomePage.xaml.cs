using System.Windows;
using System.Windows.Controls;

namespace ClassDailyLand2.App.Views.Pages;

public partial class HomePage : UserControl
{
    /// <summary>点首页卡片时请求外壳切页。</summary>
    public event Action<string>? NavigateRequested;

    public HomePage() => InitializeComponent();

    void OnCardClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: string id } && id.Length > 0)
            NavigateRequested?.Invoke(id);
    }
}
