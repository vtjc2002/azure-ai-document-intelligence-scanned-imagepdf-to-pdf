using System.Threading.Tasks;
using Azure;
using Azure.AI.FormRecognizer.DocumentAnalysis;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace ImageToPdf
{
    public class ImageToPdfFunc
    {
        private readonly ILogger<ImageToPdfFunc> _logger;

        public ImageToPdfFunc(ILogger<ImageToPdfFunc> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Azure Function triggered by a blob event.  The blob is analyzed using Azure Document Intelligence and the result is written to a txt file in the configured ProcessedContainer container
        /// </summary>
        [Function("ImageToPdfFuncBlobEventTrigger")]
        public async Task ImageToPdfBlobEventTrigger([BlobTrigger("scanned-images/{name}", Source = BlobTriggerSource.EventGrid, Connection = "ImageSourceStorage")] Stream inputStream, string name)
        {

            // get Azure Document Intelligence endpoint and key from environment variables
            var docIntelEndpoint = System.Environment.GetEnvironmentVariable("AzureDocumentIntelligenceEndpoint");
            var docIntelKey = System.Environment.GetEnvironmentVariable("AzureDocumentIntelligenceKey");

            // create a client
            var credential = new AzureKeyCredential(docIntelKey);
            var client = new DocumentAnalysisClient(new Uri(docIntelEndpoint), credential);

            try
            {

                // convert the stream to a memory stream.  This is needed as the AnalyzeDocumentAsync cannot read diretly from the blob uri that's not publicly accessible
                using var ms = new MemoryStream();
                inputStream.CopyTo(ms);
                ms.Position = 0;

                // analyze the document with prebuilt-document model
                AnalyzeDocumentOperation operation = await client.AnalyzeDocumentAsync(WaitUntil.Completed, modelId: "prebuilt-document", document: ms);
                AnalyzeResult result = operation.Value;

                // process the AnalyzeResult
                await ProcessAnalyzeResult(result, name);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error analyzing document: {ex.Message}");
            }

            _logger.LogInformation($"C# Blob trigger function Processed blob\n Name: {name} ");

            //TODO: Notifiy the user that the document has been processed.  
            //The recommendation is to use Azure Event Grid.  See https://docs.microsoft.com/en-us/azure/event-grid/overview
        }

        /// <summary>
        /// Process the AnalyzeResult and group paragraphs by page and use stringbuilder to create a string for each page.  The result is then written to a txt file in the configured ProcessedContainer container
        /// </summary>
        /// <param name="result">result from DocumentAnalysis</param>
        /// <param name="filename">file name</param>
        /// <returns></returns>
        private async Task ProcessAnalyzeResult(AnalyzeResult result, string filename)
        {

            // group paragraphs by page
            var pageResults = result.Paragraphs.GroupBy(g => g.BoundingRegions[0].PageNumber).Select(s => new { PageNumber = s.Key, Paragraphs = s.Select(sm => sm.Content) });

            // loop through each page
            for (int i = 0; i < pageResults.Count(); i++)
            {
                // create a stringbuilder to hold the text for each page
                var sb = new System.Text.StringBuilder();

                // loop through each paragraph on the page and add it to the stringbuilder
                foreach (var paragraph in pageResults.ElementAt(i).Paragraphs)
                {
                    sb.AppendLine(paragraph);
                }

                // write the stringbuilder to a txt file
                await WriteAnalyzeResultToBlob(sb.ToString(), $"{filename}-page-{i + 1}");

            }

        }

        /// <summary>
        /// Write teh AnalyzeResult to txt files in the configured ProcessedContainer container
        /// </summary>
        /// <param name="result">plain text to output</param>
        /// <param name="filename">file name</param>
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

            using (var ms = new MemoryStream())
            using (var sw = new StreamWriter(ms))
            {
                sw.Write(result);
                sw.Flush();
                ms.Position = 0;

                // try writing the text to the blob
                try
                {
                    await blobClient.UploadAsync(content: ms, overwrite: true);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error writing to blob: {ex.Message}");
                }

            }

        }
    }
}
