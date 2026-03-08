using MauiApp1.Model;
using Newtonsoft.Json;
using System.Text;

namespace MauiApp1.Services
{
    public class RouteService
    {
        private readonly HttpClient _http;

        public RouteService(HttpClient http) => _http = http;

        public async Task<IReadOnlyList<RouteDto>> GetDriverRoutesAsync()
        {
            var json = await _http.GetStringAsync("api/driver/routes");
            return JsonConvert.DeserializeObject<List<RouteDto>>(json)!;
        }

        public async Task<bool> UpdateRouteStatusAsync(int routeId, string newStatus)
        {
            var dto = new StatusUpdateDto { RouteId = routeId, Status = newStatus };
            var resp = await _http.PostAsync("api/driver/updatestatus",
                         new StringContent(JsonConvert.SerializeObject(dto),
                                           Encoding.UTF8, "application/json"));
            return resp.IsSuccessStatusCode;
        }
    }
}
