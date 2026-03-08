using MauiApp1.Pages;
using MauiApp1.Services;

namespace MauiApp1;

public partial class LoginPage : ContentPage
{
    private readonly AuthService _authService;
    private readonly RoutesPage _routesPage;

    public LoginPage(AuthService authService, RoutesPage routesPage)
    {
        InitializeComponent();
        _authService = authService;
        _routesPage = routesPage;
    }

    private async void OnLoginClicked(object sender, EventArgs e)
    {
        ErrorLabel.IsVisible = false;

        var email = EmailEntry.Text?.Trim();
        var pwd = PasswordEntry.Text;

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(pwd))
        {
            ErrorLabel.Text = "Fill in both fields.";
            ErrorLabel.IsVisible = true;
            return;
        }

        try
        {
            await _authService.LoginAsync(email, pwd);
            await Navigation.PushAsync(_routesPage);
        }
        catch (Exception ex)
        {
            ErrorLabel.Text = ex.Message;
            ErrorLabel.IsVisible = true;
        }
    }
}