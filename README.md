# Azure Document Intelligence SDK with Blob Storage Event Trigger

This project uses the Azure Document Intelligence SDK to read an image with the `prebuilt-read` model from a Blob Storage event trigger and write the results into {filename}-page-{x}.txt into the configured blob container.

## Infrastructure
Follow the below steps to provision necessary Azure infrastructure for this code sample to work using az cli and powershell.
Make sure to get the latest version of az cli and powershell before proceeding.

___Please note that you should secure your network using Vnet and secrets using Azure KeyVault for production purpose.___

___Run one command at a time to spot issues eaiser.___

```powershell
# set ps variables
$randomNum = Get-Random -Minimum 100 -Maximum 999
$appName = "docintellpoc"+$randomNum
$location = "eastus"
$resourceGroup = "rg-"+ $appName
$scannedImageContainer = "scanned-images"
$processedTxtContainer = "parased-txt"
$processedPdfContainer = "parsed-pdf"

# create resource group
az group create --name $resourceGroup --location $location

# create Storage account where the image will be uploaded
az storage account create --name "sa$appName" --resource-group $resourceGroup --location $location --sku Standard_LRS --allow-blob-public-access false

# create scanned-images container in that storage account
$storageConnstr=$(az storage account show-connection-string --name "sa$appName" --resource-group $resourceGroup --output tsv --query connectionString)

az storage container create --name $scannedImageContainer --connection-string $storageConnstr

# create Document Intelligence Service (Single-Service)
az cognitiveservices account create --name "ai-di-$appName" --resource-group $resourceGroup --kind FormRecognizer --sku S0 --location $location --assign-identity --custom-domain "ai-di-$appName"  --yes

# create Storage account for Azure Function
az storage account create --name "safunc$appName" --resource-group $resourceGroup --location $location --sku Standard_LRS --allow-blob-public-access false 

# create Azure Function
az functionapp create --consumption-plan-location $location --name "${appName}func" --os-type Windows --resource-group $resourceGroup --functions-version 4 --runtime dotnet-isolated --runtime-version 8 --storage-account "safunc$appName" 

```

Now we will configure Azure Function Application Settings

```powershell
# set value for ImageSourceStorage
$imageSourceStorageConnectionString= $(az storage account show-connection-string --name "sa$appName" --resource-group $resourceGroup --output tsv --query connectionString)

az functionapp config appsettings set --name "${appName}func" --resource-group $resourceGroup --settings "ImageSourceStorage=$imageSourceStorageConnectionString"

# set value for processedTxtContainer this is the container where the processed txt file is storaged as filename-{txt}-{pageNumber}.txt
az functionapp config appsettings set --name "${appName}func" --resource-group $resourceGroup --settings "ProcessedTxtContainer=$processedTxtContainer"

# set value for processedPdfContainer
az functionapp config appsettings set --name "${appName}func" --resource-group $resourceGroup --settings "ProcessedPdfContainer=$processedPdfContainer"

# set value for variance allowed for the parser to determine if word and word+1 is on the same line based on y position
az functionapp config appsettings set --name "${appName}func" --resource-group $resourceGroup --settings "AllowedVarianceOnSingleLineCalculation=0.02"

# set value for Azure Document Intelligence endpoint
$docIntlEndpoint = $(az cognitiveservices account show --name "ai-di-$appName" --resource-group $resourceGroup --output tsv --query properties.endpoint)

az functionapp config appsettings set --name "${appName}func" --resource-group $resourceGroup --settings "AzureDocumentIntelligenceEndpoint=$docIntlEndpoint"

# set value for Azure Document Intelligence endpoint
$docIntlKey = $(az cognitiveservices account keys list --name "ai-di-$appName" --resource-group $resourceGroup --output tsv --query key1)

az functionapp config appsettings set --name "${appName}func" --resource-group $resourceGroup --settings "AzureDocumentIntelligenceKey=$docIntlKey"

```

## Azure Function Deployment 
We are going to use VSCode and Azure Functions extension to deploy the Azure Function we just created.  You can easily use Azure DevOps Pipeline or Github Actions to setup CI/CD.

1. Clone this repository to your local machine.
2. Open the project in Visual Studio Code.
3. Install Azure Functions extension if you don't have it.
4. Click on the Azure icon on the left hand blade and navigate to the Function App that you created in previous step.
5. Right click on the Funcation App and deploy. 
**If the deloyment fails, got to Function App and set runtime to dotnet8-isolated if it's not set to that value already.  Try deploy again.

## Configure Azure Blob Event Subscription
Now the Azure Function is deployed, we need to tell storage account what webhook to call when a blob create event occurs.

```powershell
# Get storage account resource id
$resourceId=$(az storage account show --name "sa$appName" --resource-group $resourceGroup --query id -o tsv)

# Get and form function app endpoint
# Get the function app URL
$functionAppUrl=$(az functionapp show --name "${appName}func" --resource-group $resourceGroup --query defaultHostName -o tsv)

# Get the function key for ImageToPdfFuncBlobEventTrigger.  The default key name is default.
$functionKey=$(az functionapp keys list --name "${appName}func"  --resource-group $resourceGroup -o tsv --query systemKeys)

# Combine the URL and key to get the full endpoint
$endpoint="https://${functionAppUrl}/runtime/webhooks/blobs?code=$functionKey&functionName=Host.Functions.ImageToPdfFuncBlobEventTrigger"

# Create system topic on the storage account
az eventgrid system-topic create --resource-group $resourceGroup --name scanned-items-blobs-topic --location $location --topic-type Microsoft.Storage.StorageAccounts --source $resourceId

# Create event subscription to get events for blob creation to the Azure Functions
az eventgrid system-topic event-subscription create --name scanned-images-blob-created --system-topic-name scanned-items-blobs-topic --endpoint-type "WebHook" --resource-group $resourceGroup --subject-begins-with "/blobServices/default/containers/scanned-images/" --included-event-types "Microsoft.Storage.BlobCreated" --enable-advanced-filtering-on-arrays true --endpoint $endpoint

# Print out the endpoint for the next step.
echo $endpoint

```

There is currently a bug where the endpoint is not correct when using az cli.  Follow the below steps to correct it.
1. Navigate to https://portal.azure.com
2. In the search bar type in _Event Grid System Topic_ and click on it.
3. Click on _scanned-items-blobs-topic_
4. Scroll down to _Event Subscriptions_ and click on _scanned-images-blob-created_
5. Click on (change) next to the Azure Function url
6. Paste the endpoint value and cofirm selection
7. Click _Save_

Now you are ready to go.

## How to use

1. Upload an image to your Blob Storage account scanned-images container. This will trigger the Azure Function.

2. The Azure Function is set up to use the Azure Document Intelligence SDK to read the image with the `prebuilt-read` model.

3. The result of the read operation will be stored in the parased-txt container of the same storage account where the image is uploaded.

## Azure Function

The Azure Function is triggered by a Blob Storage event. When a new image is uploaded to the Blob Storage, the function is triggered and the image is processed using the Azure Document Intelligence SDK.

The function uses the `prebuilt-read` model to read the image. This model is designed to extract pages and paragraphs from documents.

## Azure Document Intelligence SDK

The Azure Document Intelligence SDK is used to read the image. It uses the `prebuilt-read` model to extract pages and paragraphs from the image.

## Next steps

You can customize the Azure Function and the use of the Azure Document Intelligence SDK to suit your needs. For example, you can choose a different model to read the image, or you can modify the function to perform additional tasks after reading the image.

## Additional References
https://techcommunity.microsoft.com/t5/ai-azure-ai-services-blog/generate-searchable-pdfs-with-azure-form-recognizer/ba-p/3652024

https://learn.microsoft.com/en-us/azure/azure-functions/functions-event-grid-blob-trigger?tabs=isolated-process%2Cnodejs-v4&pivots=programming-language-csharp

