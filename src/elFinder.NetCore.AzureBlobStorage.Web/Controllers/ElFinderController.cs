using System;
using System.IO;
using System.Threading.Tasks;
using elFinder.NetCore.AzureBlobStorage.Driver.Drivers.AzureBlob;
using elFinder.NetCore.Drawing;
using elFinder.NetCore.Drivers.FileSystem;
using elFinder.NetCore.Helpers;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace elFinder.NetCore.AzureBlobStorage.Web.Controllers
{
    [ApiController]
    [Route("el-finder/azure-blob-storage")]
    public class ElFinderController : ControllerBase
    {
        private readonly string _thumbnailsDir = "thumbnails";
        private readonly string _thumbnailsPath;
        private readonly int _thumbnailSize;
        private readonly string _directory = "Files";
        private readonly bool _isAzureDriver;

        public ElFinderController(IHostEnvironment hostingEnvironment, IConfiguration configuration)
        {
            // todo: change here the path were you want to locally store the thumbnail images
            _thumbnailsPath = Path.Combine(hostingEnvironment.ContentRootPath, "wwwroot", _thumbnailsDir);
            _thumbnailSize = 90;
            _isAzureDriver = configuration.GetSection("AzureBlobStorage:IsEnabled").Value == true.ToString();
        }

        [Route("connector")]
        public async Task<IActionResult> Connector()
        {
            await CreateDirectoryIfNotExists();

            var connector = GetConnector();

            return await connector.ProcessAsync(Request);
        }

        [Route("thumb/{hash}")]
        public async Task<IActionResult> Thumbs(string hash)
        {
            if (!_isAzureDriver)
                return await GetConnector().GetThumbnailAsync(HttpContext.Request, HttpContext.Response, hash);

            var filePath = HttpEncoder.DecodePath(hash);

            if (System.IO.File.Exists($"{_thumbnailsPath}{filePath}"))
                return StreamImageFromLocalFile(OpenFile(filePath));

            var image = await GenerateThumbnailFromBlobItemToLocalFile(filePath);

            return StreamImageFromLocalFile(image);
        }

        #region Private

        private ImageWithMimeType OpenFile(string path)
        {
            var filePath = $"{_thumbnailsPath}{path}";

            var ext = Path.GetExtension(filePath);

            var mimeType = MimeHelper.GetMimeType(ext);
            var stream = System.IO.File.OpenRead(filePath);

            return new ImageWithMimeType(mimeType, stream);
        }

        private static FileStreamResult StreamImageFromLocalFile(ImageWithMimeType image)
        {
            image.ImageStream.Seek(0, SeekOrigin.Begin);

            return new FileStreamResult(image.ImageStream, image.MimeType);
        }

        private async Task<ImageWithMimeType> GenerateThumbnailFromBlobItemToLocalFile(string filePath)
        {
            // We use ticks in the thumbnail filename request to be sure that the thumbnail will be re-created
            // if an image with the same filename will be uploaded again
            var lastIndexOf = filePath.LastIndexOf('_');
            var ticks = Path.GetFileNameWithoutExtension(filePath.Substring(lastIndexOf + 1,
                filePath.Length - lastIndexOf - 1));
            var originFilePath = filePath.Replace($"_{ticks}", "");

            await using var original =
                await AzureBlobStorageApi.FileStreamAsync($"{AzureBlobStorageApi.ContainerName}{originFilePath}");

            var image = new DefaultPictureEditor().GenerateThumbnail(original, _thumbnailSize, true);

            SaveFile(image.ImageStream, filePath);

            return image;
        }

        private void SaveFile(Stream stream, string path)
        {
            var filePath = $"{_thumbnailsPath}{path}";

            var fileDir = Path.GetDirectoryName(filePath);

            if (!Directory.Exists(fileDir)) Directory.CreateDirectory(fileDir);

            using var fileStream = System.IO.File.Create(filePath);

            stream.Seek(0, SeekOrigin.Begin);
            stream.CopyTo(fileStream);
        }

        private Connector GetConnector()
        {
            dynamic driver = new FileSystemDriver();

            if (_isAzureDriver) driver = new AzureBlobDriver();

            var uri = new Uri(UriHelper.BuildAbsolute(Request.Scheme, Request.Host));
            var root = new RootVolume(GetRootDirectory(),
                GetDirectoryUrl(),
                $"{uri.Scheme}://{uri.Authority}/el-finder/azure-blob-storage/thumb/")
            {
                ThumbnailSize = _thumbnailSize,

                //IsReadOnly = !User.IsInRole("Administrators")
                IsReadOnly = false, // Can be readonly according to user's membership permission
                IsLocked = false, // If locked, files and directories cannot be deleted, renamed or moved
                Alias = "Files", // Beautiful name given to the root/home folder
                //MaxUploadSizeInKb = 2048, // Limit imposed to user uploaded file <= 2048 KB
                //MaxUploadSizeInMb = 85, // Comment it to taker thw web.config value
            };

            driver.AddRoot(root);

            return new Connector(driver);
        }

        private string GetRootDirectory()
        {
            if (_isAzureDriver)
            {
                return AzureBlobStorageApi.PathCombine(AzureBlobStorageApi.ContainerName, _directory);
            }

            return PathHelper.GetFullPath(Path.Combine(_thumbnailsPath, _directory));
        }

        private string GetDirectoryUrl()
        {
            if (_isAzureDriver)
            {
                return $"{AzureBlobStorageApi.OriginHostName}/{AzureBlobStorageApi.ContainerName}/{_directory}/";
            }

            var uri = new Uri(UriHelper.BuildAbsolute(Request.Scheme, Request.Host));
            return $"{uri.Scheme}://{uri.Authority}/{_thumbnailsDir}/{_directory}/";
        }

        private async Task CreateDirectoryIfNotExists()
        {
            string directory = GetRootDirectory();

            if (_isAzureDriver)
            {
                var iExistDir = await new AzureBlobDirectory(directory).ExistsAsync;
                if (!iExistDir)
                {
                    await new AzureBlobDirectory(directory).CreateAsync();
                }
            }
            else
            {
                await new FileSystemDirectory(directory).CreateAsync();
            }
        }

        #endregion
    }
}