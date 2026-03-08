using MauiApp1.Model;
using Newtonsoft.Json;
using System.Net.Http.Headers;
using System.Text;

namespace MauiApp1.Services
{
    public class AuthService
    {
        private readonly HttpClient _httpClient;

        public AuthService(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        public async Task<string> LoginAsync(string email, string password)
        {
            //var login = new LoginModel { Email = email, Password = password };
            //var content = new StringContent(JsonConvert.SerializeObject(login), Encoding.UTF8, "application/json");

            //var response = await _httpClient.PostAsync("api/auth/login", content);
            ////var response = await _httpClient.PostAsync("https://localhost:7292/api/auth/login", content);
            //if (!response.IsSuccessStatusCode)
            //    throw new Exception("Login failed");

            //var responseString = await response.Content.ReadAsStringAsync();
            //var result = JsonConvert.DeserializeObject<Dictionary<string, string>>(responseString);

            //var token = result["token"];
            //await SecureStorage.Default.SetAsync("auth_token", token);
            //return token;
            var login = new LoginModel { Email = email, Password = password };
            var content = new StringContent(JsonConvert.SerializeObject(login), Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync("api/auth/login", content);
            //var response = await _httpClient.PostAsync("https://localhost:7292/api/auth/login", content);
            if (!response.IsSuccessStatusCode)
                throw new Exception("Login failed");

            var responseString = await response.Content.ReadAsStringAsync();
            var result = JsonConvert.DeserializeObject<Dictionary<string, string>>(responseString);

            var token = result["token"];
            await SecureStorage.Default.SetAsync("auth_token", token);
            return token;
        }

        public void SetAuthHeader(string token)
        {
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }
}