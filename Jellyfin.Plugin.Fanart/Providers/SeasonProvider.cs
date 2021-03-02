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
using MediaBrowser.Common.Json;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Extensions;
using MediaBrowser.Model.Net;
using MediaBrowser.Model.Providers;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Fanart.Providers
{
    public class SeasonProvider : IRemoteImageProvider, IHasOrder
    {
        private readonly IHttpClientFactory _httpClientFactory;

        public SeasonProvider(IHttpClientFactory httpClientFactory)
        {
            _httpClientFactory = httpClientFactory;
        }

        /// <inheritdoc />
        public string Name => "Fanart";

        /// <inheritdoc />
        public int Order => 1;

        /// <inheritdoc />
        public bool Supports(BaseItem item)
            => item is Season;

        /// <inheritdoc />
        public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
        {
            return new List<ImageType>
            {
                ImageType.Backdrop,
                ImageType.Thumb,
                ImageType.Banner,
                ImageType.Primary
            };
        }

        /// <inheritdoc />
        public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
        {
            var list = new List<RemoteImageInfo>();

            var season = (Season)item;
            var series = season.Series;

            if (series != null)
            {
                var id = series.GetProviderId(MetadataProvider.Tvdb);

                if (!string.IsNullOrEmpty(id) && season.IndexNumber.HasValue)
                {
                    // Bad id entered
                    try
                    {
                        await SeriesProvider.Current.EnsureSeriesJson(id, cancellationToken).ConfigureAwait(false);
                    }
                    catch (HttpRequestException ex)
                    {
                        if (!ex.StatusCode.HasValue || ex.StatusCode.Value != HttpStatusCode.NotFound)
                        {
                            throw;
                        }
                    }

                    var path = SeriesProvider.Current.GetJsonPath(id);

                    try
                    {
                        await AddImages(list, season.IndexNumber.Value, path, cancellationToken).ConfigureAwait(false);
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

        private async Task AddImages(List<RemoteImageInfo> list, int seasonNumber, string path, CancellationToken cancellationToken)
        {
            Stream fileStream = File.OpenRead(path);
            var root = await JsonSerializer.DeserializeAsync<SeriesProvider.RootObject>(fileStream, JsonDefaults.GetOptions()).ConfigureAwait(false);

            AddImages(list, root, seasonNumber, cancellationToken);
        }

        private void AddImages(List<RemoteImageInfo> list, SeriesProvider.RootObject obj, int seasonNumber, CancellationToken cancellationToken)
        {
            PopulateImages(list, obj.seasonposter, ImageType.Primary, 1000, 1426, seasonNumber);
            PopulateImages(list, obj.seasonbanner, ImageType.Banner, 1000, 185, seasonNumber);
            PopulateImages(list, obj.seasonthumb, ImageType.Thumb, 500, 281, seasonNumber);
            PopulateImages(list, obj.showbackground, ImageType.Backdrop, 1920, 1080, seasonNumber);
        }

        private void PopulateImages(
            List<RemoteImageInfo> list,
            List<SeriesProvider.Image> images,
            ImageType type,
            int width,
            int height,
            int seasonNumber)
        {
            if (images == null)
            {
                return;
            }

            list.AddRange(images.Select(i =>
            {
                var url = i.url;
                var season = i.season;

                if (!string.IsNullOrEmpty(url)
                    && !string.IsNullOrEmpty(season)
                    && int.TryParse(season, NumberStyles.Integer, CultureInfo.InvariantCulture, out var imageSeasonNumber)
                    && seasonNumber == imageSeasonNumber)
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
    }
}
