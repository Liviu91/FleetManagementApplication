using MauiApp1.Services;

namespace MauiApp1
{
    public partial class App : Application
    {
        LoginPage _loginPage;
        public App(LoginPage loginPage)
        {
            InitializeComponent();
            _loginPage = loginPage;
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            return new Window(new NavigationPage(_loginPage));
        }
    }
}