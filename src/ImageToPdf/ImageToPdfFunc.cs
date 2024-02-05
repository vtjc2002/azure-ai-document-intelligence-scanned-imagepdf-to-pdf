using System.Drawing;

using Azure;
using Azure.AI.FormRecognizer.DocumentAnalysis;
using Azure.Storage.Blobs;

using GrapeCity.Documents.Pdf;
using GrapeCity.Documents.Text;

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
        /// Azure Function triggered by a blob event.  The blob is analyzed using Azure Document Intelligence and the results are output to a PDF and txt files to storage account.
        /// </summary>
        [Function("ImageToPdfFuncBlobEventTrigger")]
        public async Task ImageToPdfBlobEventTrigger([BlobTrigger("scanned-images/{name}", Source = BlobTriggerSource.EventGrid, Connection = "ImageSourceStorage")] Stream inputStream, string name)
        {
            // start performance timer
            var watch = System.Diagnostics.Stopwatch.StartNew();

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

            _logger.LogInformation($"C# Blob trigger function Processed blob\n Name: {name}. \n Duration: {watch.ElapsedMilliseconds} ms");

            //TODO: Notifiy the user that the document has been processed.  
            //The recommendation is to use Azure Event Grid.  See https://docs.microsoft.com/en-us/azure/event-grid/overview
        }

        /// <summary>
        /// Process the AnalyzeResult and save the paragraphs to txt files separated by page and the pages to single PDF file.
        /// </summary>
        /// <param name="result">result from DocumentAnalysis</param>
        /// <param name="fileName">file name</param>
        /// <returns></returns>
        private async Task ProcessAnalyzeResult(AnalyzeResult result, string fileName)
        {

            // process the paragraphs and save to txt in the ProcessedTxtContainer
            await ProcessAnalyzeResultParagraphsToTxtFiles(result.Paragraphs, fileName);

            // process the pages and save to PDF in the ProcessedPdfContainer
            await ProcessAnalyzeReultPagesToPdf(result.Pages, fileName);

        }

        private async Task ProcessAnalyzeResultParagraphsToTxtFiles(IReadOnlyList<DocumentParagraph> result, string fileName)
        {
            var containerName = Environment.GetEnvironmentVariable("ProcessedTxtContainer");

            // group paragraphs by page
            var pageResults = result.GroupBy(g => g.BoundingRegions[0].PageNumber).Select(s => new { PageNumber = s.Key, Paragraphs = s.Select(sm => sm.Content) });

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

                // write the stringbuilder to memory stream
                using (var ms = new MemoryStream())
                using (var sw = new StreamWriter(ms))
                {
                    sw.Write(sb.ToString());
                    sw.Flush();
                    ms.Position = 0;

                    // write the stringbuilder to a txt file
                    await WriteAnalyzeResultToBlob(ms, containerName, $"{fileName}-page-{i + 1}.txt");
                }
            }
        }

        /// <summary>
        /// Processes the analyzed result pages and converts them to a PDF document.
        /// </summary>
        /// <param name="result">The list of analyzed document pages.</param>
        /// <param name="fileName">The name of the output PDF file.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        private async Task ProcessAnalyzeReultPagesToPdf(IReadOnlyList<DocumentPage> result, string fileName)
        {
            // Get the processed pdf container name from the environment variable
            var containerName = Environment.GetEnvironmentVariable("ProcessedPdfContainer");
            // Get the AllowedVarianceOnSingleLineCalculation from the environment variable
            var allowedVariance = double.Parse(Environment.GetEnvironmentVariable("AllowedVarianceOnSingleLineCalculation"));

            // Create a new PDF document:
            GcPdfDocument doc = new GcPdfDocument();

            // By default, DsPdf uses 72dpi:
            const float In = 72;

            // Loop through each page
            foreach (var page in result)
            {
                // Create a new page in the PDF document
                var pdfPage = doc.Pages.Add();
                // Set page size bsed on the page size from the DocumentAnalysis result
                pdfPage.Size = new SizeF((float)(page.Width * In), (float)(page.Height * In));

                // Create a new graphics object to draw on the page
                GcPdfGraphics g = pdfPage.Graphics;

                for (int iCounter = 0; iCounter < page.Words.Count; iCounter++)
                {
                    var word = page.Words[iCounter];

                    // Calculate optimal font size based on the polygon
                    var desiredTextHeight = word.BoundingPolygon[3].Y - word.BoundingPolygon[0].Y;
                    if (desiredTextHeight == 0)
                    {
                        desiredTextHeight = 0.1f;
                    }

                    // Set Font and FontSize
                    var tf = new TextFormat() { Font = StandardFonts.Times, FontSize = In * (float)desiredTextHeight, FontSizeInGraphicUnits = true };

                    var tl = g.CreateTextLayout();
                    tl.Append(word.Content, tf);

                    // Check if the next sequence of words are on the same line to keep them on the same line with proper spacing.
                    for (int wordCounter = iCounter; wordCounter < page.Words.Count - 1; wordCounter++)
                    {
                        var nextWord = page.Words[wordCounter + 1];
                        var nextWordY = nextWord.BoundingPolygon[0].Y;
                        var currentWordY = page.Words[wordCounter].BoundingPolygon[0].Y;

                        // use the allowedVariance to determine if the next word is on the same line based on word and word+1 Y coordinates
                        if ( Math.Abs(nextWordY - currentWordY) < allowedVariance)
                        {
                            tl.Append(" ", tf);
                            tl.Append(nextWord.Content, tf);
                            iCounter++;
                        }
                        else
                        {
                            break;
                        }
                    }

                    tl.PerformLayout(true);

                    // Convert inches to points
                    float xPoint = (float)(word.BoundingPolygon[0].X * In);
                    float yPoint = (float)(word.BoundingPolygon[0].Y * In);

                    // Draw the text layout on the page with starting point of upper left corner of the BoudingPolygon
                    g.DrawTextLayout(tl, new PointF(xPoint, yPoint));
                }
            }

            // Save the PDF to a memory stream
            using (var ms = new MemoryStream())
            {
                doc.Save(ms);
                ms.Position = 0;

                // write the PDF to a blob
                await WriteAnalyzeResultToBlob(ms, containerName, $"{fileName}.pdf");
            }
        }



        /// <summary>
        /// Write the plain text result to a blob storage container.
        /// </summary>
        /// <param name="ms">MemoryStream containing content to output</param>
        /// <param name="containerName">Name of the blob storage container</param>
        /// <param name="fileName">Name of the file to be written</param>
        /// <returns></returns>
        private async Task WriteAnalyzeResultToBlob(MemoryStream ms, string? containerName, string fileName)
        {
            // get Azure Storage connection string from environment variable
            var storageConnectionString = System.Environment.GetEnvironmentVariable("ImageSourceStorage");

            // create a client
            var blobServiceClient = new BlobServiceClient(storageConnectionString);

            // get a reference to the container
            var containerClient = blobServiceClient.GetBlobContainerClient(containerName);

            // create the container if it doesn't exist
            await containerClient.CreateIfNotExistsAsync();

            // get a reference to the blob
            var blobClient = containerClient.GetBlobClient($"{fileName}");

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
