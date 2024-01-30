# Azure Document Intelligence SDK with Blob Storage Event Trigger

This project uses the Azure Document Intelligence SDK to read an image with the `prebuilt-document` model from a Blob Storage event trigger and write the results into {filename}-page-{x}.txt into the configured blob container.

## Infrastructure
Follow the below steps to provision necessary Azure infrastructure for this code sample to work using az cli and powershell.

___Please note that you should secure your network using Vnet and secrets using Azure KeyVault for production purpose.___

```powershell
# set ps variables
$randomNum = Get-Random -Minimum 100 -Maximum 999
$appName = "docintellpoc"+$randomNum
$location = "eastus"
$resourceGroup = "rg-"+ $appName
$scannedImageContainer = "scanned-images"
$processedContainer = "parased-txt"

# create resource group
az group create --name $resourceGroup --location $location

# create Storage account where the image will be uploaded
az storage account create --name "sa$appName" --resource-group $resourceGroup --location $location --sku Standard_LRS --allow-blob-public-access false

# create scanned-images container in that storage account
$storageConnstr=$(az storage account show-connection-string --name "sa$appName" --resource-group $resourceGroup --output tsv --query connectionString)

az storage container create --name $scannedImageContainer --connection-string $storageConnstr

# create scanned-images container in that storage account
az storage container create --name $processedContainer --connection-string $storageConnstr

# create Document Intelligence Service (Single-Service)
az cognitiveservices account create --name "ai-di-$appName" --resource-group $resourceGroup --kind FormRecognizer --sku S0 --location $location --assign-identity --custom-domain "ai-di-$appName"  --yes

# create Storage account for Azure Function
az storage account create --name "safunc$appName" --resource-group $resourceGroup --location $location --sku Standard_LRS --allow-blob-public-access false 

# create Azure Function
az functionapp create --consumption-plan-location $location --name "${appName}func" --os-type Windows --resource-group $resourceGroup --functions-version 4 --runtime dotnet-isolated --runtime-version 8 --storage-account "safunc$appName" 

```

Now we will configure Azure Function Application Settings

```ps
# set value for ImageSourceStorage
$imageSourceStorageConnectionString= $(az storage account show-connection-string --name "sa$appName" --resource-group $resourceGroup --output tsv --query connectionString)

az functionapp config appsettings set --name "${appName}func" --resource-group $resourceGroup --settings "ImageSourceStorage=$imageSourceStorageConnectionString"

# set value for ProcessedContainer this is the container where the processed file is storaged as filename-{txt}-{pageNumber}.txt

az functionapp config appsettings set --name "${appName}func" --resource-group $resourceGroup --settings "ProcessedContainer=$processedContainer"

# set value for Azure Document Intelligence endpoint
$docIntlEndpoint = $(az cognitiveservices account show --name "ai-di-$appName" --resource-group $resourceGroup --output tsv --query properties.endpoint)

az functionapp config appsettings set --name "${appName}func" --resource-group $resourceGroup --settings "AzureDocumentIntelligenceEndpoint=$docIntlEndpoint"

# set value for Azure Document Intelligence endpoint
$docIntlKey = $(az cognitiveservices account keys list --name "ai-di-$appName" --resource-group $resourceGroup --output tsv --query key1)

az functionapp config appsettings set --name "${appName}func" --resource-group $resourceGroup --settings "AzureDocumentIntelligenceKey=$docIntlKey"

```

## Setup

1. Clone this repository to your local machine.
2. Open the project in your preferred IDE.

## How to use

1. Upload an image to your Blob Storage account. This will trigger the Azure Function.

2. The Azure Function is set up to use the Azure Document Intelligence SDK to read the image with the `prebuilt-document` model.

3. The result of the read operation will be stored in the same storage account where the image is uploaded

## Azure Function

The Azure Function is triggered by a Blob Storage event. When a new image is uploaded to the Blob Storage, the function is triggered and the image is processed using the Azure Document Intelligence SDK.

The function uses the `prebuilt-document` model to read the image. This model is designed to extract pages and paragraphs from documents.

## Azure Document Intelligence SDK

The Azure Document Intelligence SDK is used to read the image. It uses the `prebuilt-document` model to extract pages and paragraphs from the image.

## Next steps

You can customize the Azure Function and the use of the Azure Document Intelligence SDK to suit your needs. For example, you can choose a different model to read the image, or you can modify the function to perform additional tasks after reading the image.