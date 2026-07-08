using CommunityToolkit.Maui;
using MauiApp1.Pages;
using MauiApp1.Services;
using Microsoft.Extensions.Logging;

namespace MauiApp1
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();

            builder.UseMauiApp<App>()
                .UseMauiCommunityToolkit()
                   .ConfigureFonts(f =>
                   {
                       f.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                       f.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                   });

#if DEBUG
            builder.Logging.AddDebug();
#endif

            // 1.  HttpClient with SSL-ignore + AuthHandler
            builder.Services.AddTransient<AuthHandler>();

            builder.Services.AddHttpClient("ApiClient", c =>
            {
                c.BaseAddress = new Uri(AppConfig.ApiBaseUrl);
            })
            .ConfigurePrimaryHttpMessageHandler(() =>
                new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = (_, __, ___, ____) => true
                })
            .AddHttpMessageHandler<AuthHandler>();          // ⬅ add token automatically

            // 2.  DI registrations
            builder.Services.AddSingleton(
                sp =>
                {
                    var client = sp.GetRequiredService<IHttpClientFactory>().CreateClient("ApiClient");
                    client.Timeout = TimeSpan.FromSeconds(30);
                    return client;
                });
            builder.Services.AddSingleton<AuthService>();
            builder.Services.AddSingleton<RouteService>();
            builder.Services.AddSingleton<RabbitMqService>();
#if ANDROID
            builder.Services.AddSingleton<ObdService>();
#endif

            builder.Services.AddSingleton<RoutesPage>();
            builder.Services.AddSingleton<LoginPage>();

            return builder.Build();
        }
    }
}
