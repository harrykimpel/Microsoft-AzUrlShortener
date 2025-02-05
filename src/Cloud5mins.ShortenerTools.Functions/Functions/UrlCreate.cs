/*
```c#
Input:

    {
        // [Required] The url you wish to have a short version for
        "url": "https://docs.microsoft.com/en-ca/azure/azure-functions/functions-create-your-first-function-visual-studio",
        
        // [Optional] Title of the page, or text description of your choice.
        "title": "Quickstart: Create your first function in Azure using Visual Studio"

        // [Optional] the end of the URL. If nothing one will be generated for you.
        "vanity": "azFunc"
    }

Output:
    {
        "ShortUrl": "http://c5m.ca/azFunc",
        "LongUrl": "https://docs.microsoft.com/en-ca/azure/azure-functions/functions-create-your-first-function-visual-studio"
    }
*/

using Cloud5mins.ShortenerTools.Core.Domain;
using Cloud5mins.ShortenerTools.Core.Messages;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Azure.Storage.Blobs;
using System.Net.Http;

namespace Cloud5mins.ShortenerTools.Functions
{

    public class UrlCreate
    {
        private readonly ILogger _logger;
        private readonly ShortenerSettings _settings;

        public UrlCreate(ILoggerFactory loggerFactory, ShortenerSettings settings)
        {
            _logger = loggerFactory.CreateLogger<UrlList>();
            _settings = settings;
        }

        [Function("UrlCreate")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = "api/UrlCreate")] HttpRequestData req,
            ExecutionContext context
        )
        {
            _logger.LogInformation($"__trace creating shortURL: {req}");
            string userId = string.Empty;
            ShortRequest input;
            var result = new ShortResponse();

            try
            {
                // Validation of the inputs
                if (req == null)
                {
                    return req.CreateResponse(HttpStatusCode.NotFound);
                }

                using (var reader = new StreamReader(req.Body))
                {
                    var strBody = await reader.ReadToEndAsync();
                    input = JsonSerializer.Deserialize<ShortRequest>(strBody, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (input == null)
                    {
                        return req.CreateResponse(HttpStatusCode.NotFound);
                    }
                }

                // If the Url parameter only contains whitespaces or is empty return with BadRequest.
                if (string.IsNullOrWhiteSpace(input.Url))
                {
                    var badResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badResponse.WriteAsJsonAsync(new { Message = "The url parameter can not be empty." });
                    return badResponse;
                }

                // Validates if input.url is a valid aboslute url, aka is a complete refrence to the resource, ex: http(s)://google.com
                if (!Uri.IsWellFormedUriString(input.Url, UriKind.Absolute))
                {
                    var badResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badResponse.WriteAsJsonAsync(new { Message = $"{input.Url} is not a valid absolute Url. The Url parameter must start with 'http://' or 'http://'." });
                    return badResponse;
                }

                StorageTableHelper stgHelper = new StorageTableHelper(_settings.DataStorage);

                string longUrl = input.Url.Trim();
                string vanity = string.IsNullOrWhiteSpace(input.Vanity) ? "" : input.Vanity.Trim();
                string title = string.IsNullOrWhiteSpace(input.Title) ? "" : input.Title.Trim();

                ShortUrlEntity newRow;

                var qrCodeUrl = await GetQRCode(longUrl);
                if (!string.IsNullOrEmpty(vanity))
                {
                    newRow = new ShortUrlEntity(longUrl, vanity, title, input.Schedules, qrCodeUrl);
                    if (await stgHelper.IfShortUrlEntityExist(newRow))
                    {
                        var badResponse = req.CreateResponse(HttpStatusCode.Conflict);
                        await badResponse.WriteAsJsonAsync(new { Message = "This Short URL already exist." });
                        return badResponse;
                    }
                }
                else
                {
                    newRow = new ShortUrlEntity(longUrl, await Utility.GetValidEndUrl(vanity, stgHelper), title, input.Schedules, qrCodeUrl);
                }

                await stgHelper.SaveShortUrlEntity(newRow);

                var host = string.IsNullOrEmpty(_settings.CustomDomain) ? req.Url.Host : _settings.CustomDomain.ToString();
                result = new ShortResponse(host, newRow.Url, newRow.RowKey, newRow.Title, newRow.QrCodeUrl);

                _logger.LogInformation("Short Url created.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An unexpected error was encountered.");

                var badResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await badResponse.WriteAsJsonAsync(new { ex.Message });
                return badResponse;
            }

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(result);

            return response;
        }

        private async Task<string> GetQRCode(string shortUrl)
        {
            string qrCodeUrl = "";

            try
            {
                // Create the QR Code
                var redirectUrl = "https://api.qrserver.com/v1/create-qr-code/?color=000000&bgcolor=FFFFFF&data=" + WebUtility.UrlEncode(shortUrl) + "&qzone=0&margin=0&size=250x250&ecc=L";
                //_logger.LogInformation($"redirectUrl: {redirectUrl}");
                HttpClient client = new HttpClient();
                var response = await client.GetAsync(redirectUrl);

                if (response != null && response.StatusCode == HttpStatusCode.OK)
                {
                    _logger.LogInformation($"response status: {response.StatusCode}");

                    using (var stream = await response.Content.ReadAsStreamAsync())
                    {
                        _logger.LogInformation($"ReadAsStreamAsync successful");

                        var memStream = new MemoryStream();
                        await stream.CopyToAsync(memStream);
                        memStream.Position = 0;

                        // Save file as image on Azure blob storage
                        var blobStorageConnectionString = _settings.BlobStorageConnectionString;
                        var blobServiceClient = new BlobServiceClient(blobStorageConnectionString);
                        var blobStorageContainer = _settings.BlobStorageContainer;
                        var containerClient = blobServiceClient.GetBlobContainerClient(blobStorageContainer);
                        var blobClient = containerClient.GetBlobClient($"{Guid.NewGuid()}.png");

                        await blobClient.UploadAsync(memStream, true);

                        // Return the URL to the image
                        qrCodeUrl = blobClient.Uri.ToString();
                        _logger.LogInformation($"qrCodeUrl: {qrCodeUrl}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An unexpected error was encountered.");
                _logger.LogError($"error message: {ex.Message} - {ex.StackTrace}");
            }

            return qrCodeUrl;
        }
    }
}
