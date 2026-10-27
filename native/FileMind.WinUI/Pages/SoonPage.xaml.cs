using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace FileMind.Pages;

public sealed partial class SoonPage : Page
{
    public SoonPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is ValueTuple<string, string> (var title, var blurb))
        {
            Heading.Text = title;
            Blurb.Text = blurb;
        }
    }
}
