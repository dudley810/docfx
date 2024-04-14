﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics;
using System.Net;
using Docfx.Common;

using EnvironmentVariables = Docfx.DataContracts.Common.Constants.EnvironmentVariables;

namespace Docfx.Build.Engine;

public class XRefMapDownloader
{
    private readonly SemaphoreSlim _semaphore;
    private readonly IReadOnlyList<string> _localFileFolders;

    public XRefMapDownloader(string baseFolder = null, IReadOnlyList<string> fallbackFolders = null, int maxParallelism = 0x10)
    {
        _semaphore = new SemaphoreSlim(maxParallelism);
        if (baseFolder == null)
        {
            baseFolder = Directory.GetCurrentDirectory();
        }
        else
        {
            baseFolder = Path.Combine(Directory.GetCurrentDirectory(), baseFolder);
        }
        var localFileFolders = new List<string>() { baseFolder };
        if (fallbackFolders != null)
        {
            localFileFolders.AddRange(fallbackFolders);
        }
        _localFileFolders = localFileFolders;
    }

    /// <summary>
    /// Download xref map file from uri (async).
    /// </summary>
    /// <param name="uri">The uri of xref map file.</param>
    /// <returns>An instance of <see cref="XRefMap"/>.</returns>
    /// <threadsafety>This method is thread safe.</threadsafety>
    public async Task<IXRefContainer> DownloadAsync(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        await _semaphore.WaitAsync();
        return await Task.Run(async () =>
        {
            try
            {
                if (uri.IsAbsoluteUri)
                {
                    return await DownloadBySchemeAsync(uri);
                }
                else
                {
                    return ReadLocalFileWithFallback(uri);
                }
            }
            finally
            {
                _semaphore.Release();
            }
        });
    }

    private IXRefContainer ReadLocalFileWithFallback(Uri uri)
    {
        foreach (var localFileFolder in _localFileFolders)
        {
            var localFilePath = Path.Combine(localFileFolder, uri.OriginalString);
            if (File.Exists(localFilePath))
            {
                return ReadLocalFile(localFilePath);
            }
        }
        throw new FileNotFoundException($"Cannot find xref map file {uri.OriginalString} in path: {string.Join(",", _localFileFolders)}", uri.OriginalString);
    }

    /// <remarks>
    /// Support scheme: http, https, file.
    /// </remarks>
    protected virtual async Task<IXRefContainer> DownloadBySchemeAsync(Uri uri)
    {
        IXRefContainer result;
        if (uri.IsFile)
        {
            result = DownloadFromLocal(uri);
        }
        else if (uri.Scheme == Uri.UriSchemeHttp ||
            uri.Scheme == Uri.UriSchemeHttps)
        {
            result = await DownloadFromWebAsync(uri);
        }
        else
        {
            throw new ArgumentException($"Unsupported scheme {uri.Scheme}, expected: http, https, file.", nameof(uri));
        }
        if (result == null)
        {
            throw new InvalidDataException($"Invalid yaml file from {uri}.");
        }
        return result;
    }

    protected static IXRefContainer DownloadFromLocal(Uri uri)
    {
        var filePath = uri.LocalPath;
        return ReadLocalFile(filePath);
    }

    private static IXRefContainer ReadLocalFile(string filePath)
    {
        Logger.LogVerbose($"Reading from file: {filePath}");

        switch (Path.GetExtension(filePath).ToLowerInvariant())
        {
            case ".zip":
                return XRefArchive.Open(filePath, XRefArchiveMode.Read);

            case ".json":
                {
                    using var stream = File.OpenText(filePath);
                    return JsonUtility.Deserialize<XRefMap>(stream);
                }

            case ".yml":
            default:
                {
                    using var sr = File.OpenText(filePath);
                    return YamlUtility.Deserialize<XRefMap>(sr);
                }
        }
    }

    protected static async Task<XRefMap> DownloadFromWebAsync(Uri uri)
    {
        Logger.LogVerbose($"Reading from web: {uri.OriginalString}");

        using var httpClient = new HttpClient(new HttpClientHandler()
        {
            AutomaticDecompression = DecompressionMethods.All,
            CheckCertificateRevocationList = !EnvironmentVariables.NoCheckCertificateRevocationList,
        })
        {
            Timeout = TimeSpan.FromMinutes(30), // Default: 100 seconds
        };

        using var stream = await httpClient.GetStreamAsync(uri);

        switch (Path.GetExtension(uri.AbsolutePath).ToLowerInvariant())
        {
            case ".json":
                {
                    using var sr = new StreamReader(stream, bufferSize: 81920); // Default :1024 byte
                    var xrefMap = JsonUtility.Deserialize<XRefMap>(sr);
                    xrefMap.BaseUrl = ResolveBaseUrl(xrefMap, uri);
                    return xrefMap;
                }
            case ".yml":
            default:
                {
                    using var sr = new StreamReader(stream, bufferSize: 81920); // Default :1024 byte
                    var xrefMap = YamlUtility.Deserialize<XRefMap>(sr);
                    xrefMap.BaseUrl = ResolveBaseUrl(xrefMap, uri);
                    return xrefMap;
                }
        }
    }

    private static string ResolveBaseUrl(XRefMap map, Uri uri)
    {
        if (!string.IsNullOrEmpty(map.BaseUrl))
            return map.BaseUrl;

        // If downloaded xrefmap has no baseUrl.
        // Use xrefmap file download url as basePath.
        var baseUrl = uri.GetLeftPart(UriPartial.Path);
        return baseUrl.Substring(0, baseUrl.LastIndexOf('/') + 1);
    }
}
