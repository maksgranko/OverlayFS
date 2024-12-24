using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using DokanNet;
using DokanNet.Logging;
using static DokanNet.FormatProviders;
using FileAccess = DokanNet.FileAccess;

namespace OverlayFileSystem
{
    internal class OverlayFileSystem : IDokanOperations
    {
        private readonly string smbRoot;
        private readonly string overlayRoot;
        private const FileAccess DataAccess = FileAccess.ReadData | FileAccess.WriteData | FileAccess.AppendData |
                                              FileAccess.Execute |
                                              FileAccess.GenericExecute | FileAccess.GenericWrite |
                                              FileAccess.GenericRead;

        private const FileAccess DataWriteAccess = FileAccess.WriteData | FileAccess.AppendData |
                                                   FileAccess.Delete |
                                                   FileAccess.GenericWrite;

        private readonly ILogger _logger;

        public OverlayFileSystem(string smbPath, string overlayPath, ILogger logger)
        {
            smbRoot = smbPath;
            overlayRoot = overlayPath;
            this._logger = logger;

            if (!Directory.Exists(overlayRoot))
                Directory.CreateDirectory(overlayRoot);
        }
        private string GetOverlayPath(string fileName)
        {
            return Path.Combine(overlayRoot, fileName.TrimStart('\\'));
        }

        private string GetSmbPath(string fileName)
        {
            return Path.Combine(smbRoot, fileName.TrimStart('\\'));
        }

        protected string GetPath(string fileName)
        {
            return GetSmbPath(fileName);
        }

        protected NtStatus Trace(string method, string fileName, IDokanFileInfo info, NtStatus result,
            params object[] parameters)
        {
#if TRACE
            var extraParameters = parameters != null && parameters.Length > 0
                ? ", " + string.Join(", ", parameters.Select(x => string.Format(DefaultFormatProvider, "{0}", x)))
                : string.Empty;

            _logger.Debug(DokanFormat($"{method}('{fileName}', {info}{extraParameters}) -> {result}"));
#endif

            return result;
        }

        private NtStatus Trace(string method, string fileName, IDokanFileInfo info,
            FileAccess access, FileShare share, FileMode mode, FileOptions options, FileAttributes attributes,
            NtStatus result)
        {
#if TRACE
            _logger.Debug(
                DokanFormat(
                    $"{method}('{fileName}', {info}, [{access}], [{share}], [{mode}], [{options}], [{attributes}]) -> {result}"));
#endif

            return result;
        }

        protected static Int32 GetNumOfBytesToCopy(Int32 bufferLength, long offset, IDokanFileInfo info, FileStream stream)
        {
            if (info.PagingIo)
            {
                var longDistanceToEnd = stream.Length - offset;
                var isDistanceToEndMoreThanInt = longDistanceToEnd > Int32.MaxValue;
                if (isDistanceToEndMoreThanInt) return bufferLength;
                var distanceToEnd = (Int32)longDistanceToEnd;
                if (distanceToEnd < bufferLength) return distanceToEnd;
                return bufferLength;
            }
            return bufferLength;
        }
        #region --- NEW IMPLEMENTATION OF IDOKAN ---

        public NtStatus CreateFile(string fileName, FileAccess access, FileShare share, FileMode mode, FileOptions options, FileAttributes attributes, IDokanFileInfo info)
        {
            _logger.Debug($"CreateFile('{fileName}') called.");

            if (string.IsNullOrWhiteSpace(fileName) || fileName.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                return DokanResult.InvalidName;
            }

            string overlayPath = GetOverlayPath(fileName);
            string smbPath = GetSmbPath(fileName);
            //string b = File.ReadAllText(fileName);

            if (mode == FileMode.Create || mode == FileMode.OpenOrCreate || mode == FileMode.CreateNew)
            {
                if (File.Exists(overlayPath) || File.Exists(smbPath))
                {
                    info.Context = new FileStream(
                        File.Exists(overlayPath) ? overlayPath : smbPath,
                        mode,
                        System.IO.FileAccess.ReadWrite,
                        share,
                        4096,
                        options);
                    return DokanResult.Success;
                }
                var newFile = File.Create(overlayPath);
                info.Context = newFile;
                return DokanResult.Success;
            }
            if ((access & FileAccess.WriteData) != 0)
            {
                info.Context = new FileStream(overlayPath, mode, System.IO.FileAccess.Write, share, 4096, options);
                return DokanResult.Success;
            }
            if ((access & FileAccess.Synchronize) != 0)
            {
                return DokanResult.Success;
            }

            if (Directory.Exists(overlayPath))
            {
                info.IsDirectory = true;
                return DokanResult.Success;
            }
            else if (Directory.Exists(smbPath))
            {
                info.IsDirectory = true;
                return DokanResult.Success;
            }
            if (File.Exists(overlayPath))
            {
                info.Context = new FileStream(overlayPath, mode, System.IO.FileAccess.Read, share, 4096, options);
                return DokanResult.Success;
            }
            else if (File.Exists(smbPath))
            {
                info.Context = new FileStream(smbPath, mode, System.IO.FileAccess.Read, share, 4096, options);
                return DokanResult.Success;
            }

            return DokanResult.FileNotFound;
        }

        public NtStatus ReadFile(string fileName, byte[] buffer, out int bytesRead, long offset, IDokanFileInfo info)
        {
            bytesRead = 0;

            if (info.Context is FileStream stream)
            {
                stream.Position = offset;
                bytesRead = stream.Read(buffer, 0, buffer.Length);
                return DokanResult.Success;
            }

            return DokanResult.Error;
        }

        public NtStatus WriteFile(string fileName, byte[] buffer, out int bytesWritten, long offset, IDokanFileInfo info)
        {
            bytesWritten = 0;

            if (info.Context is FileStream stream)
            {
                    stream.Position = offset;
                    stream.Write(buffer, 0, buffer.Length);
                    bytesWritten = buffer.Length;
                return DokanResult.Success;
            }

            return DokanResult.Error;
        }

        public NtStatus GetFileInformation(string fileName, out FileInformation fileInfo, IDokanFileInfo info)
        {
            _logger.Debug($"GetFileInformation('{fileName}') called.");

            // Проверка корня
            if (string.IsNullOrWhiteSpace(fileName) || fileName == @"\")
            {
                fileInfo = new FileInformation
                {
                    FileName = @"\",
                    Attributes = FileAttributes.Directory, // Указываем, что это папка
                    CreationTime = DateTime.Now,
                    LastAccessTime = DateTime.Now,
                    LastWriteTime = DateTime.Now,
                    Length = 0 // Корень не имеет размера
                };
                return DokanResult.Success;
            }

            // Если не корень, ищем реальный файл
            var overlayPath = GetOverlayPath(fileName);
            if (File.Exists(overlayPath))
            {
                var file = new FileInfo(overlayPath);
                fileInfo = new FileInformation
                {
                    FileName = fileName,
                    Attributes = file.Attributes,
                    CreationTime = file.CreationTime,
                    LastAccessTime = file.LastAccessTime,
                    LastWriteTime = file.LastWriteTime,
                    Length = file.Length
                };
                return DokanResult.Success;
            }

            fileInfo = default;
            return DokanResult.FileNotFound;
        }


        public NtStatus DeleteFile(string fileName, IDokanFileInfo info)
        {
            string overlayPath = GetOverlayPath(fileName);

            if (File.Exists(overlayPath))
            {
                File.Delete(overlayPath);
                return DokanResult.Success;
            }

            return DokanResult.AccessDenied;
        }

        public NtStatus DeleteDirectory(string fileName, IDokanFileInfo info)
        {
            string overlayPath = GetOverlayPath(fileName);

            if (Directory.Exists(overlayPath))
            {
                Directory.Delete(overlayPath);
                return DokanResult.Success;
            }

            return DokanResult.AccessDenied;
        }

        public NtStatus FindFiles(string fileName, out IList<FileInformation> files, IDokanFileInfo info)
        {
            var overlayPath = GetOverlayPath(fileName);
            var smbPath = GetSmbPath(fileName);

            var fileMap = new Dictionary<string, FileInformation>(StringComparer.OrdinalIgnoreCase);

            if (Directory.Exists(overlayPath))
            {
                foreach (var f in Directory.EnumerateFileSystemEntries(overlayPath))
                {
                    var fileInfo = new FileInfo(f);
                    fileMap[fileInfo.Name] = new FileInformation
                    {
                        FileName = fileInfo.Name,
                        Attributes = fileInfo.Attributes,
                        CreationTime = fileInfo.CreationTime,
                        LastAccessTime = fileInfo.LastAccessTime,
                        LastWriteTime = fileInfo.LastWriteTime,
                        Length = fileInfo.Length
                    };
                }
            }

            if (Directory.Exists(smbPath))
            {
                foreach (var f in Directory.EnumerateFileSystemEntries(smbPath))
                {
                    var fileInfo = new FileInfo(f);
                    if (!fileMap.ContainsKey(fileInfo.Name))
                    {
                        fileMap[fileInfo.Name] = new FileInformation
                        {
                            FileName = fileInfo.Name,
                            Attributes = fileInfo.Attributes,
                            CreationTime = fileInfo.CreationTime,
                            LastAccessTime = fileInfo.LastAccessTime,
                            LastWriteTime = fileInfo.LastWriteTime,
                            Length = fileInfo.Length
                        };
                    }
                }
            }

            files = fileMap.Values.ToList();

            return DokanResult.Success;
        }


        public void Cleanup(string fileName, IDokanFileInfo info)
        {
            if (info.Context is FileStream stream)
            {
                stream.Dispose();
                info.Context = null;
            }
        }

        public void CloseFile(string fileName, IDokanFileInfo info)
        {
            Cleanup(fileName, info);
        }

        #endregion --- NEW IMPLEMENTATION OF IDOKAN ---
        #region Implementation of IDokanOperations

        public NtStatus FlushFileBuffers(string fileName, IDokanFileInfo info)
        {
            try
            {
                ((FileStream)(info.Context)).Flush();
                return Trace(nameof(FlushFileBuffers), fileName, info, DokanResult.Success);
            }
            catch (IOException)
            {
                return Trace(nameof(FlushFileBuffers), fileName, info, DokanResult.DiskFull);
            }
        }

        public NtStatus SetFileAttributes(string fileName, FileAttributes attributes, IDokanFileInfo info)
        {
            try
            {
                if (attributes != 0)
                    File.SetAttributes(GetPath(fileName), attributes);
                return Trace(nameof(SetFileAttributes), fileName, info, DokanResult.Success, attributes.ToString());
            }
            catch (UnauthorizedAccessException)
            {
                return Trace(nameof(SetFileAttributes), fileName, info, DokanResult.AccessDenied, attributes.ToString());
            }
            catch (FileNotFoundException)
            {
                return Trace(nameof(SetFileAttributes), fileName, info, DokanResult.FileNotFound, attributes.ToString());
            }
            catch (DirectoryNotFoundException)
            {
                return Trace(nameof(SetFileAttributes), fileName, info, DokanResult.PathNotFound, attributes.ToString());
            }
        }

        public NtStatus SetFileTime(string fileName, DateTime? creationTime, DateTime? lastAccessTime,
            DateTime? lastWriteTime, IDokanFileInfo info)
        {
            try
            {
                if (info.Context is FileStream stream)
                {
                    var ct = creationTime?.ToFileTime() ?? 0;
                    var lat = lastAccessTime?.ToFileTime() ?? 0;
                    var lwt = lastWriteTime?.ToFileTime() ?? 0;
                    if (NativeMethods.SetFileTime(stream.SafeFileHandle, ref ct, ref lat, ref lwt))
                        return DokanResult.Success;
                    throw Marshal.GetExceptionForHR(Marshal.GetLastWin32Error());
                }

                var filePath = GetPath(fileName);

                if (creationTime.HasValue)
                    File.SetCreationTime(filePath, creationTime.Value);

                if (lastAccessTime.HasValue)
                    File.SetLastAccessTime(filePath, lastAccessTime.Value);

                if (lastWriteTime.HasValue)
                    File.SetLastWriteTime(filePath, lastWriteTime.Value);

                return Trace(nameof(SetFileTime), fileName, info, DokanResult.Success, creationTime, lastAccessTime,
                    lastWriteTime);
            }
            catch (UnauthorizedAccessException)
            {
                return Trace(nameof(SetFileTime), fileName, info, DokanResult.AccessDenied, creationTime, lastAccessTime,
                    lastWriteTime);
            }
            catch (FileNotFoundException)
            {
                return Trace(nameof(SetFileTime), fileName, info, DokanResult.FileNotFound, creationTime, lastAccessTime,
                    lastWriteTime);
            }
        }

        public NtStatus MoveFile(string oldName, string newName, bool replace, IDokanFileInfo info)
        {
            var oldpath = GetPath(oldName);
            var newpath = GetPath(newName);

            (info.Context as FileStream)?.Dispose();
            info.Context = null;

            var exist = info.IsDirectory ? Directory.Exists(newpath) : File.Exists(newpath);

            try
            {

                if (!exist)
                {
                    info.Context = null;
                    if (info.IsDirectory)
                        Directory.Move(oldpath, newpath);
                    else
                        File.Move(oldpath, newpath);
                    return Trace(nameof(MoveFile), oldName, info, DokanResult.Success, newName,
                        replace.ToString(CultureInfo.InvariantCulture));
                }
                else if (replace)
                {
                    info.Context = null;

                    if (info.IsDirectory) //Cannot replace directory destination - See MOVEFILE_REPLACE_EXISTING
                        return Trace(nameof(MoveFile), oldName, info, DokanResult.AccessDenied, newName,
                            replace.ToString(CultureInfo.InvariantCulture));

                    File.Delete(newpath);
                    File.Move(oldpath, newpath);
                    return Trace(nameof(MoveFile), oldName, info, DokanResult.Success, newName,
                        replace.ToString(CultureInfo.InvariantCulture));
                }
            }
            catch (UnauthorizedAccessException)
            {
                return Trace(nameof(MoveFile), oldName, info, DokanResult.AccessDenied, newName,
                    replace.ToString(CultureInfo.InvariantCulture));
            }
            return Trace(nameof(MoveFile), oldName, info, DokanResult.FileExists, newName,
                replace.ToString(CultureInfo.InvariantCulture));
        }

        public NtStatus SetEndOfFile(string fileName, long length, IDokanFileInfo info)
        {
            try
            {
                ((FileStream)(info.Context)).SetLength(length);
                return Trace(nameof(SetEndOfFile), fileName, info, DokanResult.Success,
                    length.ToString(CultureInfo.InvariantCulture));
            }
            catch (IOException)
            {
                return Trace(nameof(SetEndOfFile), fileName, info, DokanResult.DiskFull,
                    length.ToString(CultureInfo.InvariantCulture));
            }
        }

        public NtStatus SetAllocationSize(string fileName, long length, IDokanFileInfo info)
        {
            try
            {
                ((FileStream)(info.Context)).SetLength(length);
                return Trace(nameof(SetAllocationSize), fileName, info, DokanResult.Success,
                    length.ToString(CultureInfo.InvariantCulture));
            }
            catch (IOException)
            {
                return Trace(nameof(SetAllocationSize), fileName, info, DokanResult.DiskFull,
                    length.ToString(CultureInfo.InvariantCulture));
            }
        }

        public NtStatus LockFile(string fileName, long offset, long length, IDokanFileInfo info)
        {
#if !NETCOREAPP1_0
            try
            {
                ((FileStream)(info.Context)).Lock(offset, length);
                return Trace(nameof(LockFile), fileName, info, DokanResult.Success,
                    offset.ToString(CultureInfo.InvariantCulture), length.ToString(CultureInfo.InvariantCulture));
            }
            catch (IOException)
            {
                return Trace(nameof(LockFile), fileName, info, DokanResult.AccessDenied,
                    offset.ToString(CultureInfo.InvariantCulture), length.ToString(CultureInfo.InvariantCulture));
            }
#else
// .NET Core 1.0 do not have support for FileStream.Lock
            return DokanResult.NotImplemented;
#endif
        }

        public NtStatus UnlockFile(string fileName, long offset, long length, IDokanFileInfo info)
        {
#if !NETCOREAPP1_0
            try
            {
                ((FileStream)(info.Context)).Unlock(offset, length);
                return Trace(nameof(UnlockFile), fileName, info, DokanResult.Success,
                    offset.ToString(CultureInfo.InvariantCulture), length.ToString(CultureInfo.InvariantCulture));
            }
            catch (IOException)
            {
                return Trace(nameof(UnlockFile), fileName, info, DokanResult.AccessDenied,
                    offset.ToString(CultureInfo.InvariantCulture), length.ToString(CultureInfo.InvariantCulture));
            }
#else
// .NET Core 1.0 do not have support for FileStream.Unlock
            return DokanResult.NotImplemented;
#endif
        }

        public NtStatus GetDiskFreeSpace(out long freeBytesAvailable, out long totalBytes, out long totalFreeBytes, IDokanFileInfo info)
        {
            _logger.Debug("GetDiskFreeSpace called.");

            try
            {
                // Получаем информацию о диске для папки Overlay
                var driveInfo = new DriveInfo(Path.GetPathRoot(overlayRoot)); // overlayRoot - путь к Overlay

                // Считываем значения дискового пространства
                totalBytes = driveInfo.TotalSize;                      // Общий объём диска
                totalFreeBytes = driveInfo.TotalFreeSpace;             // Свободное место на диске
                freeBytesAvailable = driveInfo.AvailableFreeSpace;     // Доступное пользователю место

                _logger.Debug($"Disk space: Total={totalBytes}, Free={totalFreeBytes}, Available={freeBytesAvailable}");

                return DokanResult.Success;
            }
            catch (Exception ex)
            {
                _logger.Error($"GetDiskFreeSpace error: {ex.Message}");
                freeBytesAvailable = 0;
                totalBytes = 0;
                totalFreeBytes = 0;
                return DokanResult.Error;
            }
        }


        public NtStatus GetVolumeInformation(out string volumeLabel, out FileSystemFeatures features,
                                     out string fileSystemName, out uint maximumComponentLength, IDokanFileInfo info)
        {
            // Указываем параметры тома
            volumeLabel = "OverlayFS";
            fileSystemName = "NTFS";
            maximumComponentLength = 255;
            features = FileSystemFeatures.CasePreservedNames |
                       FileSystemFeatures.CaseSensitiveSearch |
                       FileSystemFeatures.PersistentAcls |
                       FileSystemFeatures.SupportsRemoteStorage |
                       FileSystemFeatures.UnicodeOnDisk;

            return DokanResult.Success;
        }


        public NtStatus GetFileSecurity(string fileName, out FileSystemSecurity security, AccessControlSections sections,
    IDokanFileInfo info)
        {
            try
            {
                string overlayPath = GetOverlayPath(fileName);
                string smbPath = GetSmbPath(fileName);
                string targetPath = null;

                // Определяем, какой слой используется (приоритет Overlay Write)
                if (File.Exists(overlayPath) || Directory.Exists(overlayPath))
                {
                    targetPath = overlayPath; // Overlay Write имеет приоритет
                }
                else if (File.Exists(smbPath) || Directory.Exists(smbPath))
                {
                    targetPath = smbPath; // Если файла нет в overlay, проверяем SMB
                }

                if (targetPath == null)
                {
                    security = null;
                    return Trace(nameof(GetFileSecurity), fileName, info, DokanResult.FileNotFound, sections.ToString());
                }

                // Получаем права доступа из правильного слоя
                security = info.IsDirectory
                    ? (FileSystemSecurity)new DirectoryInfo(targetPath).GetAccessControl(sections)
                    : new FileInfo(targetPath).GetAccessControl(sections);

                return Trace(nameof(GetFileSecurity), fileName, info, DokanResult.Success, sections.ToString());
            }
            catch (UnauthorizedAccessException ex)
            {
                security = null;
                _logger.Error($"Ошибка доступа для {fileName}: {ex.Message}");
                return Trace(nameof(GetFileSecurity), fileName, info, DokanResult.AccessDenied, sections.ToString());
            }
            catch (Exception ex)
            {
                security = null;
                _logger.Error($"Ошибка безопасности для {fileName}: {ex.Message}");
                return Trace(nameof(GetFileSecurity), fileName, info, DokanResult.Error, sections.ToString());
            }
        }

        public NtStatus SetFileSecurity(string fileName, FileSystemSecurity security, AccessControlSections sections,
    IDokanFileInfo info)
        {
            try
            {
                string overlayPath = GetOverlayPath(fileName);
                string smbPath = GetSmbPath(fileName);
                bool applied = false;

                // Применяем права для файла в Overlay Write (если он есть)
                if ((info.IsDirectory && Directory.Exists(overlayPath)) || (!info.IsDirectory && File.Exists(overlayPath)))
                {
                    if (info.IsDirectory && security is DirectorySecurity dirSecurity)
                    {
                        new DirectoryInfo(overlayPath).SetAccessControl(dirSecurity);
                    }
                    else if (!info.IsDirectory && security is FileSecurity fileSecurity)
                    {
                        new FileInfo(overlayPath).SetAccessControl(fileSecurity);
                    }
                    applied = true; // Помечаем, что применили права
                }

                // Применяем права для файла в SMB (если он есть)
                if ((info.IsDirectory && Directory.Exists(smbPath)) || (!info.IsDirectory && File.Exists(smbPath)))
                {
                    if (info.IsDirectory && security is DirectorySecurity dirSecurity)
                    {
                        new DirectoryInfo(smbPath).SetAccessControl(dirSecurity);
                    }
                    else if (!info.IsDirectory && security is FileSecurity fileSecurity)
                    {
                        new FileInfo(smbPath).SetAccessControl(fileSecurity);
                    }
                    applied = true; // Помечаем, что применили права
                }

                if (applied)
                {
                    return Trace(nameof(SetFileSecurity), fileName, info, DokanResult.Success, sections.ToString());
                }
                return Trace(nameof(SetFileSecurity), fileName, info, DokanResult.FileNotFound, sections.ToString());
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.Error($"Ошибка доступа при установке прав для {fileName}: {ex.Message}");
                return Trace(nameof(SetFileSecurity), fileName, info, DokanResult.AccessDenied, sections.ToString());
            }
            catch (Exception ex)
            {
                _logger.Error($"Ошибка при установке прав для {fileName}: {ex.Message}");
                return Trace(nameof(SetFileSecurity), fileName, info, DokanResult.Error, sections.ToString());
            }
        }



        public NtStatus Mounted(string mountPoint, IDokanFileInfo info)
        {
            return Trace(nameof(Mounted), null, info, DokanResult.Success);
        }

        public NtStatus Unmounted(IDokanFileInfo info)
        {
            return Trace(nameof(Unmounted), null, info, DokanResult.Success);
        }

        public NtStatus FindStreams(string fileName, IntPtr enumContext, out string streamName, out long streamSize,
            IDokanFileInfo info)
        {
            streamName = string.Empty;
            streamSize = 0;
            return Trace(nameof(FindStreams), fileName, info, DokanResult.NotImplemented, enumContext.ToString(),
                "out " + streamName, "out " + streamSize.ToString());
        }

        public NtStatus FindStreams(string fileName, out IList<FileInformation> streams, IDokanFileInfo info)
        {
            streams = new FileInformation[0];
            return Trace(nameof(FindStreams), fileName, info, DokanResult.NotImplemented);
        }

        public IList<FileInformation> FindFilesHelper(string fileName, string searchPattern)
        {
            IList<FileInformation> files = new DirectoryInfo(GetPath(fileName))
                .EnumerateFileSystemInfos()
                .Where(finfo => DokanHelper.DokanIsNameInExpression(searchPattern, finfo.Name, true))
                .Select(finfo => new FileInformation
                {
                    Attributes = finfo.Attributes,
                    CreationTime = finfo.CreationTime,
                    LastAccessTime = finfo.LastAccessTime,
                    LastWriteTime = finfo.LastWriteTime,
                    Length = (finfo as FileInfo)?.Length ?? 0,
                    FileName = finfo.Name
                }).ToArray();

            return files;
        }

        public NtStatus FindFilesWithPattern(string fileName, string searchPattern, out IList<FileInformation> files,
            IDokanFileInfo info)
        {
            files = FindFilesHelper(fileName, searchPattern);

            return Trace(nameof(FindFilesWithPattern), fileName, info, DokanResult.Success);
        }

        #endregion Implementation of IDokanOperations
    }
}
