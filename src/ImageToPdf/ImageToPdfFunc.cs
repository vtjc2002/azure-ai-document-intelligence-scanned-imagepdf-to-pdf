using System.IO;
using System.Threading.Tasks;
using Azure;
using Azure.AI.FormRecognizer.DocumentAnalysis;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;
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

            ms.Dispose();

            _logger.LogInformation($"C# Blob trigger function Processed blob\n Name: {name} \n Data:");
        }

    }
}
