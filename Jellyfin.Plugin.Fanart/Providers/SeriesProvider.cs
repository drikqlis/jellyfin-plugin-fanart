using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Fanart.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Json;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Extensions;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Net;
using MediaBrowser.Model.Providers;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Fanart.Providers
{
    public class SeriesProvider : IRemoteImageProvider, IHasOrder
    {
        private readonly IServerConfigurationManager _config;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IFileSystem _fileSystem;

        private readonly SemaphoreSlim _ensureSemaphore = new SemaphoreSlim(1, 1);

        public SeriesProvider(IServerConfigurationManager config, IHttpClientFactory httpClientFactory, IFileSystem fileSystem)
        {
            _config = config;
            _httpClientFactory = httpClientFactory;
            _fileSystem = fileSystem;

            Current = this;
        }

        internal static SeriesProvider Current { get; private set; }

        /// <inheritdoc />
        public string Name => "Fanart";

        /// <inheritdoc />
        public int Order => 1;

        /// <inheritdoc />
        public bool Supports(BaseItem item)
            => item is Series;

        /// <inheritdoc />
        public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
        {
            return new List<ImageType>
            {
                ImageType.Primary,
                ImageType.Thumb,
                ImageType.Art,
                ImageType.Logo,
                ImageType.Backdrop,
                ImageType.Banner
            };
        }

        /// <inheritdoc />
        public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
        {
            var list = new List<RemoteImageInfo>();

            var series = (Series)item;

            var id = series.GetProviderId(MetadataProvider.Tvdb);

            if (!string.IsNullOrEmpty(id))
            {
                // Bad id entered
                try
                {
                    await EnsureSeriesJson(id, cancellationToken).ConfigureAwait(false);
                }
                catch (HttpRequestException ex)
                {
                    if (!ex.StatusCode.HasValue || ex.StatusCode.Value != HttpStatusCode.NotFound)
                    {
                        throw;
                    }
                }

                var path = GetJsonPath(id);

                try
                {
                    await AddImages(list, path);
                }
                catch (FileNotFoundException)
                {
                    // No biggie. Don't blow up
                }
                catch (IOException)
                {
                    // No biggie. Don't blow up
                }
            }

            var language = "en";

            var isLanguageEn = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase);

            // Sort first by width to prioritize HD versions
            return list.OrderByDescending(i => i.Width ?? 0)
                .ThenByDescending(i =>
                {
                    if (string.Equals(language, i.Language, StringComparison.OrdinalIgnoreCase))
                    {
                        return 3;
                    }

                    if (!isLanguageEn)
                    {
                        if (string.Equals("en", i.Language, StringComparison.OrdinalIgnoreCase))
                        {
                            return 2;
                        }
                    }

                    if (string.IsNullOrEmpty(i.Language))
                    {
                        return isLanguageEn ? 3 : 2;
                    }

                    return 0;
                })
                .ThenByDescending(i => i.CommunityRating ?? 0)
                .ThenByDescending(i => i.VoteCount ?? 0);
        }

        private async Task AddImages(List<RemoteImageInfo> list, string path)
        {
            Stream fileStream = File.OpenRead(path);
            var root = await JsonSerializer.DeserializeAsync<RootObject>(fileStream, JsonDefaults.GetOptions()).ConfigureAwait(false);

            AddImages(list, root);
        }

        private void AddImages(List<RemoteImageInfo> list, RootObject obj)
        {
            PopulateImages(list, obj.hdtvlogo, ImageType.Logo, 800, 310);
            PopulateImages(list, obj.hdclearart, ImageType.Art, 1000, 562);
            PopulateImages(list, obj.clearlogo, ImageType.Logo, 400, 155);
            PopulateImages(list, obj.clearart, ImageType.Art, 500, 281);
            PopulateImages(list, obj.showbackground, ImageType.Backdrop, 1920, 1080, true);
            PopulateImages(list, obj.seasonthumb, ImageType.Thumb, 500, 281);
            PopulateImages(list, obj.tvthumb, ImageType.Thumb, 500, 281);
            PopulateImages(list, obj.tvbanner, ImageType.Banner, 1000, 185);
            PopulateImages(list, obj.tvposter, ImageType.Primary, 1000, 1426);
        }

        private void PopulateImages(
            List<RemoteImageInfo> list,
            List<Image> images,
            ImageType type,
            int width,
            int height,
            bool allowSeasonAll = false)
        {
            if (images == null)
            {
                return;
            }

            list.AddRange(images.Select(i =>
            {
                var url = i.url;
                var season = i.season;

                var isSeasonValid = string.IsNullOrEmpty(season) ||
                    (allowSeasonAll && string.Equals(season, "all", StringComparison.OrdinalIgnoreCase));

                if (!string.IsNullOrEmpty(url) && isSeasonValid)
                {
                    var likesString = i.likes;

                    var info = new RemoteImageInfo
                    {
                        RatingType = RatingType.Likes,
                        Type = type,
                        Width = width,
                        Height = height,
                        ProviderName = Name,
                        Url = url.Replace("http://", "https://", StringComparison.OrdinalIgnoreCase),
                        Language = i.lang
                    };

                    if (!string.IsNullOrEmpty(likesString)
                        && int.TryParse(likesString, NumberStyles.Integer, CultureInfo.InvariantCulture, out var likes))
                    {
                        info.CommunityRating = likes;
                    }

                    return info;
                }

                return null;
            }).Where(i => i != null));
        }

        /// <inheritdoc />
        public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        {
            return _httpClientFactory.CreateClient(NamedClient.Default).GetAsync(new Uri(url), cancellationToken);
        }

        /// <summary>
        /// Gets the series data path.
        /// </summary>
        /// <param name="appPaths">The app paths.</param>
        /// <param name="seriesId">The series id.</param>
        /// <returns>System.String.</returns>
        internal static string GetSeriesDataPath(IApplicationPaths appPaths, string seriesId)
        {
            var seriesDataPath = Path.Combine(GetSeriesDataPath(appPaths), seriesId);

            return seriesDataPath;
        }

        /// <summary>
        /// Gets the series data path.
        /// </summary>
        /// <param name="appPaths">The app paths.</param>
        /// <returns>System.String.</returns>
        internal static string GetSeriesDataPath(IApplicationPaths appPaths)
        {
            var dataPath = Path.Combine(appPaths.CachePath, "fanart-tv");

            return dataPath;
        }

        public string GetJsonPath(string tvdbId)
        {
            var dataPath = GetSeriesDataPath(_config.ApplicationPaths, tvdbId);
            return Path.Combine(dataPath, "fanart.json");
        }

        internal async Task EnsureSeriesJson(string tvdbId, CancellationToken cancellationToken)
        {
            var path = GetJsonPath(tvdbId);

            // Only allow one thread in here at a time since every season will be calling this method, possibly concurrently
            await _ensureSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                var fileInfo = _fileSystem.GetFileSystemInfo(path);

                if (fileInfo.Exists)
                {
                    if ((DateTime.UtcNow - _fileSystem.GetLastWriteTimeUtc(fileInfo)).TotalDays <= 2)
                    {
                        return;
                    }
                }

                await DownloadSeriesJson(tvdbId, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _ensureSemaphore.Release();
            }
        }

        /// <summary>
        /// Downloads the series json.
        /// </summary>
        /// <param name="tvdbId">The TVDB identifier.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>Task.</returns>
        internal async Task DownloadSeriesJson(string tvdbId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var url = string.Format(
                CultureInfo.InvariantCulture,
                Plugin.BaseUrl,
                Plugin.ApiKey,
                tvdbId,
                "tv");

            var clientKey = Plugin.Instance.Configuration.PersonalApiKey;
            if (!string.IsNullOrWhiteSpace(clientKey))
            {
                url += "&client_key=" + clientKey;
            }

            var path = GetJsonPath(tvdbId);

            Directory.CreateDirectory(Path.GetDirectoryName(path));

            try
            {
                var httpClient = _httpClientFactory.CreateClient(NamedClient.Default);
                using (var httpResponse = await httpClient.GetAsync(new Uri(url), cancellationToken).ConfigureAwait(false))
                using (var response = httpResponse.Content)
                using (var fileStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, IODefaults.FileStreamBufferSize, FileOptions.Asynchronous))
                {
                    await response.CopyToAsync(fileStream, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (HttpRequestException exception)
            {
                if (exception.StatusCode.HasValue && exception.StatusCode.Value == HttpStatusCode.NotFound)
                {
                    // If the user has automatic updates enabled, save a dummy object to prevent repeated download attempts
                    Stream fileStream = File.OpenWrite(path);
                    await JsonSerializer.SerializeAsync(fileStream, new RootObject(), JsonDefaults.GetOptions()).ConfigureAwait(false);

                    return;
                }

                throw;
            }
        }

        public class Image
        {
            public string id { get; set; }

            public string url { get; set; }

            public string lang { get; set; }

            public string likes { get; set; }

            public string season { get; set; }
        }

        public class RootObject
        {
            public string name { get; set; }

            public string thetvdb_id { get; set; }

            public List<Image> clearlogo { get; set; }

            public List<Image> hdtvlogo { get; set; }

            public List<Image> clearart { get; set; }

            public List<Image> showbackground { get; set; }

            public List<Image> tvthumb { get; set; }

            public List<Image> seasonposter { get; set; }

            public List<Image> seasonthumb { get; set; }

            public List<Image> hdclearart { get; set; }

            public List<Image> tvbanner { get; set; }

            public List<Image> characterart { get; set; }

            public List<Image> tvposter { get; set; }

            public List<Image> seasonbanner { get; set; }
        }
    }
}
