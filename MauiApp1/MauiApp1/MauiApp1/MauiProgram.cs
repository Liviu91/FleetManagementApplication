using CommunityToolkit.Maui;
using MauiApp1.Pages;
using MauiApp1.Services;
using Microsoft.Extensions.Logging;

namespace MauiApp1
{
    public static class MauiProgram
    {
        //        public static MauiApp CreateMauiApp()
        //        {
        //            var builder = MauiApp.CreateBuilder();
        //            builder
        //                .UseMauiApp<App>()
        //                .ConfigureFonts(fonts =>
        //                {
        //                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
        //                    fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
        //                });

        //#if DEBUG
        //    		builder.Logging.AddDebug();
        //#endif

        //            // Register HttpClient with your backend base URL
        //            //builder.Services.AddSingleton(new HttpClient
        //            //{
        //            //    //BaseAddress = new Uri("https://localhost:7292/") // ← replace with your actual URL
        //            //    BaseAddress = new Uri("https://10.0.2.2:7292/") // ← replace with your actual URL
        //            //});

        //            builder.Services.AddSingleton<HttpClient>(sp =>
        //            {
        //                var handler = new HttpClientHandler
        //                {
        //                    ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true // ← ignores all SSL errors
        //                };

        //                return new HttpClient(handler)
        //                {
        //                    BaseAddress = new Uri("https://10.0.2.2:7292/")
        //                };
        //            });

        //            builder.Services.AddSingleton<AuthService>();

        //            // Register pages & services (example)
        //            builder.Services.AddSingleton<LoginPage>();

        //            return builder.Build();
        //        }
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
                //c.BaseAddress = new Uri(DeviceInfo.Platform == DevicePlatform.Android
                //                         ? "http://192.168.30.131:7292/"  // Android emulator loop-back
                //                         : "https://localhost:7292/");
                //c.BaseAddress = new Uri(DeviceInfo.Platform == DevicePlatform.Android
                //                         ? "https://10.0.2.2:7292/"  // Android emulator loop-back
                //                         : "https://localhost:7292/");
                c.BaseAddress = new Uri(DeviceInfo.Platform == DevicePlatform.Android
                                         ? "http://192.168.1.142:7292/"  // PC IP on local network
                                         : "http://192.168.1.142:7292/");
                //c.BaseAddress = new Uri(DeviceInfo.Platform = = DevicePlatform.Android
                //         ? "http://10.126.159.22:7292/"  // Android emulator loop-back
                //         : "http://10.126.159.22:7292/");
            })
            .ConfigurePrimaryHttpMessageHandler(() =>
                new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = (_, __, ___, ____) => true
                })
            .AddHttpMessageHandler<AuthHandler>();          // ⬅ add token automatically

            // 2.  DI registrations
            builder.Services.AddSingleton(
                sp => sp.GetRequiredService<IHttpClientFactory>().CreateClient("ApiClient"));
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
