﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using PatreonDownloader.Common.Interfaces;
using PatreonDownloader.Common.Interfaces.Plugins;
using PatreonDownloader.Engine.Enums;
using PatreonDownloader.Engine.Events;
using PatreonDownloader.Engine.Exceptions;
using PatreonDownloader.Engine.Helpers;
using PatreonDownloader.Engine.Models;
using PatreonDownloader.Engine.Stages.Crawling;
using PatreonDownloader.Engine.Stages.Downloading;
using PatreonDownloader.Engine.Stages.Initialization;
using PatreonDownloader.Interfaces;
using PatreonDownloader.Interfaces.Models;
using PatreonDownloader.PuppeteerEngine;

namespace PatreonDownloader.Engine
{
    public sealed class PatreonDownloader : IPatreonDownloader, IDisposable
    {
        private CookieContainer _cookieContainer;

        private IWebDownloader _webDownloader;
        private ICampaignIdRetriever _campaignIdRetriever;
        private ICampaignInfoRetriever _campaignInfoRetriever;
        private IRemoteFilenameRetriever _remoteFilenameRetriever;
        private IDownloader _directDownloader;
        private IDownloadManager _downloadManager;
        private IPageCrawler _pageCrawler;
        private IPluginManager _pluginManager;
        private ICookieValidator _cookieValidator;
        private IPuppeteerEngine _puppeteerEngine;

        private SemaphoreSlim _initializationSemaphore;
        // We don't want those variables to be optimized by compiler
        private volatile bool _isInitialized;
        private volatile bool _isRunning;

        private bool _headlessBrowser;

        private readonly Logger _logger = LogManager.GetCurrentClassLogger();

        public event EventHandler<DownloaderStatusChangedEventArgs> StatusChanged;
        public event EventHandler<PostCrawlEventArgs> PostCrawlStart;
        public event EventHandler<PostCrawlEventArgs> PostCrawlEnd;
        public event EventHandler<NewCrawledUrlEventArgs> NewCrawledUrl;
        public event EventHandler<CrawlerMessageEventArgs> CrawlerMessage;
        public event EventHandler<FileDownloadedEventArgs> FileDownloaded;

        public bool IsRunning
        {
            get => _isRunning;
        }

        // TODO: Implement cancellation token
        /// <summary>
        /// Create a new downloader for specified url
        /// </summary>
        /// <param name="cookieContainer">Cookie container containing patreon and cloudflare session cookies</param>
        /// <param name="headlessBrowser">If set to false then the internal browser window will be visible</param>
        public PatreonDownloader(CookieContainer cookieContainer, bool headlessBrowser = true)
        {
            _cookieContainer = cookieContainer ?? throw new ArgumentNullException(nameof(cookieContainer));
            _headlessBrowser = headlessBrowser;

            _initializationSemaphore = new SemaphoreSlim(1,1);
            _isInitialized = false;

            OnStatusChanged(new DownloaderStatusChangedEventArgs(DownloaderStatus.Ready));
        }

        /// <summary>
        /// Download specified creator
        /// </summary>
        /// <param name="url">Url of the creator's page</param>
        /// <param name="settings">Downloader settings, will be set to default values if not provided</param>
        public async Task Download(string url, PatreonDownloaderSettings settings = null)
        {
            if(string.IsNullOrEmpty(url))
                throw new ArgumentException("Argument cannot be null or empty", nameof(url));

            url = url.ToLower(CultureInfo.InvariantCulture);

            if (!url.StartsWith("https://www.patreon.com/"))
                throw new PatreonDownloaderException("Download url should start with \"https://www.patreon.com/\"");

            if (!url.StartsWith("https://www.patreon.com/m/")
                && !url.StartsWith("https://www.patreon.com/user")
                && !url.EndsWith("/posts"))
            {
                StringBuilder errorStringBuilder = new StringBuilder();
                errorStringBuilder.AppendLine("Invalid download url. Make sure it follows one of the following patterns:");
                errorStringBuilder.AppendLine("https://www.patreon.com/m/#numbers#/posts");
                errorStringBuilder.AppendLine("https://www.patreon.com/user?u=#numbers#");
                errorStringBuilder.AppendLine("https://www.patreon.com/user/posts?u=#numbers#");
                errorStringBuilder.AppendLine("https://www.patreon.com/#creator name#/posts");
                errorStringBuilder.AppendLine("If you think this is an error, feel free to create a new issue on GitHub.");
                throw new PatreonDownloaderException(errorStringBuilder.ToString());
            }


            settings = settings ?? new PatreonDownloaderSettings();

            string downloadDirectory = settings.DownloadDirectory;

            settings.Consumed = true;
            _logger.Debug($"Patreon downloader settings: {settings}");

            try
            {
                // Make sure several threads cannot access initialization code at once
                await _initializationSemaphore.WaitAsync();
                try
                {
                    if (_isRunning)
                    {
                        throw new DownloaderAlreadyRunningException(
                            "Unable to start new download while another one is in progress");
                    }

                    _isRunning = true;
                    OnStatusChanged(new DownloaderStatusChangedEventArgs(DownloaderStatus.Initialization));

                    // Initialize all required classes if required
                    if (!_isInitialized)
                    {
                        _logger.Debug("Initiaization required");
                        Initialize(settings);
                    }

                    //Call initialization code in all plugins
                    await _pluginManager.BeforeStart();

                    //Call initialization code in direct downloader
                    await _directDownloader.BeforeStart();

                    try
                    {
                        await _cookieValidator.ValidateCookies(_cookieContainer);
                    }
                    catch (CookieValidationException ex)
                    {
                        _logger.Fatal($"Cookie validation failed: {ex}");
                        throw;
                    }
                }
                finally
                {
                    // Release the lock after all required initialization code has finished execution
                    _initializationSemaphore.Release();
                }

                _logger.Debug("Retrieving campaign ID");
                OnStatusChanged(new DownloaderStatusChangedEventArgs(DownloaderStatus.RetrievingCampaignInformation));
                long campaignId = await _campaignIdRetriever.RetrieveCampaignId(url);

                if (campaignId == -1)
                {
                    throw new PatreonDownloaderException("Unable to retrieve campaign id.");
                }

                _logger.Debug($"Campaign ID: {campaignId}");

                _logger.Debug($"Retrieving campaign info");
                CampaignInfo campaignInfo = await _campaignInfoRetriever.RetrieveCampaignInfo(campaignId);
                _logger.Debug($"Campaign name: {campaignInfo.Name}");

                try
                {

                    if (string.IsNullOrEmpty(downloadDirectory))
                    {
                        //Download directory is creator's campaign name with invalid path characters removed
                        string creatorNameDirectory = String.Concat(campaignInfo.Name.Split(Path.GetInvalidFileNameChars()))
                            .ToLower(CultureInfo.InvariantCulture).Trim();
                        downloadDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "download", creatorNameDirectory);
                    }
                    if (!Directory.Exists(downloadDirectory))
                    {
                        Directory.CreateDirectory(downloadDirectory);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Fatal($"Unable to create download directory: {ex}");
                    throw new PatreonDownloaderException("Unable to create download directory", ex);
                }

                _logger.Debug("Starting crawler");
                OnStatusChanged(new DownloaderStatusChangedEventArgs(DownloaderStatus.Crawling));
                List<CrawledUrl> crawledUrls = await _pageCrawler.Crawl(campaignInfo, settings, downloadDirectory);

                _logger.Debug("Closing puppeteer browser");
                await _puppeteerEngine.CloseBrowser();

                _logger.Debug("Starting downloader");
                OnStatusChanged(new DownloaderStatusChangedEventArgs(DownloaderStatus.Downloading));
                await _downloadManager.Download(crawledUrls, downloadDirectory);

                _logger.Debug("Finished downloading");
                OnStatusChanged(new DownloaderStatusChangedEventArgs(DownloaderStatus.Done));
            }
            finally
            {
                _isRunning = false;
                OnStatusChanged(new DownloaderStatusChangedEventArgs(DownloaderStatus.Ready));
            }
        }

        private void Initialize(PatreonDownloaderSettings settings)
        {
            if (_isInitialized)
                return;

            _logger.Debug("Initializing PatreonDownloader...");

            _logger.Debug("Initializing plugin manager");
            _pluginManager = new PluginManager();

            _logger.Debug("Initializing puppeteer engine");
            _puppeteerEngine = new PuppeteerEngine.PuppeteerEngine(_headlessBrowser);

            _logger.Debug("Initializing file downloader");
            _webDownloader = new WebDownloader(_cookieContainer, _puppeteerEngine, settings.OverwriteFiles);

            _logger.Debug("Initializing cookie validator");
            _cookieValidator = new CookieValidator(_webDownloader);

            _logger.Debug("Initializing id retriever");
            _campaignIdRetriever = new CampaignIdRetriever(_webDownloader);

            _logger.Debug("Initializing campaign info retriever");
            _campaignInfoRetriever = new CampaignInfoRetriever(_webDownloader);

            _logger.Debug("Initializing remote filename retriever");
            _remoteFilenameRetriever = new RemoteFilenameRetriever(_cookieContainer);

            _logger.Debug("Initializing default (direct) downloader");
            _directDownloader = new DirectDownloader(_webDownloader, _remoteFilenameRetriever);

            _logger.Debug("Initializing download manager");
            _downloadManager = new DownloadManager(_pluginManager, _directDownloader);
            _downloadManager.FileDownloaded += DownloadManagerOnFileDownloaded;

            _logger.Debug("Initializing page crawler");
            _pageCrawler = new PageCrawler(_webDownloader);
            _pageCrawler.PostCrawlStart += PageCrawlerOnPostCrawlStart;
            _pageCrawler.PostCrawlEnd += PageCrawlerOnPostCrawlEnd;
            _pageCrawler.NewCrawledUrl += PageCrawlerOnNewCrawledUrl;
            _pageCrawler.CrawlerMessage += PageCrawlerOnCrawlerMessage;

            _isInitialized = true;
        }

        private void PageCrawlerOnCrawlerMessage(object sender, CrawlerMessageEventArgs e)
        {
            EventHandler<CrawlerMessageEventArgs> handler = CrawlerMessage;
            handler?.Invoke(this, e);
        }

        private void PageCrawlerOnNewCrawledUrl(object sender, NewCrawledUrlEventArgs e)
        {
            EventHandler<NewCrawledUrlEventArgs> handler = NewCrawledUrl;
            handler?.Invoke(this, e);
        }

        private void PageCrawlerOnPostCrawlEnd(object sender, PostCrawlEventArgs e)
        {
            EventHandler<PostCrawlEventArgs> handler = PostCrawlEnd;
            handler?.Invoke(this, e);
        }

        private void PageCrawlerOnPostCrawlStart(object sender, PostCrawlEventArgs e)
        {
            EventHandler<PostCrawlEventArgs> handler = PostCrawlStart;
            handler?.Invoke(this, e);
        }

        private void DownloadManagerOnFileDownloaded(object sender, FileDownloadedEventArgs e)
        {
            EventHandler<FileDownloadedEventArgs> handler = FileDownloaded;
            handler?.Invoke(this, e);
        }

        private void OnStatusChanged(DownloaderStatusChangedEventArgs e)
        {
            EventHandler<DownloaderStatusChangedEventArgs> handler = StatusChanged;
            handler?.Invoke(this, e);
        }

        public void Dispose()
        {
            _initializationSemaphore?.Dispose();
            ((IDisposable)_puppeteerEngine)?.Dispose();
        }
    }
}
