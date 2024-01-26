using System.IO;
using System.Threading.Tasks;
using Azure;
using Azure.AI.FormRecognizer.DocumentAnalysis;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace ImageToPdf
{
    public class ImageToPdfFunc
    {
        private readonly ILogger<ImageToPdfFunc> _logger;
        private readonly string _scannedImagesContainerName = "scanned-images";

        public ImageToPdfFunc(ILogger<ImageToPdfFunc> logger)
        {
            _logger = logger;
        }

        [Function("ImageToPdfFuncBlobEventTrigger")]
        public async Task ImageToPdfBlobEventTrigger([BlobTrigger("scanned-images/{name}", Source = BlobTriggerSource.EventGrid, Connection = "ImageSourceStorage")] Stream inputStream, string name)
        {

            // get Azure Document Intelligence endpoint and key from environment variables
            var docIntelEndpoint = System.Environment.GetEnvironmentVariable("AzureDocumentIntelligenceEndpoint");
            var docIntelKey = System.Environment.GetEnvironmentVariable("AzureDocumentIntelligenceKey");

            // create a client
            var credential = new AzureKeyCredential(docIntelKey);
            var client = new DocumentAnalysisClient(new Uri(docIntelEndpoint), credential);

            // convert the stream to a memory stream.  This is needed as the AnalyzeDocumentAsync method requires a seekable stream
            using var ms = new MemoryStream();
            inputStream.CopyTo(ms);
            ms.Position = 0;


            AnalyzeDocumentOperation operation = await client.AnalyzeDocumentAsync(WaitUntil.Completed, modelId: "prebuilt-document", document: ms);
            AnalyzeResult result = operation.Value;

            // process the AnalyzeResult
            await ProcessAnalyzeResult(result, name);

            ms.Dispose();

            _logger.LogInformation($"C# Blob trigger function Processed blob\n Name: {name} \n Data:");
        }

        /// <summary>
        /// Process the AnalyzeResult and group paragraphs by page and use stringbuilder to create a string for each page
        /// </summary>
        /// <param name="result"></param>
        /// <param name="filename"></param>
        /// <returns></returns>
        private async Task ProcessAnalyzeResult(AnalyzeResult result, string filename)
        {
            // create a stringbuilder to hold the text for each page
            var sb = new System.Text.StringBuilder();

            // group paragraphs by page
            var pageResults = result.Paragraphs.GroupBy(g => g.BoundingRegions[0].PageNumber).Select(s => new { PageNumber = s.Key, Paragraphs = s.Select(sm => sm.Content) });

            // loop through each page
            for (int i = 0; i < pageResults.Count(); i++)
            {
                // loop through each paragraph on the page and add it to the stringbuilder
                foreach (var paragraph in pageResults.ElementAt(i).Paragraphs)
                {
                    sb.AppendLine(paragraph);
                }

                try { 
                    // write the stringbuilder to a txt file
                    await WriteAnalyzeResultToBlob(sb.ToString(), $"{filename}-page-{i + 1}");
                }
                catch (RequestFailedException ex)
                {
                    _logger.LogError($"Error writing to blob: {ex.Message}");
                }

            }
        }

        /// <summary>
        /// Write teh AnalyzeResult to txt files in the parsed-text container
        /// </summary>
        /// <param name="result"></param>
        /// <param name="filename"></param>
        /// <returns></returns>
        private async Task WriteAnalyzeResultToBlob(string result, string filename)
        {
            // get Azure Storage connection string from environment variable
            var storageConnectionString = System.Environment.GetEnvironmentVariable("ImageSourceStorage");

            // create a client
            var blobServiceClient = new BlobServiceClient(storageConnectionString);

            // get a reference to the container
            var containerClient = blobServiceClient.GetBlobContainerClient(Environment.GetEnvironmentVariable("ProcessedContainer"));

            // create the container if it doesn't exist
            await containerClient.CreateIfNotExistsAsync();

            // get a reference to the blob
            var blobClient = containerClient.GetBlobClient($"{filename}.txt");


            // convert result to a memory stream
            using var ms = new MemoryStream();
            using var sw = new StreamWriter(ms);

            sw.Write(result);
            sw.Flush();
            ms.Position = 0;

            // write the text to the blob
            await blobClient.UploadAsync(content: ms, overwrite: true);

            
        }
    }
}
