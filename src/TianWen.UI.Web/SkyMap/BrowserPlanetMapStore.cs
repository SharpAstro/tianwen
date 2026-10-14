using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TianWen.Lib.Imaging.Planetary;

namespace TianWen.UI.Web.SkyMap
{
    /// <summary>
    /// <see cref="IPlanetMapStore"/> in the browser: the same MAST URL the desktop fetches, which answers with
    /// <c>Access-Control-Allow-Origin: *</c>, through the page's own <see cref="HttpClient"/> (the browser's fetch and its HTTP cache;
    /// there is no disk to keep it on). The TIFF is decoded in .NET by <see cref="OpalColourMaps.Decode"/>, as on the desktop.
    /// </summary>
    internal sealed class BrowserPlanetMapStore(HttpClient http) : IPlanetMapStore
    {
        public async Task<byte[]?> GetAsync(OpalColourMap map, CancellationToken cancellationToken = default)
        {
            using var response = await http.GetAsync(map.Url, cancellationToken);
            return response.IsSuccessStatusCode ? await response.Content.ReadAsByteArrayAsync(cancellationToken) : null;
        }
    }
}
