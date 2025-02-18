﻿using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using elFinder.NetCore.AzureBlobStorage.Driver.Helpers;
using elFinder.NetCore.AzureBlobStorage.Driver.Models;
using elFinder.NetCore.Drawing;
using elFinder.NetCore.Drivers;
using elFinder.NetCore.Helpers;
using elFinder.NetCore.Models;
using elFinder.NetCore.Models.Commands;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace elFinder.NetCore.AzureBlobStorage.Driver.Drivers.AzureBlob
{

    /// <summary>
    ///     Represents a driver for AzureBlobStorage system
    /// </summary>
    public class AzureBlobDriver : BaseDriver, IDriver
    {
        private new const string VolumePrefix = "a";


        public AzureBlobDriver()
        {
            base.VolumePrefix = VolumePrefix;
            Roots = new List<RootVolume>();
        }


        #region IDriver Members

        public async Task<JsonResult> ArchiveAsync(FullPath parentPath, IEnumerable<FullPath> paths, string filename, string mimeType)
        {
            var response = new AddResponseModel();

            if (paths == null) throw new NotSupportedException();

            if (mimeType != "application/zip") throw new NotSupportedException("Only .zip files are currently supported.");

            var directoryInfo = parentPath.Directory;

            if (directoryInfo == null) return await Json(response);

            filename ??= "newfile";

            if (filename.EndsWith(".zip")) filename = filename.Replace(".zip", "");

            var newPath = AzureBlobStorageApi.PathCombine(directoryInfo.FullName, filename + ".zip");
            await AzureBlobStorageApi.DeleteFileIfExistsAsync(newPath);

            var archivePath = Path.GetTempFileName();

            var pathList = paths.ToList();

            using (var newFile = ZipFile.Open(archivePath, ZipArchiveMode.Update))
            {
                foreach (var tg in pathList)
                    if (tg.IsDirectory)
                    {
                        await AddDirectoryToArchiveAsync(newFile, tg.Directory, "");
                    }
                    else
                    {
                        var filePath = Path.GetTempFileName();
                        await File.WriteAllBytesAsync(filePath, await AzureBlobStorageApi.FileBytesAsync(tg.File.FullName));
                        newFile.CreateEntryFromFile(filePath, tg.File.Name);
                    }
            }

            await using (var stream = new FileStream(archivePath, FileMode.Open))
            {
                await AzureBlobStorageApi.PutAsync(newPath, stream);
            }

            // Cleanup
            File.Delete(archivePath);

            response.Added.Add(await CustomBaseModel.CustomCreateAsync(new AzureBlobFile(newPath), parentPath.RootVolume));

            return await Json(response);
        }


        public async Task<JsonResult> CropAsync(FullPath path, int x, int y, int width, int height)
        {
            await RemoveThumbsAsync(path);

            // Crop Image
            ImageWithMimeType image;

            await using (var stream = await path.File.OpenReadAsync())
            {
                image = path.RootVolume.PictureEditor.Crop(stream, x, y, width, height);
            }

            await AzureBlobStorageApi.PutAsync(path.File.FullName, image.ImageStream);

            var output = new ChangedResponseModel();
            output.Changed.Add(await BaseModel.CreateAsync(path.File, path.RootVolume));

            return await Json(output);
        }


        public async Task<JsonResult> DimAsync(FullPath path)
        {
            await using var stream = await AzureBlobStorageApi.FileStreamAsync(path.File.FullName);

            var response = new DimResponseModel(path.RootVolume.PictureEditor.ImageSize(stream));

            return await Json(response);
        }


        public async Task<JsonResult> DuplicateAsync(IEnumerable<FullPath> paths)
        {
            var response = new AddResponseModel();

            var pathList = paths.ToList();

            foreach (var path in pathList)
                if (path.IsDirectory)
                {
                    var parentPath = path.Directory.Parent.FullName;
                    var name = path.Directory.Name;
                    var newName = $"{parentPath}/{name} copy";

                    // Check if directory already exists
                    if (!await AzureBlobStorageApi.DirectoryExistsAsync(newName))
                    {
                        // Doesn't exist
                        await AzureBlobStorageApi.CopyDirectoryAsync(path.Directory.FullName, newName);
                    }
                    else
                    {
                        // Already exists, create numbered copy
                        var newNameFound = false;

                        for (var i = 1; i < 100; i++)
                        {
                            newName = $"{parentPath}/{name} copy {i}";

                            // Test that it doesn't exist
                            if (await AzureBlobStorageApi.DirectoryExistsAsync(newName)) continue;

                            await AzureBlobStorageApi.CopyDirectoryAsync(path.Directory.FullName, newName);
                            newNameFound = true;

                            break;
                        }

                        // Check if new name was found
                        if (!newNameFound) return Error.NewNameSelectionException($@"{parentPath}/{name} copy");
                    }

                    response.Added.Add(await BaseModel.CreateAsync(new AzureBlobDirectory(newName), path.RootVolume));
                }
                else // File
                {
                    var parentPath = path.File.Directory.FullName;
                    var name = path.File.Name.Substring(0, path.File.Name.Length - path.File.Extension.Length);
                    var ext = path.File.Extension;

                    var newName = $"{parentPath}/{name} copy{ext}";

                    // Check if file already exists
                    if (!await AzureBlobStorageApi.FileExistsAsync(newName))
                    {
                        // Doesn't exist
                        await AzureBlobStorageApi.CopyFileAsync(path.File.FullName, newName);
                    }
                    else
                    {
                        // Already exists, create numbered copy
                        var newNameFound = false;

                        for (var i = 1; i < 100; i++)
                        {
                            // Compute new name
                            newName = $@"{parentPath}/{name} copy {i}{ext}";

                            // Test that it doesn't exist
                            if (await AzureBlobStorageApi.FileExistsAsync(newName)) continue;

                            await AzureBlobStorageApi.CopyFileAsync(path.File.FullName, newName);
                            newNameFound = true;

                            break;
                        }

                        // Check if new name was found
                        if (!newNameFound) return Error.NewNameSelectionException($@"{parentPath}/{name} copy");
                    }

                    response.Added.Add(await CustomBaseModel.CustomCreateAsync(new AzureBlobFile(newName), path.RootVolume));
                }

            return await Json(response);
        }


        public async Task<JsonResult> ExtractAsync(FullPath fullPath, bool newFolder)
        {
            var response = new AddResponseModel();

            if (fullPath.IsDirectory || fullPath.File.Extension.ToLower() != ".zip") throw new NotSupportedException("Only .zip files are currently supported.");

            var rootPath = fullPath.File.Directory.FullName;

            if (newFolder)
            {
                // Azure doesn't like directory names that look like a file name i.e. blah.png
                // So iterate through the names until there's no more extension
                var path = Path.GetFileNameWithoutExtension(fullPath.File.Name);

                while (Path.HasExtension(path)) path = Path.GetFileNameWithoutExtension(path);

                rootPath = AzureBlobStorageApi.PathCombine(rootPath, path);
                var rootDir = new AzureBlobDirectory(rootPath);

                if (!await rootDir.ExistsAsync) await rootDir.CreateAsync();

                response.Added.Add(await BaseModel.CreateAsync(rootDir, fullPath.RootVolume));
            }

            // Create temp file
            var archivePath = Path.GetTempFileName();
            await File.WriteAllBytesAsync(archivePath, await AzureBlobStorageApi.FileBytesAsync(fullPath.File.FullName));

            using (var archive = ZipFile.OpenRead(archivePath))
            {
                var separator = Path.DirectorySeparatorChar.ToString();

                foreach (var entry in archive.Entries)
                    try
                    {
                        var file = AzureBlobStorageApi.PathCombine(rootPath, entry.FullName);

                        if (file.EndsWith(separator)) //directory
                        {
                            var dir = new AzureBlobDirectory(file);

                            if (!await dir.ExistsAsync) await dir.CreateAsync();

                            if (!newFolder) response.Added.Add(await BaseModel.CreateAsync(dir, fullPath.RootVolume));
                        }
                        else
                        {
                            var filePath = Path.GetTempFileName();
                            entry.ExtractToFile(filePath, true);

                            await using (var stream = new FileStream(filePath, FileMode.Open))
                            {
                                await AzureBlobStorageApi.PutAsync(file, stream);
                            }

                            File.Delete(filePath);

                            if (!newFolder) response.Added.Add(await CustomBaseModel.CustomCreateAsync(new AzureBlobFile(file), fullPath.RootVolume));
                        }
                    }
                    catch (Exception ex)
                    {
                        throw new Exception(entry.FullName, ex);
                    }
            }

            File.Delete(archivePath);

            return await Json(response);
        }


        public async Task<IActionResult> FileAsync(FullPath path, bool download)
        {
            // Check if path is directory
            if (path.IsDirectory) return new ForbidResult();

            // Check if path exists
            if (!await AzureBlobStorageApi.FileExistsAsync(path.File.FullName)) return new NotFoundResult();

            // Check if access is allowed
            if (path.RootVolume.IsShowOnly) return new ForbidResult();

            var contentType = download ? "application/octet-stream" : MimeHelper.GetMimeType(path.File.Extension);

            var stream = new MemoryStream();
            await AzureBlobStorageApi.GetAsync(path.File.FullName, stream);
            stream.Position = 0;

            return new FileStreamResult(stream, contentType);
        }


        public async Task<JsonResult> GetAsync(FullPath path)
        {
            var response = new GetResponseModel();

            // Get content
            await using (var stream = new MemoryStream())
            {
                await AzureBlobStorageApi.GetAsync(path.File.FullName, stream);
                stream.Position = 0;

                using (var reader = new StreamReader(stream))
                {
                    response.Content = await reader.ReadToEndAsync();
                }
            }

            return await Json(response);
        }


        public async Task<JsonResult> InitAsync(FullPath path, IEnumerable<string> mimeTypes)
        {
            if (path == null)
            {
                var root = Roots.FirstOrDefault(r => r.StartDirectory != null) ?? Roots.First();

                path = new FullPath(root, new AzureBlobDirectory(root.StartDirectory ?? root.RootDirectory), null);
            }

            // todo: BaseModel.CreateAsync internally calls GetDirectoriesAsync() which calls AzureBlobStorageApi.ListFilesAndDirectoriesAsync
            // todo: AzureBlobStorageApi.ListFilesAndDirectoriesAsync is then called again here few lines below;
            // todo: we should be able to reduce it to one single call 
            var response = new InitResponseModel(await BaseModel.CreateAsync(path.Directory, path.RootVolume), new Options(path));

            // Get all files and directories
            var items = AzureBlobStorageApi.ListFilesAndDirectoriesAsync(path.Directory.FullName);
            var itemList = items.ToList();

            // Add visible files
            foreach (var file in itemList.Where(i => !i.Name.EndsWith("/")))
            {
                var f = new AzureBlobFile(file);

                if (f.Attributes.HasFlag(FileAttributes.Hidden)) continue;

                var mimeTypesList = mimeTypes.ToList();

                if (mimeTypesList.Any() && !mimeTypesList.Contains(f.MimeType) && !mimeTypesList.Contains(f.MimeType.Type))
                    continue;

                response.Files.Add(await CustomBaseModel.CustomCreateAsync(f, path.RootVolume));
            }

            // Add visible directories
            foreach (var dir in itemList.Where(i => i.Name.EndsWith("/")))
            {
                var d = new AzureBlobDirectory(dir);

                if (!d.Attributes.HasFlag(FileAttributes.Hidden)) response.Files.Add(await BaseModel.CreateAsync(d, path.RootVolume));
            }

            // Add roots
            foreach (var root in Roots) response.Files.Add(await BaseModel.CreateAsync(new AzureBlobDirectory(root.RootDirectory), root));

            if (path.RootVolume.RootDirectory != path.Directory.FullName)
            {
                // Get all files and directories
                var entries = AzureBlobStorageApi.ListFilesAndDirectoriesAsync(path.RootVolume.RootDirectory);

                // Add visible directories
                foreach (var dir in entries.Where(i => i.Name.EndsWith("/")))
                {
                    var d = new AzureBlobDirectory(dir);

                    if (!d.Attributes.HasFlag(FileAttributes.Hidden)) response.Files.Add(await BaseModel.CreateAsync(d, path.RootVolume));
                }
            }

            if (path.RootVolume.MaxUploadSizeInKb.HasValue) response.Options.UploadMaxSize = $"{path.RootVolume.MaxUploadSizeInKb.Value}K";

            return await Json(response);
        }


        public async Task<JsonResult> ListAsync(FullPath path, IEnumerable<string> intersect, IEnumerable<string> mimeTypes)
        {
            var response = new ListResponseModel();

            // Get all files and directories
            var items = AzureBlobStorageApi.ListFilesAndDirectoriesAsync(path.Directory.FullName);
            var itemList = items.ToList();

            var mimeTypesList = mimeTypes.ToList();

            // Add visible files
            foreach (var file in itemList.Where(i => !i.Name.EndsWith("/")))
            {
                var f = new AzureBlobFile(file);

                if (f.Attributes.HasFlag(FileAttributes.Hidden)) continue;

                if (mimeTypesList.Any() && !mimeTypesList.Contains(f.MimeType) && !mimeTypesList.Contains(f.MimeType.Type))
                    continue;

                response.List.Add(f.Name);
            }

            // Add visible directories
            foreach (var file in itemList.Where(i => i.Name.EndsWith("/")))
            {
                var d = new AzureBlobFile(file);

                if (!d.Attributes.HasFlag(FileAttributes.Hidden)) response.List.Add(d.Name);
            }

            var intersectList = intersect.ToList();
            if (intersectList.Any()) response.List.RemoveAll(l => !intersectList.Contains(l));

            return await Json(response);
        }


        public async Task<JsonResult> MakeDirAsync(FullPath path, string name, IEnumerable<string> dirs)
        {
            var response = new AddResponseModel();

            if (!string.IsNullOrEmpty(name))
            {
                // Create directory
                var newDir = new AzureBlobDirectory(AzureBlobStorageApi.PathCombine(path.Directory.FullName, name));
                await newDir.CreateAsync();
                response.Added.Add(await BaseModel.CreateAsync(newDir, path.RootVolume));
            }

            var enumerable = dirs as string[] ?? dirs.ToArray();

            if (!enumerable.Any()) return await Json(response);

            foreach (var dir in enumerable)
            {
                var dirName = dir.StartsWith("/") ? dir.Substring(1) : dir;
                var newDir = new AzureBlobDirectory(AzureBlobStorageApi.PathCombine(path.Directory.FullName, dirName));
                await newDir.CreateAsync();

                response.Added.Add(await BaseModel.CreateAsync(newDir, path.RootVolume));

                var relativePath = newDir.FullName.Substring(path.RootVolume.RootDirectory.Length);

                // response.Hashes.Add(new KeyValuePair<string, string>($"/{dirName}", path.RootVolume.VolumeId + HttpEncoder.EncodePath(relativePath)));
                response.Hashes.Add($"/{dirName}", path.RootVolume.VolumeId + HttpEncoder.EncodePath(relativePath));
            }

            return await Json(response);
        }


        public async Task<JsonResult> MakeFileAsync(FullPath path, string name)
        {
            var newFile = new AzureBlobFile(AzureBlobStorageApi.PathCombine(path.Directory.FullName, name));
            await newFile.CreateAsync();

            var response = new AddResponseModel();
            response.Added.Add(await CustomBaseModel.CustomCreateAsync(newFile, path.RootVolume));

            return await Json(response);
        }


        public async Task<JsonResult> OpenAsync(FullPath path, bool tree, IEnumerable<string> mimeTypes)
        {
            // todo: BaseModel.CreateAsync internally calls GetDirectoriesAsync() which calls AzureBlobStorageApi.ListFilesAndDirectoriesAsync
            // todo: AzureBlobStorageApi.ListFilesAndDirectoriesAsync is then called again here few lines below;
            // todo: we should be able to reduce it to one single call 
            var response = new OpenResponse(await BaseModel.CreateAsync(path.Directory, path.RootVolume), path);

            // Get all files and directories
            var items = AzureBlobStorageApi.ListFilesAndDirectoriesAsync(path.Directory.FullName);
            var itemList = items.ToList();

            var mimeTypesList = mimeTypes.ToList();

            // Add visible files
            foreach (var file in itemList.Where(i => !i.Name.EndsWith("/")))
            {
                var f = new AzureBlobFile(file);

                if (f.Attributes.HasFlag(FileAttributes.Hidden)) continue;

                if (mimeTypesList.Any() && !mimeTypesList.Contains(f.MimeType) && !mimeTypesList.Contains(f.MimeType.Type))
                    continue;

                response.Files.Add(await CustomBaseModel.CustomCreateAsync(f, path.RootVolume));
            }

            // Add visible directories
            foreach (var dir in itemList.Where(i => i.Name.EndsWith("/")))
            {
                var d = new AzureBlobDirectory(dir);

                if (!d.Attributes.HasFlag(FileAttributes.Hidden)) response.Files.Add(await BaseModel.CreateAsync(d, path.RootVolume));
            }

            // Add parents
            if (!tree) return await Json(response);

            var parent = path.Directory;

            while (parent != null && parent.FullName != path.RootVolume.RootDirectory)
            {
                // Update parent
                parent = parent.Parent;

                // Ensure it's a child of the root
                if (parent != null && path.RootVolume.RootDirectory.Contains(parent.FullName)) response.Files.Insert(0, await BaseModel.CreateAsync(parent, path.RootVolume));
            }

            return await Json(response);
        }


        public async Task<JsonResult> ParentsAsync(FullPath path)
        {
            var response = new TreeResponseModel();

            if (path.Directory.FullName == path.RootVolume.RootDirectory)
            {
                response.Tree.Add(await BaseModel.CreateAsync(path.Directory, path.RootVolume));
            }
            else
            {
                var parent = path.Directory;

                foreach (var item in await parent.Parent.GetDirectoriesAsync()) response.Tree.Add(await BaseModel.CreateAsync(item, path.RootVolume));

                while (parent.FullName != path.RootVolume.RootDirectory)
                {
                    parent = parent.Parent;
                    response.Tree.Add(await BaseModel.CreateAsync(parent, path.RootVolume));
                }
            }

            return await Json(response);
        }


        public async Task<FullPath> ParsePathAsync(string target)
        {
            if (string.IsNullOrEmpty(target)) return null;

            var split = StringHelpers.Split('_', target);

            var root = Roots.First(r => r.VolumeId == split.Prefix);
            var path = HttpEncoder.DecodePath(split.Content);
            var dirUrl = !root.RootDirectory.EndsWith(path) ? path : string.Empty;
            var dir = new AzureBlobDirectory(root.RootDirectory + dirUrl);

            if (await dir.ExistsAsync) return new FullPath(root, dir, target);

            var file = new AzureBlobFile(root.RootDirectory + dirUrl);

            return new FullPath(root, file, target);
        }


        public async Task<JsonResult> PasteAsync(FullPath dest, IEnumerable<FullPath> paths, bool isCut, IEnumerable<string> renames, string suffix)
        {
            var response = new ReplaceResponseModel();

            var pathList = paths.ToList();

            foreach (var src in pathList)
                if (src.IsDirectory)
                {
                    var newDir = new AzureBlobDirectory(AzureBlobStorageApi.PathCombine(dest.Directory.FullName, src.Directory.Name));

                    // Check if it already exists
                    if (await newDir.ExistsAsync) await newDir.DeleteAsync();

                    if (isCut)
                    {
                        await RemoveThumbsAsync(src);
                        await AzureBlobStorageApi.MoveDirectoryAsync(src.Directory.FullName, newDir.FullName);
                        response.Removed.Add(src.HashedTarget);
                    }
                    else
                    {
                        // Copy directory
                        await AzureBlobStorageApi.CopyDirectoryAsync(src.Directory.FullName, newDir.FullName);
                    }

                    response.Added.Add(await BaseModel.CreateAsync(newDir, dest.RootVolume));
                }
                else
                {
                    var newFilePath = AzureBlobStorageApi.PathCombine(dest.Directory.FullName, src.File.Name);
                    await AzureBlobStorageApi.DeleteFileIfExistsAsync(newFilePath);

                    if (isCut)
                    {
                        await RemoveThumbsAsync(src);

                        // Move file
                        await AzureBlobStorageApi.MoveFileAsync(src.File.FullName, newFilePath);

                        response.Removed.Add(src.HashedTarget);
                    }
                    else
                    {
                        // Copy file
                        await AzureBlobStorageApi.CopyFileAsync(src.File.FullName, newFilePath);
                    }

                    response.Added.Add(await CustomBaseModel.CustomCreateAsync(new AzureBlobFile(newFilePath), dest.RootVolume));
                }

            return await Json(response);
        }


        public async Task<JsonResult> PutAsync(FullPath path, string content)
        {
            var response = new ChangedResponseModel();

            // Write content
            await AzureBlobStorageApi.PutAsync(path.File.FullName, content);

            response.Changed.Add(await BaseModel.CreateAsync(path.File, path.RootVolume));

            return await Json(response);
        }


        public Task<JsonResult> PutAsync(FullPath path, byte[] content)
        {
            return PutAsync(path, Encoding.UTF8.GetString(content, 0, content.Length));
        }


        public async Task<JsonResult> RemoveAsync(IEnumerable<FullPath> paths)
        {
            var response = new RemoveResponseModel();

            foreach (var path in paths)
            {
                await RemoveThumbsAsync(path);

                if (path.IsDirectory && await path.Directory.ExistsAsync)
                    await AzureBlobStorageApi.DeleteDirectoryAsync(path.Directory.FullName);
                else if (await path.File.ExistsAsync) await AzureBlobStorageApi.DeleteFileAsync(path.File.FullName);

                response.Removed.Add(path.HashedTarget);
            }

            return await Json(response);
        }


        public async Task<JsonResult> RenameAsync(FullPath path, string name)
        {
            var response = new ReplaceResponseModel();
            response.Removed.Add(path.HashedTarget);
            await RemoveThumbsAsync(path);

            if (path.IsDirectory)
            {
                // Get new path
                var newPath = AzureBlobStorageApi.PathCombine(path.Directory.Parent.FullName, name);

                // Move file
                await AzureBlobStorageApi.MoveDirectoryAsync(path.Directory.FullName, newPath);

                // Add it to added entries list
                response.Added.Add(await BaseModel.CreateAsync(new AzureBlobDirectory(newPath), path.RootVolume));
            }
            else
            {
                // Get new path
                var newPath = AzureBlobStorageApi.PathCombine(path.File.DirectoryName ?? string.Empty, name);

                // Move file
                await AzureBlobStorageApi.MoveFileAsync(path.File.FullName, newPath);

                // Add it to added entries list
                response.Added.Add(await CustomBaseModel.CustomCreateAsync(new AzureBlobFile(newPath), path.RootVolume));
            }

            return await Json(response);
        }


        public async Task<JsonResult> ResizeAsync(FullPath path, int width, int height)
        {
            await RemoveThumbsAsync(path);

            // Resize Image
            ImageWithMimeType image;

            await using (var stream = await path.File.OpenReadAsync())
            {
                image = path.RootVolume.PictureEditor.Resize(stream, width, height);
            }

            await AzureBlobStorageApi.PutAsync(path.File.FullName, image.ImageStream);

            var output = new ChangedResponseModel();
            output.Changed.Add(await BaseModel.CreateAsync(path.File, path.RootVolume));

            return await Json(output);
        }


        public async Task<JsonResult> RotateAsync(FullPath path, int degree)
        {
            await RemoveThumbsAsync(path);

            // Crop Image
            ImageWithMimeType image;

            await using (var stream = await path.File.OpenReadAsync())
            {
                image = path.RootVolume.PictureEditor.Rotate(stream, degree);
            }

            await AzureBlobStorageApi.PutAsync(path.File.FullName, image.ImageStream);

            var output = new ChangedResponseModel();
            output.Changed.Add(await BaseModel.CreateAsync(path.File, path.RootVolume));

            return await Json(output);
        }

        public Task<JsonResult> SearchAsync(FullPath path, string query, IEnumerable<string> mimeTypes)
        {
            throw new NotImplementedException();
        }


        public async Task<JsonResult> SizeAsync(IEnumerable<FullPath> paths)
        {
            var response = new SizeResponseModel();

            foreach (var path in paths)
                if (path.IsDirectory)
                {
                    response.DirectoryCount++; // API counts the current directory in the total

                    var sizeAndCount = await DirectorySizeAndCount(new AzureBlobDirectory(path.Directory.FullName));

                    response.DirectoryCount += sizeAndCount.DirectoryCount;
                    response.FileCount += sizeAndCount.FileCount;
                    response.Size += sizeAndCount.Size;
                }
                else
                {
                    response.FileCount++;
                    response.Size += await path.File.LengthAsync;
                }

            return await Json(response);
        }


        public async Task<JsonResult> ThumbsAsync(IEnumerable<FullPath> paths)
        {
            var response = new ThumbsResponseModel();

            foreach (var path in paths)
                response.Images.Add(path.HashedTarget, await path.RootVolume.GenerateThumbHashAsync(path.File));

            //response.Images.Add(target, path.Root.GenerateThumbHash(path.File) + path.File.Extension); // 2018.02.23: Fix

            return await Json(response);
        }


        public async Task<JsonResult> TreeAsync(FullPath path)
        {
            var response = new TreeResponseModel();

            var items = AzureBlobStorageApi.ListFilesAndDirectoriesAsync(path.Directory.FullName);

            // Add visible directories
            foreach (var dir in items.Where(i => i.Name.EndsWith("/")))
            {
                var d = new AzureBlobDirectory(dir);

                if (!d.Attributes.HasFlag(FileAttributes.Hidden)) response.Tree.Add(await BaseModel.CreateAsync(d, path.RootVolume));
            }

            return await Json(response);
        }


        public async Task<JsonResult> UploadAsync(FullPath path, IEnumerable<IFormFile> files, bool? overwrite, IEnumerable<FullPath> uploadPaths, IEnumerable<string> renames, string suffix)
        {
            var response = new AddResponseModel();

            var fileList = files.ToList();

            // Check if max upload size is set and that no files exceeds it
            if (path.RootVolume.MaxUploadSize.HasValue && fileList.Any(x => x.Length > path.RootVolume.MaxUploadSize))

                // Max upload size exceeded
                return Error.UploadFileTooLarge();

            foreach (var rename in renames)
            {
                var fileInfo = new FileInfo(Path.Combine(path.Directory.FullName, rename));
                var destination = Path.Combine(path.Directory.FullName, $"{Path.GetFileNameWithoutExtension(rename)}{suffix}{Path.GetExtension(rename)}");
                fileInfo.MoveTo(destination);
                response.Added.Add(await CustomBaseModel.CustomCreateAsync(new AzureBlobFile(destination), path.RootVolume));
            }

            var uploadPathList = uploadPaths.ToList();

            foreach (var uploadPath in uploadPathList)
            {
                var dir = uploadPath.Directory;

                while (dir.FullName != path.RootVolume.RootDirectory)
                {
                    response.Added.Add(await BaseModel.CreateAsync(new AzureBlobDirectory(dir.FullName), path.RootVolume));
                    dir = dir.Parent;
                }
            }

            var i = 0;

            foreach (var file in fileList)
            {
                var destination = uploadPathList.Count() > i ? uploadPathList.ElementAt(i).Directory.FullName : path.Directory.FullName;
                var azureFile = new AzureBlobFile(AzureBlobStorageApi.PathCombine(destination, Path.GetFileName(file.FileName)));

                if (await azureFile.ExistsAsync)
                {
                    if (overwrite ?? path.RootVolume.UploadOverwrite)
                    {
                        await azureFile.DeleteAsync();
                        await AzureBlobStorageApi.UploadAsync(file, azureFile.FullName);
                        response.Added.Add(await CustomBaseModel.CustomCreateAsync(new AzureBlobFile(azureFile.FullName), path.RootVolume));
                    }
                    else
                    {
                        var newName = await CreateNameForCopy(azureFile, suffix);
                        await AzureBlobStorageApi.UploadAsync(file, AzureBlobStorageApi.PathCombine(azureFile.DirectoryName, newName));
                        response.Added.Add(await CustomBaseModel.CustomCreateAsync(new AzureBlobFile(newName), path.RootVolume));
                    }
                }
                else
                {
                    await AzureBlobStorageApi.UploadAsync(file, azureFile.FullName);
                    response.Added.Add(await CustomBaseModel.CustomCreateAsync(new AzureBlobFile(azureFile.FullName), path.RootVolume));
                }

                i++;
            }

            return await Json(response);
        }

        public Task<JsonResult> ZipDownloadAsync(IEnumerable<FullPath> paths)
        {
            throw new NotImplementedException();
        }

        public Task<FileStreamResult> ZipDownloadAsync(FullPath cwdPath, string archivedFileKey, string downloadFileName, string mimeType)
        {
            throw new NotImplementedException();
        }

        #endregion


        #region Private

        private static async Task<string> CreateNameForCopy(IFile file, string suffix)
        {
            var parentPath = file.DirectoryName;
            var name = Path.GetFileNameWithoutExtension(file.Name);
            var extension = file.Extension;

            for (var i = 1; i < 10; i++)
            {
                var newName = $"{parentPath}/{name}{suffix ?? "-"}{i}{extension}";

                if (!await AzureBlobStorageApi.FileExistsAsync(newName)) return newName;
            }

            return $"{parentPath}/{name}{suffix ?? "-"}{Guid.NewGuid()}{extension}";
        }


        private static async Task<SizeResponseModel> DirectorySizeAndCount(IDirectory d)
        {
            var response = new SizeResponseModel();

            // Add file sizes.
            // todo: add mimetypes instead of null as GetFilesAsync parameter
            foreach (var file in await d.GetFilesAsync(null))
            {
                response.FileCount++;
                response.Size += await file.LengthAsync;
            }

            // Add subdirectory sizes.
            foreach (var directory in await d.GetDirectoriesAsync())
            {
                response.DirectoryCount++;

                var subdir = await DirectorySizeAndCount(directory);
                response.DirectoryCount += subdir.DirectoryCount;
                response.FileCount += subdir.FileCount;
                response.Size += subdir.Size;
            }

            return response;
        }


        private static async Task RemoveThumbsAsync(FullPath path)
        {
            if (path.IsDirectory)
            {
                var thumbPath = path.RootVolume.GenerateThumbPath(path.Directory);

                if (thumbPath == null) return;

                await AzureBlobStorageApi.DeleteDirectoryIfExistsAsync(thumbPath);
            }
            else
            {
                var thumbPath = await path.RootVolume.GenerateThumbPathAsync(path.File);

                if (thumbPath == null) return;

                await AzureBlobStorageApi.DeleteFileIfExistsAsync(thumbPath);
            }
        }

        #endregion


    }

}