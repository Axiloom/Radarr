using System.Net;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.RemotePathMappings;
using NzbDrone.Core.ThingiProvider;

namespace NzbDrone.Core.Download
{
    public abstract class UsenetClientBase<TSettings> : DownloadClientBase<TSettings>
        where TSettings : IProviderConfig, new()
    {
        protected readonly IHttpClient _httpClient;
        private readonly IValidateNzbs _nzbValidationService;

        protected UsenetClientBase(IHttpClient httpClient,
                                   IConfigService configService,
                                   IDiskProvider diskProvider,
                                   IRemotePathMappingService remotePathMappingService,
                                   IValidateNzbs nzbValidationService,
                                   Logger logger)
            : base(configService, diskProvider, remotePathMappingService, logger)
        {
            _httpClient = httpClient;
            _nzbValidationService = nzbValidationService;
        }

        public override DownloadProtocol Protocol => DownloadProtocol.Usenet;

        protected abstract string AddFromNzbFile(RemoteMovie remoteMovie, string filename, byte[] fileContents);

        public override async Task<string> Download(RemoteMovie remoteMovie, IIndexer indexer)
        {
            var url = remoteMovie.Release.DownloadUrl;
            var filename = FileNameBuilder.CleanFileName(remoteMovie.Release.Title) + ".nzb";

            byte[] nzbData;

            try
            {
                var request = indexer?.GetDownloadRequest(url) ?? new HttpRequest(url);
                request.RateLimitKey = remoteMovie?.Release?.IndexerId.ToString();

                var response = await RetryStrategy
                    .ExecuteAsync(static async (state, _) => await state._httpClient.GetAsync(state.request), (_httpClient, request))
                    .ConfigureAwait(false);

                nzbData = response.ResponseData;

                _logger.Debug("Downloaded nzb for movie '{0}' finished ({1} bytes from {2})", remoteMovie.Release.Title, nzbData.Length, url);
            }
            catch (HttpException ex)
            {
                if (ex.Response.StatusCode == HttpStatusCode.NotFound)
                {
                    _logger.Error(ex, "Downloading nzb file for movie '{0}' failed since it no longer exists ({1})", remoteMovie.Release.Title, url);
                    throw new ReleaseUnavailableException(remoteMovie.Release, "Downloading nzb failed", ex);
                }

                if ((int)ex.Response.StatusCode == 429)
                {
                    _logger.Error("API Grab Limit reached for {0}", url);
                }
                else
                {
                    _logger.Error(ex, "Downloading nzb for movie '{0}' failed ({1})", remoteMovie.Release.Title, url);
                }

                throw new ReleaseDownloadException(remoteMovie.Release, "Downloading nzb failed", ex);
            }
            catch (WebException ex)
            {
                _logger.Error(ex, "Downloading nzb for movie '{0}' failed ({1})", remoteMovie.Release.Title, url);

                throw new ReleaseDownloadException(remoteMovie.Release, "Downloading nzb failed", ex);
            }

            _nzbValidationService.Validate(filename, nzbData);

            _logger.Info("Adding report [{0}] to the queue.", remoteMovie.Release.Title);
            return AddFromNzbFile(remoteMovie, filename, nzbData);
        }
    }
}
