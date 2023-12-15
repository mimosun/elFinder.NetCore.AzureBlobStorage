# elFinder.NetCore.AzureBlobStorage
Microsoft Azure Blob Storage driver for elFinder.NetCore

<img src="https://github.com/mimosun/elFinder.NetCore.AzureBlobStorage/blob/main/_misc/logo.png" alt="logo" width="350" />
<img src="https://github.com/mimosun/elFinder.NetCore.AzureBlobStorage/blob/main/_misc/azureblobstorage.png" alt="logo" width="350" />

## Instructions

1. Install the NuGet package: https://www.nuget.org/packages/elFinder.NetCore.AzureBlobStorage.Combine/

2. Look at the [demo project](https://github.com/mimosun/elFinder.NetCore.AzureBlobStorage/tree/main/src/elFinder.NetCore.AzureBlobStorage.Web) for an example on how to integrate it into your own project.

## Azure Blob Storage Connector

In order to use the Azure Blob Storage Connector

1. Open your **appsettings.json** file and look for the **AzureBlobStorage** section:

> Replace `ConnectionString`, `ContainerName` and `OriginHostName` with the appropriate values for your Azure account.

2. The thumbnails are stored in the local file system in folder **./wwwroot/thumbnails**. You can change this folder location in `ElFinderController` constructor.

## Description

This plugin is based on the project [**elFinder.NetCore.AzureBlobStorage**](https://github.com/brunomel/elFinder.NetCore.AzureBlobStorage) by [Bruno Melegari](https://github.com/brunomel) and combine source code from [**elFinder.NetCore**](https://github.com/gordon-matt/elFinder.NetCore) by [Matt Gordon](https://github.com/gordon-matt)

Improvement:
* Allow store files with child location instead of just at Azure container root
* Fix [**Checkmarx© advisory: CVE-2021-23427 (Severity: Critical)**](https://devhub.checkmarx.com/cve-details/CVE-2021-23427)
* Fix [**Checkmarx© advisory: CVE-2021-23428 (Severity: Critical)**](https://devhub.checkmarx.com/cve-details/CVE-2021-23428)

