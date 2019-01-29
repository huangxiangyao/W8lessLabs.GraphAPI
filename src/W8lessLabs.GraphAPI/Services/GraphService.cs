﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace W8lessLabs.GraphAPI
{
    public class GraphService : IGraphService
    {
        internal const string GraphEndpoint = "https://graph.microsoft.com/v1.0";
        internal const string GraphEndpoint_Me = GraphEndpoint+ "/me";
        internal const string GraphEndpoint_Drives = GraphEndpoint + "/drives";
        internal const string GraphEndpoint_DriveRoot = GraphEndpoint+ "/me/drive/root";
        internal const string GraphEndpoint_DriveItems = GraphEndpoint + "/me/drive/items";
        internal const string GraphEndpoint_DriveRootChildren = GraphEndpoint + "/me/drive/root/children";
        internal const string GraphEndpoint_Delta = GraphEndpoint + "/me/drive/root/delta";

        internal const string SkipTokenParam = "$skipToken=";
        internal const string DeltaTokenParam = "token=";

        internal const int MaxSingleFileUploadSize = 1048576 * 4; // 4 MiB

        private readonly IAuthService _authService;
        private readonly IHttpService _http;
        private readonly IJsonSerializer _json;

        // Tokens cache
        private Dictionary<string, AccountToken> _accountTokens;

        // keep a small LRU queue of drive items
        private LinkedList<LRUCacheEntry<GetDriveItemsResponse>> _itemsCache;
        private const int CacheLimit = 30;
        private const int MaxCacheAgeSeconds = 120;

        public GraphService(IAuthService authService, IHttpService http, IJsonSerializer json)
        {
            _authService = authService ?? throw new ArgumentNullException(nameof(authService));
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _json = json ?? throw new ArgumentNullException(nameof(json));
            _itemsCache = new LinkedList<LRUCacheEntry<GetDriveItemsResponse>>();
            _accountTokens = new Dictionary<string, AccountToken>();
        }

        private async Task<(bool tokenSuccess, string token)> _TryGetTokenAsync(GraphAccount account)
        {
            bool tokenSuccess = false;
            string token = null;
            DateTimeOffset tokenExpires = default;

            if(_accountTokens.TryGetValue(account.AccountId, out AccountToken cachedToken))
            {
                tokenSuccess = true;
                token = cachedToken.Token;
                tokenExpires = cachedToken.TokenExpires;
            }

            if(token == null || tokenExpires == default || DateTimeOffset.Now > (tokenExpires - TimeSpan.FromSeconds(30)))
            {
                GraphAuthResponse auth;
                (tokenSuccess, auth) = await _authService.TryGetTokenAsync(account);
                if(tokenSuccess)
                {
                    token = auth.AccessToken;
                    tokenExpires = auth.TokenExpires;
                    
                    // cache the token
                    _accountTokens[account.AccountId] = new AccountToken(token, tokenExpires);
                }
            }

            return (tokenSuccess, token);
        }

        private bool _TryGetRequestUrlForFolder(string path, out string requestUrl)
        {
            if (string.IsNullOrEmpty(path) || path == "/")
                requestUrl = GraphEndpoint_DriveRoot;
            else
                requestUrl = GraphEndpoint_DriveRoot + ":" + Uri.EscapeUriString(path);
            return true;
        }

        private bool _TryGetRequestUrlForChildren(string path, out string requestUrl)
        {
            if (string.IsNullOrEmpty(path) || path == "/")
                requestUrl = GraphEndpoint_DriveRootChildren;
            else
                requestUrl = GraphEndpoint_DriveRoot + ":" + Uri.EscapeUriString(path) + ":/children";
            return true;
        }

        private bool _TryGetRequestUrlForContent(string path, out string requestUrl)
        {
            if (string.IsNullOrEmpty(path) || path == "/")
                requestUrl = GraphEndpoint_DriveRootChildren;
            else
                requestUrl = GraphEndpoint_DriveRoot + ":" + Uri.EscapeUriString(path) + ":/content";
            return true;
        }

        private bool _TryGetRequestUrlForPermissions(string path, out string requestUrl)
        {
            if (string.IsNullOrEmpty(path) || path == "/")
                requestUrl = GraphEndpoint_DriveRoot + "/permissions";
            else
                requestUrl = GraphEndpoint_DriveRoot + ":" + Uri.EscapeUriString(path) + ":/permissions";
            return true;
        }

        private bool _TryGetRequestUrlForPermissionsById(string driveItemId, out string requestUrl)
        {
            if (string.IsNullOrEmpty(driveItemId))
            {
                requestUrl = string.Empty;
                return false;
            }
            else
                requestUrl = GraphEndpoint_DriveItems + "/" + Uri.EscapeUriString(driveItemId) + "/permissions";
            return true;
        }

        private bool _TryGetRequestUrlWithPaging(GetDriveItemsRequest request, out string requestUrl)
        {
            if (_TryGetRequestUrlForChildren(request?.Path, out requestUrl))
            {
                int pageSize = Math.Max(request.PageSize, GetDriveItemsRequest.MinimumPageSize);
                requestUrl += "?$top=" + pageSize;

                if (!string.IsNullOrEmpty(request.SkipToken))
                    requestUrl += "&$skipToken=" + WebUtility.UrlEncode(request.SkipToken);

                return true;
            }
            else
                requestUrl = null;
            return false;
        }

        private string _GetSkipToken(string nextLink)
        {
            string skipToken = null;
            if(!string.IsNullOrEmpty(nextLink))
            {
                int index = nextLink.IndexOf(SkipTokenParam, StringComparison.OrdinalIgnoreCase);
                if(index != -1)
                {
                    skipToken = nextLink.Substring(index + SkipTokenParam.Length);
                    index = skipToken.IndexOf('&');
                    if(index != -1) // there's another parameter to trim off...
                        skipToken = skipToken.Substring(0, index);
                }
            }
            return skipToken;
        }

        private HttpServiceHeadersScope _WithAuthHeader(string token) =>
            _http.WithHeaders(("Authorization", "Bearer " + token));

        private T _UnwrapResponse<T>(HttpResponseValue<T> response)
        {
            if (response.Success)
                return response.Value;
            else
            {
                return default;
            }
        }

        public async Task<GraphUser> GetMeAsync(GraphAccount account)
        {
            (bool tokenSuccess, string token) = await _TryGetTokenAsync(account).ConfigureAwait(false);
            if (tokenSuccess)
            {
                using (_WithAuthHeader(token))
                    return _UnwrapResponse(await _http.GetJsonAsync<GraphUser>(GraphEndpoint_Me).ConfigureAwait(false));
            }
            else
                return null;
        }

        public async Task<GetDriveItemsResponse> GetDriveItemsAsync(GraphAccount account, GetDriveItemsRequest request)
        {
            if (request != null)
            {
                if (request.AllowCache && _TryGetFromCache(request.GetCacheKey(), out GetDriveItemsResponse cachedResponse))
                    return cachedResponse;
                else
                {
                    if (_TryGetRequestUrlWithPaging(request, out string requestUrl))
                    {
                        (bool tokenSuccess, string token) = await _TryGetTokenAsync(account).ConfigureAwait(false);
                        if (tokenSuccess)
                        {
                            using (_WithAuthHeader(token))
                            {
                                var response = await _http.GetJsonAsync<GraphResponse<DriveItem[]>>(requestUrl).ConfigureAwait(false);
                                if (response.Success)
                                {
                                    var graphResponse = response.Value;
                                    var driveItems = new GetDriveItemsResponse(graphResponse.Value, _GetSkipToken(graphResponse.NextLink));
                                    _AddToCache(request, driveItems);
                                    return driveItems;
                                }
                                else
                                    return default;
                            }
                        }
                    }
                }
            }
            return null;
        }

        public async Task<GetDriveItemsDeltaResponse> GetDriveItemsDeltaAsync(GraphAccount account, string deltaOrNextLink = null)
        {
            string requestUrl = GraphEndpoint_DriveRoot + "/delta";
            if (!string.IsNullOrEmpty(deltaOrNextLink))
                requestUrl = deltaOrNextLink;

            (bool tokenSuccess, string token) = await _TryGetTokenAsync(account).ConfigureAwait(false);
            if (tokenSuccess)
            {
                using (_WithAuthHeader(token))
                {
                    var response = await _http.GetJsonAsync<GraphResponse<DriveItem[]>>(requestUrl).ConfigureAwait(false);
                    if (response.Success)
                    {
                        var graphResponse = response.Value;
                        var driveItems = new GetDriveItemsDeltaResponse(graphResponse.Value, graphResponse.NextLink, graphResponse.DeltaLink);
                        return driveItems;
                    }
                    else
                        return default;
                }
            }
            return null;
        }

        public string GetDeltaLinkFromToken(string deltaToken)
        {
            if (deltaToken is null) throw new ArgumentNullException(nameof(deltaToken));

            return GraphEndpoint_Delta + "?" + DeltaTokenParam + Uri.EscapeDataString(deltaToken);
        }

        public async Task<int> GetChildItemsCountAsync(GraphAccount account, string path)
        {
            int count = 0;
            if (_TryGetRequestUrlForFolder(path, out string requestUrl))
            {
                requestUrl += "?expand=children(select=id)";

                (bool tokenSuccess, string token) = await _TryGetTokenAsync(account).ConfigureAwait(false);
                if (tokenSuccess)
                {
                    using (_WithAuthHeader(token))
                    {
                        var response = await _http.GetJsonAsync<DriveItem>(requestUrl).ConfigureAwait(false);
                        if(response.Success)
                            count = response.Value?.IsFolder() == true ? response.Value.Folder.ChildCount : 0;
                    }
                }
            }
            return count;
        }

        public async Task<IEnumerable<Permission>> GetPermissionsAsync(GraphAccount account, string path)
        {
            if (_TryGetRequestUrlForPermissions(path, out string requestUrl))
            {
                (bool tokenSuccess, string token) = await _TryGetTokenAsync(account).ConfigureAwait(false);
                if (tokenSuccess)
                {
                    using (_WithAuthHeader(token))
                    {
                        return _UnwrapResponse(
                            await _http.GetJsonAsync<GraphResponse<Permission[]>>(requestUrl).ConfigureAwait(false))?.Value;
                    }
                }
            }
            return Array.Empty<Permission>();
        }

        public async Task<IEnumerable<Permission>> GetPermissionsByIdAsync(GraphAccount account, string driveItemId)
        {
            if (_TryGetRequestUrlForPermissionsById(driveItemId, out string requestUrl))
            {
                (bool tokenSuccess, string token) = await _TryGetTokenAsync(account).ConfigureAwait(false);
                if (tokenSuccess)
                {
                    using (_WithAuthHeader(token))
                    {
                        return _UnwrapResponse(
                            await _http.GetJsonAsync<GraphResponse<Permission[]>>(requestUrl).ConfigureAwait(false))?.Value;
                    }
                }
            }
            return Array.Empty<Permission>();
        }

        public async Task<DriveItem> CreateFolderAsync(GraphAccount account, string path, string newFolderName)
        {
            if (_TryGetRequestUrlForChildren(path, out string requestUrl))
            {
                (bool tokenSuccess, string token) = await _TryGetTokenAsync(account).ConfigureAwait(false);
                if (tokenSuccess)
                {
                    using (_WithAuthHeader(token))
                    {
                        var createFolder = new
                        {
                            Name = newFolderName,
                            Folder = new Folder()
                        };
                        _UnwrapResponse(await _http.PostJsonAsync<DriveItem>(requestUrl, _json.Serialize(createFolder)).ConfigureAwait(false));
                    }
                }
            }
            return null;
        }

        private async Task<DriveItem> _UploadSmallFileAsync(GraphAccount account, FileUploadRequest request, Stream fileContent)
        {
            string requestUrl = GraphEndpoint_DriveRoot + ":" + Uri.EscapeUriString(request.GetFullFilePath()) + ":/content";

            (bool tokenSuccess, string token) = await _TryGetTokenAsync(account).ConfigureAwait(false);
            if (tokenSuccess)
            {
                using (_WithAuthHeader(token))
                    return _UnwrapResponse(await _http.PutBinaryAsync<DriveItem>(requestUrl, fileContent).ConfigureAwait(false));
            }
            return default;
        }

        public async Task<DriveItem> _UploadLargeFileAsync(GraphAccount account, FileUploadRequest request, Stream fileContent)
        {
            string createSessionUrl = GraphEndpoint_DriveRoot + ":" + Uri.EscapeUriString(request.GetFullFilePath()) + ":/createUploadSession";
            (bool tokenSuccess, string token) = await _TryGetTokenAsync(account).ConfigureAwait(false);
            if (tokenSuccess)
            {
                using (_WithAuthHeader(token))
                {
                    string replace = request.Overwrite ? "replace" : "fail";
                    string fileName = _json.Serialize(request.FileName);
                    //string description = _json.Serialize(request.Description); // Only supported on ODrive Personal
                    //string fileSystemInfo = @"{ ""odata.type"": ""microsoft.graph.fileSystemInfo"" }"; // TODO - support adding FSInfo

                    string createSessionRequest = $@"{{
    ""item"": {{
        ""@microsoft.graph.conflictBehavior"": ""{replace}"",
        ""name"": {fileName}
    }}
}}";
                    var createSessionResponse = _UnwrapResponse(await _http.PostJsonAsync<CreateUploadSessionResponse>(createSessionUrl, createSessionRequest).ConfigureAwait(false));
                    if (createSessionResponse != null)
                    {
                        using (var session = new FileUploadSession(request,
                            createSessionResponse.UploadUrl,
                            createSessionResponse.ExpirationDateTime,
                            _http,
                            _json))
                        {
                            await session.UploadAsync(fileContent, request.FileSize).ConfigureAwait(false);
                        }
                    }
                }
            }
            return default;
        }

        public async Task<DriveItem> UploadFileAsync(GraphAccount account, FileUploadRequest request, Stream fileContent)
        {
            if (string.IsNullOrEmpty(request.FilePath))
                throw new ArgumentException("FilePath", "A File Path is required.");

            if (string.IsNullOrEmpty(request.FileName))
                throw new ArgumentException("FileName", "A File Name is required.");

            return await _UploadLargeFileAsync(account, request, fileContent).ConfigureAwait(false);
        }

        private bool _TryGetFromCache(string cacheKey, out GetDriveItemsResponse cachedResponse)
        {
            var entry = _itemsCache.FirstOrDefault(i => i.Key == cacheKey);
            bool found = false;

            if (entry != null)
            {
                if ((DateTime.Now - entry.Created).TotalSeconds > MaxCacheAgeSeconds)
                {
                    _itemsCache.Remove(entry);
                    cachedResponse = default;
                }
                else
                {
                    entry.LastUsed = DateTime.Now;
                    cachedResponse = entry.Value;
                    found = true;
                }
            }
            else
                cachedResponse = default;

            return found;
        }

        private void _AddToCache(GetDriveItemsRequest request, GetDriveItemsResponse response)
        {
            if (!_TryGetFromCache(request.GetCacheKey(), out GetDriveItemsResponse cachedResponse))
            {
                if (_itemsCache.Count > CacheLimit)
                {
                    var prune = _itemsCache.OrderBy(c => c.LastUsed).First();
                    _itemsCache.Remove(prune);
                }
                _itemsCache.AddFirst(new LRUCacheEntry<GetDriveItemsResponse>()
                {
                    Key = request.GetCacheKey(),
                    Value = response,
                    LastUsed = DateTime.Now,
                    Created = DateTime.Now
                });
            }
        }

        private class AccountToken
        {
            public AccountToken(string token, DateTimeOffset tokenExpires)
            {
                Token = token;
                TokenExpires = tokenExpires;
            }

            public string Token;
            public DateTimeOffset TokenExpires;
        }

        private class LRUCacheEntry<T>
        {
            public string Key { get; set; }
            public DateTime LastUsed { get; set; }
            public DateTime Created { get; set; }
            public T Value { get; set; }
        }

        private class CreateUploadSessionResponse
        {
            public string UploadUrl { get; set; }
            public DateTimeOffset ExpirationDateTime { get; set; }
        }
    }
}
