using ADB_Explorer.Helpers;
using ADB_Explorer.Models;
using ADB_Explorer.ViewModels;
using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.DeviceCommands;
using System.Threading.Channels;
using static ADB_Explorer.Models.AbstractFile;

namespace ADB_Explorer.Services;

public partial class ADBService
{
    public static readonly char[] LINE_SEPARATORS = ['\n', '\r'];

    private const string GET_PROP = "getprop";
    private const string ANDROID_VERSION = "ro.build.version.release";
    private const string MMC_PROP = "vold.microsd.uuid";
    private const string OTG_PROP = "vold.otgstorage.uuid";

    // First partition of MMC block device 0 / 1
    private static readonly string[] MMC_BLOCK_DEVICES = ["/dev/block/mmcblk0p1", "/dev/block/mmcblk1p1"];
    private static readonly string[] EMULATED_DRIVES_GREP = ["|", "grep", "-E", "'/mnt/media_rw/|/storage/'"];

    private static readonly string[] READLINK_ARGS1 = ["link", "in"]; // Preceded by 'for'
    private static readonly string[] READLINK_ARGS2 =
    [
        ";", "do",
        "if [ -z \"$readlink_cmd\" ]; then echo \"/// $link ///  /// u ///\"; continue; fi;",
        "target=$($readlink_cmd -f \"$link\" 2>/dev/null);",
        "if [ -z \"$target\" ]; then kind=x;",
        "elif [ -d \"$target\" ]; then kind=d;",
        "elif [ -f \"$target\" ]; then kind=f;",
        "elif [ -b \"$target\" ]; then kind=b;",
        "elif [ -c \"$target\" ]; then kind=c;",
        "elif [ -p \"$target\" ]; then kind=p;",
        "elif [ -S \"$target\" ]; then kind=s;",
        "else kind=u; fi;",
        "echo \"/// $link /// $target /// $kind ///\";",
        "done"
    ];


    public class AdbDevice(LogicalDeviceViewModel other) : Device
    {
        private readonly AdbCommandClient commandClient = new();

        public LogicalDeviceViewModel Device { get; } = other;

        public override string ID => Device.ID;

        public override DeviceType Type => Device.Type;

        public override DeviceStatus Status => Device.Status;
        
        public override string IpAddress => Device.IpAddress;

        private const string CURRENT_DIR = ".";
        private const string PARENT_DIR = "..";
        private static readonly string[] SPECIAL_DIRS = [CURRENT_DIR, PARENT_DIR];

        /// <summary>Represents the Unix filesystem permissions and file type.<br />
        /// Since the file type flags overlap, they CANNOT be used as flags.</summary>
        public enum UnixFileMode
        {
            /// <summary>No permissions.</summary>
            None = 0x0,
            /// <summary>Execute permission for others.</summary>
            OtherExecute = 0x1,
            /// <summary>Write permission for others.</summary>
            OtherWrite = 0x2,
            /// <summary>Read permission for others.</summary>
            OtherRead = 0x4,
            /// <summary>Execute permission for group.</summary>
            GroupExecute = 0x8,
            /// <summary>Write permission for group.</summary>
            GroupWrite = 0x10,
            /// <summary>Read permission for group.</summary>
            GroupRead = 0x20,
            /// <summary>Execute permission for owner.</summary>
            UserExecute = 0x40,
            /// <summary>Write permission for owner.</summary>
            UserWrite = 0x80,
            /// <summary>Read permission for owner.</summary>
            UserRead = 0x100,
            /// <summary>Sticky bit permission.</summary>
            StickyBit = 0x200,
            /// <summary>Set group permission.</summary>
            SetGroup = 0x400,
            /// <summary>Set user permission.</summary>
            SetUser = 0x800,
            /// <summary>FIFO.</summary>
            S_IFIFO = 0x1000,
            /// <summary>Character device.</summary>
            S_IFCHR = 0x2000,
            /// <summary>Directory.</summary>
            S_IFDIR = 0x4000,
            /// <summary>Block device.</summary>
            S_IFBLK = 0x6000,
            /// <summary>Regular file.</summary>
            S_IFREG = 0x8000,
            /// <summary>Symbolic link.</summary>
            S_IFLNK = 0xA000,
            /// <summary>Socket.</summary>
            S_IFSOCK = 0xC000,
        }

        private static FileStat CreateFile(string path, IFileStatistics entry)
        {
            var name = FileHelper.GetFullName(entry.Path);
            long? size = entry.Size > long.MaxValue ? long.MaxValue : (long)entry.Size;
            var mode = (UnixFileMode)(uint)entry.FileMode;

            if (SPECIAL_DIRS.Contains(name))
                return null;

            var type = ParseFileMode(mode);
            if (mode is UnixFileMode.None || type is FileType.Folder)
            {
                size = null;
            }

            return new(
                fileName: name,
                path: FileHelper.ConcatPaths(path, name),
                type: type,
                size: size,
                modifiedTime: entry.Time == default ? null : entry.Time.LocalDateTime,
                isLink: mode.HasFlag(UnixFileMode.S_IFLNK));
        }

        private static FileType ParseFileMode(UnixFileMode mode)
        {
            if (mode.HasFlag(UnixFileMode.S_IFSOCK)) return FileType.Socket;
            if (mode.HasFlag(UnixFileMode.S_IFLNK)) return FileType.Unknown;
            if (mode.HasFlag(UnixFileMode.S_IFREG)) return FileType.File;
            if (mode.HasFlag(UnixFileMode.S_IFBLK)) return FileType.BlockDevice;
            if (mode.HasFlag(UnixFileMode.S_IFDIR)) return FileType.Folder;
            if (mode.HasFlag(UnixFileMode.S_IFCHR)) return FileType.CharDevice;
            if (mode.HasFlag(UnixFileMode.S_IFIFO)) return FileType.FIFO;

            return FileType.Unknown;
        }

        public async Task<IReadOnlyList<(string, FileType)>> GetLinkTypeAsync(
            IEnumerable<string> filePaths,
            CancellationToken cancellationToken)
        {
            var paths = filePaths.ToArray();
            // Run readlink in a loop to support single param versions. File type tests are
            // shell built-ins because some embedded ADB devices do not provide stat at all.
            string readLinkCommand = string.Join(' ',
                new[]
                {
                    "readlink_cmd=$(command -v readlink 2>/dev/null);",
                    "if [ -z \"$readlink_cmd\" ] && command -v busybox >/dev/null 2>&1 && busybox readlink --help >/dev/null 2>&1; then readlink_cmd='busybox readlink'; fi;",
                    "for"
                }
                    .Concat(READLINK_ARGS1)
                    .Concat(paths.Select(path => EscapeAdbShellString(path)))
                    .Concat(READLINK_ARGS2));
            string stdout = await commandClient.ExecuteShellAsync(
                Device,
                readLinkCommand,
                cancellationToken).ConfigureAwait(false);

            return ParseLinkDetails(paths, stdout);
        }

        internal static IReadOnlyList<(string Target, FileType Type)> ParseLinkDetails(
            IEnumerable<string> filePaths,
            string output)
        {
            var paths = filePaths.ToArray();
            var links = AdbRegEx.RE_LINK_DETAILS().Matches(output)
                .Where(match => match.Success)
                .ToDictionary(
                    match => match.Groups["Source"].Value,
                    match => (
                        match.Groups["Target"].Value,
                        match.Groups["Type"].Value switch
                        {
                            "d" => FileType.Folder,
                            "f" => FileType.File,
                            "b" => FileType.BlockDevice,
                            "c" => FileType.CharDevice,
                            "p" => FileType.FIFO,
                            "s" => FileType.Socket,
                            "x" => FileType.BrokenLink,
                            _ => FileType.Unknown,
                        }));

            List<(string Target, FileType Type)> result = [];
            foreach (var file in paths)
            {
                result.Add(links.TryGetValue(file, out var link)
                    ? link
                    : ("", FileType.Unknown));
            }

            return result;
        }

        public async Task ListDirectoryAsync(string path, ChannelWriter<FileClass> output, CancellationToken cancellationToken)
        {
            try
            {
                var client = new AdbClient(AdbServerEndPoint);
                await foreach (var entry in client.GetDirectoryAsyncListing(
                    Device.DeviceData,
                    path,
                    Device.AndroidVersion >= 11,
                    cancellationToken).ConfigureAwait(false))
                {
                    var item = CreateFile(path, entry);

                    if (item is null)
                        continue;

                    await output.WriteAsync(FileClass.GenerateAndroidFile(item), cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                throw new IOException(Strings.Resources.S_LS_ERROR, e);
            }
            finally
            {
                output.TryComplete();
            }
        }

        public async Task<string> TranslateDevicePathAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(AdbExplorerConst.ADB_POLL_COMMAND_TIMEOUT);
            if (path.StartsWith('~'))
                path = path.Length == 1 ? "/" : path[1..];

            if (path.StartsWith("//"))
                path = path[1..];

            const string errorMarker = "__ADB_EXPLORER_PATH_ERROR__";
            string output = await commandClient.ExecuteShellAsync(
                Device,
                $"cd {EscapeAdbShellString(path)} 2>/dev/null && pwd || echo {errorMarker}",
                timeout.Token).ConfigureAwait(false);
            string result = output.TrimEnd(LINE_SEPARATORS);
            if (result.EndsWith(errorMarker, StringComparison.Ordinal))
                throw new DirectoryNotFoundException(path);

            return result;
        }

        public async Task<ulong> CountFilesAsync(
            string path,
            IEnumerable<string> includeNames,
            IEnumerable<string> excludeNames,
            CancellationToken cancellationToken)
        {
            string[] args = PrepFindArgs(path, includeNames, excludeNames, countOnly: true);
            string command = ShellCommands.TranslateCommand("find", ID);
            string output = await commandClient.ExecuteShellAsync(
                Device,
                $"{command} {string.Join(' ', args)}",
                cancellationToken).ConfigureAwait(false);
            return ulong.TryParse(output.Trim(), out ulong count) ? count : 0;
        }

        public async Task<ulong?> GetPackagesCountAsync(CancellationToken cancellationToken)
        {
            string output = await commandClient.ExecuteShellAsync(
                Device,
                "pm list packages | wc -l",
                cancellationToken).ConfigureAwait(false);
            return ulong.TryParse(output.Trim(), out ulong count) ? count : null;
        }

        public async Task<List<Package>> GetPackagesAsync(
            bool includeSystem,
            bool includeOptionalParameters,
            CancellationToken cancellationToken)
        {
            string optionalParameters = includeOptionalParameters
                ? " -U --show-versioncode"
                : "";
            var systemTask = includeSystem
                ? commandClient.ExecuteShellAsync(
                    Device,
                    $"pm list packages -s -f{optionalParameters}",
                    cancellationToken)
                : Task.FromResult("");
            var userTask = commandClient.ExecuteShellAsync(
                Device,
                $"pm list packages -3 -f{optionalParameters}",
                cancellationToken);
            await Task.WhenAll(systemTask, userTask).ConfigureAwait(false);

            List<Package> packages = [];
            if (includeSystem)
            {
                packages.AddRange((await systemTask.ConfigureAwait(false))
                    .Split(LINE_SEPARATORS, StringSplitOptions.RemoveEmptyEntries)
                    .Where(line => line.StartsWith("package:", StringComparison.Ordinal))
                    .Select(line => Package.New(line, Package.PackageType.System)));
            }

            packages.AddRange((await userTask.ConfigureAwait(false))
                .Split(LINE_SEPARATORS, StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line.StartsWith("package:", StringComparison.Ordinal))
                .Select(line => Package.New(line, Package.PackageType.User)));
            return packages;
        }

        public async Task<List<LogicalDrive>> GetDrivesAsync(CancellationToken cancellationToken = default)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(AdbExplorerConst.ADB_POLL_COMMAND_TIMEOUT);
            var requestToken = timeout.Token;
            List<LogicalDrive> drives = [];
            var rootTask = ReadDrivesAsync(AdbRegEx.RE_EMULATED_STORAGE_SINGLE(), requestToken, "/");
            var internalTask = ReadDrivesAsync(AdbRegEx.RE_EMULATED_STORAGE_SINGLE(), requestToken, "/sdcard");
            var externalTask = ReadDrivesAsync(AdbRegEx.RE_EMULATED_ONLY(), requestToken, EMULATED_DRIVES_GREP);
            var propsTask = LoadPropertiesAsync(requestToken);
            await Task.WhenAll(rootTask, internalTask, externalTask, propsTask).ConfigureAwait(false);

            var root = await rootTask.ConfigureAwait(false);
            if (root is null)
                return null;
            else if (root.Any())
                drives.Add(root.First());

            var intStorage = await internalTask.ConfigureAwait(false);
            if (intStorage is null)
                return drives;
            if (intStorage.Any())
                drives.Add(intStorage.First());

            var extStorage = await externalTask.ConfigureAwait(false);
            if (extStorage is null)
                return drives;

            Func<LogicalDrive, bool> predicate = drives.Any(drive => drive.Type is AbstractDrive.DriveType.Internal)
                ? d => d.Type is not AbstractDrive.DriveType.Internal and not AbstractDrive.DriveType.Root
                : d => d.Type is not AbstractDrive.DriveType.Root;

            drives.AddRange(extStorage.Where(predicate));

            if (drives.All(d => d.Type != AbstractDrive.DriveType.Internal))
            {
                drives.Insert(0, new(path: AdbExplorerConst.DEFAULT_PATH));
            }

            if (drives.All(d => d.Type != AbstractDrive.DriveType.Root))
            {
                drives.Insert(0, new(path: "/"));
            }

            await ClassifyExtensionDrivesAsync(drives, requestToken).ConfigureAwait(false);
            return drives;
        }

        private async Task ClassifyExtensionDrivesAsync(
            List<LogicalDrive> drives,
            CancellationToken cancellationToken)
        {
            var extensionDrives = drives
                .Where(drive => drive.Type is AbstractDrive.DriveType.Unknown)
                .ToArray();
            if (extensionDrives.Length == 0)
                return;

            LogicalDrive mmcDrive = null;
            if (!string.IsNullOrEmpty(MmcProp))
            {
                mmcDrive = extensionDrives.FirstOrDefault(drive => drive.ID == MmcProp);
            }
            else if (OtgProp is null)
            {
                string output = await commandClient.ExecuteShellAsync(
                    Device,
                    $"stat -c '%t,%T' {string.Join(' ', MMC_BLOCK_DEVICES)} 2>/dev/null; sm list-volumes public 2>/dev/null",
                    cancellationToken).ConfigureAwait(false);
                var nodeMatch = AdbRegEx.RE_MMC_BLOCK_DEVICE_NODE().Match(output);
                if (nodeMatch.Success)
                {
                    if (extensionDrives.Length == 1)
                    {
                        mmcDrive = extensionDrives[0];
                    }
                    else if (int.TryParse(
                            nodeMatch.Groups["major"].Value,
                            NumberStyles.HexNumber,
                            CultureInfo.InvariantCulture,
                            out int major)
                        && int.TryParse(
                            nodeMatch.Groups["minor"].Value,
                            NumberStyles.HexNumber,
                            CultureInfo.InvariantCulture,
                            out int minor))
                    {
                        var volumeMatch = Regex.Match(
                            output,
                            $@"{major},{minor}\s+mounted\s+(?<id>[\w-]+)");
                        if (volumeMatch.Success)
                            mmcDrive = extensionDrives.FirstOrDefault(
                                drive => drive.ID == volumeMatch.Groups["id"].Value);
                    }
                }
            }

            if (mmcDrive is not null)
                mmcDrive.Type = AbstractDrive.DriveType.Expansion;
            DeviceHelper.SetExternalDrives(extensionDrives);
        }

        private async Task<IEnumerable<LogicalDrive>> ReadDrivesAsync(
            Regex re,
            CancellationToken cancellationToken,
            params string[] args)
        {
            try
            {
                string output = await commandClient.ExecuteShellAsync(
                    Device,
                    $"df {string.Join(' ', args)}",
                    cancellationToken).ConfigureAwait(false);
                return re.Matches(output).Select(m => new LogicalDrive(
                    m.Groups,
                    isEmulator: Type is DeviceType.Emulator,
                    forcePath: args[0] == "/" ? "/" : ""));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return null;
            }
        }

        private Dictionary<string, string> props;
        private async Task LoadPropertiesAsync(CancellationToken cancellationToken)
        {
            if (props is not null)
                return;

            try
            {
                string output = await commandClient.ExecuteShellAsync(
                    Device,
                    GET_PROP,
                    cancellationToken).ConfigureAwait(false);
                props = output.Split(LINE_SEPARATORS, StringSplitOptions.RemoveEmptyEntries)
                    .Where(line => line.Length > 1 && line[0] == '[' && line[^1] == ']')
                    .Select(line => line.Split(':', 2))
                    .Where(parts => parts.Length == 2)
                    .ToDictionary(
                        parts => parts[0].Trim('[', ']', ' '),
                        parts => parts[1].Trim('[', ']', ' '),
                        StringComparer.Ordinal);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                props = [];
            }
        }

        public string MmcProp => props?.GetValueOrDefault(MMC_PROP);
        public string OtgProp => props?.GetValueOrDefault(OTG_PROP);

        public byte? AndroidVersion;

        public async Task<string> GetAndroidVersion(CancellationToken cancellationToken = default)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(AdbExplorerConst.ADB_POLL_COMMAND_TIMEOUT);
            string version = (await commandClient.ExecuteShellAsync(
                Device,
                $"getprop {ANDROID_VERSION}",
                timeout.Token).ConfigureAwait(false)).Trim();
            AndroidVersion = byte.TryParse(version.Split('.')[0], out byte ver) ? ver : null;

            return version;
        }

        public static void Reboot(string deviceId, string arg)
        {
            if (ExecuteDeviceAdbCommand(deviceId, "reboot", out string stdout, out string stderr, CancellationToken.None, arg) != 0)
                throw new Exception(string.IsNullOrEmpty(stderr) ? stdout : stderr);
        }

    }
}
