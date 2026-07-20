using ADB_Explorer.Converters;
using ADB_Explorer.Helpers;
using ADB_Explorer.Models;
using ADB_Explorer.ViewModels;
using System.Security.Cryptography.X509Certificates;

namespace ADB_Explorer.Services;

public static class Security
{
    private const int MAX_HASH_PARALLELISM = 2;
    private static readonly SemaphoreSlim ValidationGate = new(1, 1);

    /// <summary>
    /// Verifies that the specified file has a valid Authenticode signature issued to Google LLC.
    /// </summary>
    /// <remarks>
    /// Uses WinVerifyTrust to check signature integrity, certificate chain trust (offline, no revocation check),
    /// and the certificate's owner.
    /// </remarks>
    public static bool VerifyAuthenticode(string filePath, string owner)
    {
        try
        {
            if (!NativeMethods.WinTrust.VerifyEmbeddedSignature(filePath))
                return false;

#pragma warning disable SYSLIB0057 // No X509CertificateLoader API extracts the signer cert from an Authenticode-signed PE file.
            using var cert = X509Certificate2.CreateFromSignedFile(filePath);
#pragma warning restore SYSLIB0057

            return cert.Subject.Contains($"O={owner}", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static string CalculateWindowsFileHash(string path, bool useSHA = false)
    {
        try
        {
            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.SequentialScan);
            return CalculateWindowsFileHash(stream, useSHA);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Calculates the cryptographic hash of the specified file stream using either the MD5 or SHA256 algorithm.
    /// </summary>
    /// <remarks>SHA256 provides a stronger hash than MD5 and is recommended for security-sensitive scenarios.
    /// The stream must not be modified during the hashing process.</remarks>
    /// <param name="file">A readable stream positioned at the beginning of the file to compute the hash for.</param>
    /// <param name="useSHA">Specifies whether to use the SHA256 algorithm. If <see langword="true"/>, SHA256 is used; otherwise, MD5 is
    /// used.</param>
    /// <returns>A hexadecimal *UPPERCASE* string representation of the computed hash value.</returns>
    public static string CalculateWindowsFileHash(Stream file, bool useSHA = false)
    {
        var hash = useSHA
            ? SHA256.HashData(file)
            : MD5.HashData(file);
        
        return Convert.ToHexString(hash);
    }

    public static string CalculateHexStringHash(string str)
    {
        var hash = MD5.HashData(Convert.FromHexString(str));
        return Convert.ToHexString(hash);
    }

    public static Dictionary<string, string> CalculateWindowsFolderHash(string path, string parent = "")
    {
        if (!Path.Exists(path))
            return [];

        if (!File.GetAttributes(path).HasFlag(FileAttributes.Directory))
        {
            return new() { { Path.GetFileName(path), CalculateWindowsFileHash(path) } };
        }

        var root = string.IsNullOrEmpty(parent) ? path : parent;
        Dictionary<string, string> hashes = new(StringComparer.Ordinal);
        object hashesLock = new();
        System.IO.EnumerationOptions enumerationOptions = new()
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        Parallel.ForEach(
            Directory.EnumerateFiles(path, "*", enumerationOptions),
            new ParallelOptions { MaxDegreeOfParallelism = MAX_HASH_PARALLELISM },
            file =>
            {
                string key = FileHelper.ExtractRelativePath(file, root).Replace('\\', '/');
                string hash = CalculateWindowsFileHash(file);
                lock (hashesLock)
                    hashes.TryAdd(key, hash);
            });

        return new(hashes);
    }

    public static Dictionary<string, string> CalculateAndroidFolderHash(FilePath path, Device device)
    {
        // find ./ -mindepth 1 -type f -exec md5sum {} \;
        string[] args = [ ADBService.EscapeAdbShellString(path.FullPath), "-type", "f", "-exec", "md5sum", "{}", @"\;" ];
        ADBService.ExecuteDeviceAdbShellCommand(device.ID, "find", out string stdout, out string stderr, new(), args);

        var list = AdbRegEx.RE_ANDROID_FIND_HASH().Matches(stdout);
        return list.Where(m => m.Success).ToDictionary(
            m => FileHelper.ExtractRelativePath(m.Groups["Path"].Value.TrimEnd('\r', '\n'), path.FullPath), 
            m => m.Groups["Hash"].Value.ToUpper());
    }

    public static Dictionary<string, string> CalculateAndroidArchiveHash(FilePath path, Device device)
    {
        // tar -xf *.tar.gz --to-command='echo $(md5sum) $TAR_FILENAME'
        string[] args = [ "xf", ADBService.EscapeAdbShellString(path.FullPath), "--to-command='echo $(md5sum) $TAR_FILENAME'" ];
        ADBService.ExecuteDeviceAdbShellCommand(device.ID, "tar", out string stdout, out string stderr, new(), args);

        var list = AdbRegEx.RE_ANDROID_FIND_HASH().Matches(stdout);
        return list.Where(m => m.Success).ToDictionary(
            m => m.Groups["Path"].Value.TrimEnd('\r', '\n'),
            m => m.Groups["Hash"].Value.ToUpper());
    }

    public static void ValidateOps()
    {
        foreach (var item in App.FileActions.SelectedFileOps.Value)
        {
            ValidateOperation(item);
        }
    }

    public static async void ValidateOperation(FileOperation op)
    {
        op.SetValidation(true);
        bool gateAcquired = false;

        try
        {
            await ValidationGate.WaitAsync();
            gateAcquired = true;

            var result = await Task.Run(() =>
            {
                Dictionary<string, string> source = null;
                Dictionary<string, string> target = null;
                Parallel.Invoke(
                    new ParallelOptions { MaxDegreeOfParallelism = 2 },
                    () => source = op.FilePath.PathType is AbstractFile.FilePathType.Android
                        ? CalculateAndroidFolderHash(op.FilePath, op.Device)
                        : CalculateWindowsFolderHash(op.FilePath.FullPath),
                    () => target = op.TargetPath.PathType is AbstractFile.FilePathType.Android
                        ? CalculateAndroidFolderHash(op.TargetPath, op.Device)
                        : CalculateWindowsFolderHash(op.TargetPath.FullPath));

                List<FileOpProgressInfo> updates = new(source.Count);
                int fails = 0;
                string singleTargetHash = op.OperationName is FileOperation.OperationType.Copy && target.Count == 1
                    ? target.Values.First()
                    : null;

                foreach (var item in source.OrderBy(item => item.Key))
                {
                    string key = op.AndroidPath.IsDirectory
                        ? FileHelper.ConcatPaths(op.AndroidPath, item.Key)
                        : op.AndroidPath.FullPath;
                    string targetHash = singleTargetHash;
                    bool targetExists = targetHash is not null || target.TryGetValue(item.Key, out targetHash);

                    FileOpProgressInfo update = !targetExists
                        ? new HashFailInfo(key, false)
                        : string.Equals(item.Value, targetHash, StringComparison.Ordinal)
                            ? new HashSuccessInfo(key)
                            : new HashFailInfo(key);
                    updates.Add(update);
                    if (update is HashFailInfo)
                        fails++;
                }

                return (Updates: updates, Fails: fails, SourceCount: source.Count);
            });

            op.ClearChildren();
            op.AddUpdates(result.Updates);

            FileOpProgressInfo lastUpdate = result.Updates.LastOrDefault();
            string message = result.SourceCount == 1
                ? lastUpdate is HashFailInfo fail
                    ? string.Format(Strings.Resources.S_VALIDATION_ERROR, fail.Message)
                    : Strings.Resources.S_FILEOP_VALIDATED
                : FileOpStatusConverter.StatusString(
                    typeof(HashFailInfo),
                    result.SourceCount - result.Fails,
                    result.Fails,
                    total: true);

            op.StatusInfo = result.Fails > 0
                ? new FailedOpProgressViewModel(message)
                : new CompletedShellProgressViewModel(message);
            op.IsValidated = result.Fails < 1;
        }
        catch (Exception e)
        {
            op.StatusInfo = new FailedOpProgressViewModel(e.Message);
            op.IsValidated = false;
        }
        finally
        {
            if (gateAcquired)
                ValidationGate.Release();
            op.SetValidation(false);
        }
    }
}
