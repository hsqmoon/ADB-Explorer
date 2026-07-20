// Virtual File Data Object library by David Anson
// https://dlaa.me/blog/post/9913083
// Used and modified under the MIT license

using ADB_Explorer.Helpers;
using ADB_Explorer.Models;
using ADB_Explorer.Services.AppInfra;
using ADB_Explorer.ViewModels;
using System.Runtime.InteropServices.ComTypes;
using Vanara.Extensions;
using static ADB_Explorer.Services.FileDescriptor;
using static Vanara.PInvoke.Shell32;

namespace ADB_Explorer.Services;

/// <summary>
/// Handles everything related to Shell Drag & Drop (including clipboard) 
/// </summary>
public sealed class VirtualFileDataObject : ViewModelBase, System.Runtime.InteropServices.ComTypes.IDataObject, IAsyncOperation
{
    public static FileGroup SelfFileGroup { get; private set; }
    public static IEnumerable<FileClass> SelfFiles { get; private set; }
    public static string DummyFileName { get; private set; }
    private static VirtualFileDataObject clipboardOwner;

    /// <summary>
    /// In-order list of registered data objects.
    /// </summary>
    private readonly List<DataObject> dataObjects = [];
    private readonly object dataObjectsLock = new();
    private readonly object selfDataLock = new();
    private readonly object operationsQueueLock = new();
    private readonly CancellationTokenSource preparationCancellation = new();
    private readonly TaskCompletionSource<bool> preparationCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IReadOnlyList<FileClass> selfFiles;
    private FileGroup selfFileGroup;
    private bool selfDataReleased;
    private Task operationsQueueTask;
    private int operationsQueueSucceeded;

    /// <summary>
    /// Tracks whether an asynchronous operation is ongoing.
    /// </summary>
    private volatile bool inOperation;

    public DataObjectMethod Method { get; private set; }

    public VirtualFileDataObject(DragDropEffects preferredDropEffect = DragDropEffects.Copy | DragDropEffects.Move, DataObjectMethod method = DataObjectMethod.DragDrop)
    {
        PreferredDropEffect = preferredDropEffect;
        Method = method;
    }

    #region IDataObject Members
    // Explicit interface implementation hides the technical details from users of VirtualFileDataObject.

    /// <summary>
    /// Creates a connection between a data object and an advisory sink.
    /// </summary>
    /// <param name="pFormatetc">A FORMATETC structure that defines the format, target device, aspect, and medium that will be used for future notifications.</param>
    /// <param name="advf">One of the ADVF values that specifies a group of flags for controlling the advisory connection.</param>
    /// <param name="adviseSink">A pointer to the IAdviseSink interface on the advisory sink that will receive the change notification.</param>
    /// <param name="connection">When this method returns, contains a pointer to a DWORD token that identifies this connection.</param>
    /// <returns>HRESULT success code.</returns>
    int System.Runtime.InteropServices.ComTypes.IDataObject.DAdvise(ref FORMATETC pFormatetc, ADVF advf, IAdviseSink adviseSink, out int connection)
    {
        NativeMethods.ThrowExceptionForHR(NativeMethods.HResult.OLE_E_ADVISENOTSUPPORTED);
        throw new NotImplementedException();
    }

    /// <summary>
    /// Destroys a notification connection that had been previously established.
    /// </summary>
    /// <param name="connection">A DWORD token that specifies the connection to remove.</param>
    void System.Runtime.InteropServices.ComTypes.IDataObject.DUnadvise(int connection)
    {
        NativeMethods.ThrowExceptionForHR(NativeMethods.HResult.OLE_E_ADVISENOTSUPPORTED);
        throw new NotImplementedException();
    }

    /// <summary>
    /// Creates an object that can be used to enumerate the current advisory connections.
    /// </summary>
    /// <param name="enumAdvise">When this method returns, contains an IEnumSTATDATA that receives the interface pointer to the new enumerator object.</param>
    /// <returns>HRESULT success code.</returns>
    int System.Runtime.InteropServices.ComTypes.IDataObject.EnumDAdvise(out IEnumSTATDATA enumAdvise)
    {
        NativeMethods.ThrowExceptionForHR(NativeMethods.HResult.OLE_E_ADVISENOTSUPPORTED);
        throw new NotImplementedException();
    }

    /// <summary>
    /// Creates an object for enumerating the FORMATETC structures for a data object.
    /// </summary>
    /// <param name="direction">One of the DATADIR values that specifies the direction of the data.</param>
    /// <returns>IEnumFORMATETC interface.</returns>
    IEnumFORMATETC System.Runtime.InteropServices.ComTypes.IDataObject.EnumFormatEtc(DATADIR direction)
    {
        if (direction != DATADIR.DATADIR_GET)
            throw new NotImplementedException();

        FORMATETC[] formats;
        lock (dataObjectsLock)
        {
            formats = [.. dataObjects.Select(dataObject => dataObject.FORMATETC)];
        }

        if (formats.Length == 0)
        {
            // Note: SHCreateStdEnumFmtEtc fails for a count of 0; throw helpful exception
            throw new InvalidOperationException("VirtualFileDataObject requires at least one data object to enumerate.");
        }

        // Create enumerator and return it
        var res = NativeMethods.SHCreateStdEnumFmtEtc(formats.Length, formats, out IEnumFORMATETC enumerator);
        if (res is NativeMethods.HResult.Ok)
        {
            return enumerator;
        }

        // Returning null here can cause an AV in the caller; throw instead
        NativeMethods.ThrowExceptionForHR(res);
        throw new NotImplementedException();
    }

    /// <summary>
    /// Provides a standard FORMATETC structure that is logically equivalent to a more complex structure.
    /// </summary>
    /// <param name="formatIn">A pointer to a FORMATETC structure that defines the format, medium, and target device that the caller would like to use to retrieve data in a subsequent call such as GetData.</param>
    /// <param name="formatOut">When this method returns, contains a pointer to a FORMATETC structure that contains the most general information possible for a specific rendering, making it canonically equivalent to formatetIn.</param>
    /// <returns>HRESULT success code.</returns>
    int System.Runtime.InteropServices.ComTypes.IDataObject.GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Obtains data from a source data object.
    /// </summary>
    /// <param name="format">A pointer to a FORMATETC structure that defines the format, medium, and target device to use when passing the data.</param>
    /// <param name="medium">When this method returns, contains a pointer to the STGMEDIUM structure that indicates the storage medium containing the returned data through its tymed member, and the responsibility for releasing the medium through the value of its pUnkForRelease member.</param>
    void System.Runtime.InteropServices.ComTypes.IDataObject.GetData(ref FORMATETC format, out STGMEDIUM medium)
    {
        var formatCopy = format;
        medium = new();

        var hr = (NativeMethods.HResult)((System.Runtime.InteropServices.ComTypes.IDataObject)this).QueryGetData(ref format);

#if !DEPLOY
        AdbDataFormat adbDataFormat = AdbDataFormats.GetFormat(formatCopy.cfFormat);

        // Unknown formats are not printed
        if (adbDataFormat is not null)
            DebugLog.PrintLine($"Query: {adbDataFormat.Name}");
#endif

        if (hr is NativeMethods.HResult.Ok)
        {
            // Find the best match
            DataObject dataObject;
            lock (dataObjectsLock)
            {
                dataObject = dataObjects.FirstOrDefault(d =>
                        d.FORMATETC.cfFormat == formatCopy.cfFormat
                        && d.FORMATETC.dwAspect == formatCopy.dwAspect
                        && 0 != (d.FORMATETC.tymed & formatCopy.tymed)
                        && d.FORMATETC.lindex == formatCopy.lindex);
            }

            if (dataObject is not null)
            {
#if !DEPLOY
                var index = adbDataFormat == AdbDataFormats.FileContents
                        ? $"[{dataObject.FORMATETC.lindex}]"
                        : "";

                // Unknown formats are not printed
                if (adbDataFormat is not null)
                    DebugLog.PrintLine($"Get data: {adbDataFormat.Name}{index}");
#endif

                // Populate the STGMEDIUM
                medium.tymed = dataObject.FORMATETC.tymed;
                var result = dataObject.GetData(); // Possible call to user code

                hr = result.Item2;
                if (hr is NativeMethods.HResult.Ok)
                {
                    medium.unionmember = result.Item1;
                }
            }
            else
            {
                // Couldn't find a match
                hr = NativeMethods.HResult.DV_E_FORMATETC;
            }
        }

        // Not redundant; hr gets updated in the block above
        if (hr is NativeMethods.HResult.Ok)
            return;

        // We seem unable to send data to File Explorer in DEBUG, even when not debugging.
        // This might be because of the FileContents data format, which is an async stream
        // compared to all other data formats which are just byte arrays on HGlobal, already populated with the data
        var ex = Marshal.GetExceptionForHR((int)hr);
#if DEBUG
        Trace.WriteLine(ex?.Message);
#else
        throw ex;
#endif
    }

    /// <summary>
    /// Obtains data from a source data object.
    /// </summary>
    /// <param name="format">A pointer to a FORMATETC structure that defines the format, medium, and target device to use when passing the data.</param>
    /// <param name="medium">A STGMEDIUM that defines the storage medium containing the data being transferred.</param>
    void System.Runtime.InteropServices.ComTypes.IDataObject.GetDataHere(ref FORMATETC format, ref STGMEDIUM medium)
    {
        throw new NotImplementedException();
    }
    
    /// <summary>
    /// Determines whether the data object is capable of rendering the data described in the FORMATETC structure.
    /// </summary>
    /// <param name="format">A pointer to a FORMATETC structure that defines the format, medium, and target device to use for the query.</param>
    /// <returns>HRESULT success code.</returns>
    int System.Runtime.InteropServices.ComTypes.IDataObject.QueryGetData(ref FORMATETC format)
    {
        NativeMethods.HResult GetError(FORMATETC format)
        {
            var formatCopy = format; // Cannot use ref or out parameter inside an anonymous method, lambda expression, or query expression
            DataObject[] objects;
            lock (dataObjectsLock)
            {
                objects = [.. dataObjects];
            }

            var formatMatches = objects.Where(d => d.FORMATETC.cfFormat == formatCopy.cfFormat);
            if (!formatMatches.Any())
            {
                return NativeMethods.HResult.DV_E_FORMATETC;
            }
            var tymedMatches = formatMatches.Where(d => 0 != (d.FORMATETC.tymed & formatCopy.tymed));
            if (!tymedMatches.Any())
            {
                return NativeMethods.HResult.DV_E_TYMED;
            }
            var aspectMatches = tymedMatches.Where(d => d.FORMATETC.dwAspect == formatCopy.dwAspect);
            if (!aspectMatches.Any())
            {
                return NativeMethods.HResult.DV_E_DVASPECT;
            }
            return NativeMethods.HResult.Ok;
        }

        return (int)GetError(format);
    }

    /// <summary>
    /// Transfers data to the object that implements this method.
    /// </summary>
    /// <param name="formatIn">A FORMATETC structure that defines the format used by the data object when interpreting the data contained in the storage medium.</param>
    /// <param name="medium">A STGMEDIUM structure that defines the storage medium in which the data is being passed.</param>
    /// <param name="release">true to specify that the data object called, which implements SetData, owns the storage medium after the call returns.</param>
    void System.Runtime.InteropServices.ComTypes.IDataObject.SetData(ref FORMATETC formatIn, ref STGMEDIUM medium, bool release)
    {
        var handled = false;
        if (medium.tymed == formatIn.tymed
            && formatIn is {
                dwAspect: DVASPECT.DVASPECT_CONTENT, 
                tymed: TYMED.TYMED_HGLOBAL
            })
        {
            // Supported format; capture the data
            var ptr = NativeMethods.MGlobalLock(medium.unionmember);
            if (IntPtr.Zero != ptr)
            {
                try
                {
                    var length = NativeMethods.MGlobalSize(ptr).ToInt32();
                    var data = new byte[length];
                    Marshal.Copy(ptr, data, 0, length);
                    
                    // Store it in our own format
                    var format = AdbDataFormats.GetFormat(formatIn.cfFormat) ?? new AdbDataFormat(formatIn.cfFormat);
                    SetData(format, data);
                    handled = true;
                }
                finally
                {
                    NativeMethods.MGlobalUnlock(medium.unionmember);
                }
            }

            // Release memory if we now own it
            if (release)
            {
                Marshal.FreeHGlobal(medium.unionmember);
            }
        }

        // Throw if unhandled
        if (!handled)
        {
            throw new NotImplementedException();
        }
    }

#endregion

    public string[] GetFormats()
    {
        lock (dataObjectsLock)
        {
            return [.. dataObjects.Select(o => AdbDataFormats.GetFormatName(o.FORMATETC.cfFormat))];
        }
    }

    public static FORMATETC CreateFormat(AdbDataFormat dataFormat, int index = -1) => new()
    {
        cfFormat = dataFormat,
        ptd = IntPtr.Zero,
        dwAspect = DVASPECT.DVASPECT_CONTENT,
        lindex = index,
        tymed = dataFormat.tymed,
    };

    public void UpdateData(AdbDataFormat dataFormat, IEnumerable<byte> data)
    {
        var dataArray = data as byte[] ?? data.ToArray();
        Func<(HANDLE, NativeMethods.HResult)> getData = () =>
        {
            var ptr = Marshal.AllocHGlobal(dataArray.Length);
            Marshal.Copy(dataArray, 0, ptr, dataArray.Length);
            return (ptr, NativeMethods.HResult.Ok);
        };

        lock (dataObjectsLock)
        {
            var dataObject = dataObjects.FirstOrDefault(item => item.FORMATETC.cfFormat == dataFormat);
            if (dataObject is null)
            {
                dataObjects.Add(new()
                {
                    FORMATETC = CreateFormat(dataFormat),
                    GetData = getData,
                });
            }
            else
            {
                dataObject.GetData = getData;
            }
        }
    }

    /// <summary>
    /// Provides data for the specified data format (HGLOBAL).
    /// </summary>
    /// <param name="dataFormat">Data format.</param>
    /// <param name="data">Sequence of data.</param>
    public void SetData(AdbDataFormat dataFormat, IEnumerable<byte> data)
    {
        var dataArray = data as byte[] ?? data.ToArray();
        DataObject dataObject = new()
        {
            FORMATETC = CreateFormat(dataFormat),
            GetData = () =>
            {
                var ptr = Marshal.AllocHGlobal(dataArray.Length);
                Marshal.Copy(dataArray, 0, ptr, dataArray.Length);
                return (ptr, NativeMethods.HResult.Ok);
            },
        };

        lock (dataObjectsLock)
        {
            dataObjects.Add(dataObject);
        }
    }

    public void UpdateData(AdbDataFormat dataFormat, IEnumerable<StreamContents> dataStreams)
    {
        lock (dataObjectsLock)
        {
            // Remove all previous streams
            dataObjects.RemoveAll(d => d.FORMATETC.cfFormat == dataFormat);

            // Set n CFSTR_FILECONTENTS
            var index = 0;
            foreach (var stream in dataStreams)
            {
                SetData(dataFormat, index, stream);
                index++;
            }
        }
    }

    /// <summary>
    /// Provides data for the specified data format and index (ISTREAM).
    /// </summary>
    /// <param name="dataFormat">Data format.</param>
    /// <param name="index">Index of data.</param>
    /// <param name="streamData">Action generating the data.</param>
    /// <remarks>
    /// Uses Stream instead of IEnumerable(T) because Stream is more likely
    /// to be natural for the expected scenarios.
    /// </remarks>
    public void SetData(AdbDataFormat dataFormat, int index, StreamContents streamData)
    {
        DataObject dataObject = new()
        {
            FORMATETC = CreateFormat(dataFormat, index),
            GetData = () =>
            {
                var iStream = streamData();
                if (iStream is null)
                    return (IntPtr.Zero, NativeMethods.HResult.Fail);

                var ptr = Marshal.GetComInterfaceForObject(iStream, typeof(IStream));
                Marshal.ReleaseComObject(iStream);

                return (ptr, NativeMethods.HResult.Ok);
            },
        };

        lock (dataObjectsLock)
        {
            dataObjects.Add(dataObject);
        }
    }

    public void SetFileDescriptors(IEnumerable<FileDescriptor> fileDescriptors, bool includeContent = true)
    {
        FileGroup group = new(fileDescriptors);
        SetSelfFileGroup(group);

        UpdateData(AdbDataFormats.FileDescriptor, group.GroupDescriptorBytes);

        if (includeContent)
            UpdateData(AdbDataFormats.FileContents, group.DataStreams);
    }

    public void SetAdbDrag(IEnumerable<FileClass> files, ADBService.AdbDevice device)
    {
        var fileList = files.ToArray();
        lock (selfDataLock)
        {
            selfFiles = fileList;
            if (!selfDataReleased)
                SelfFiles = fileList;
        }

        NativeMethods.ADBDRAGLIST adbDrag = new(device, fileList);
        SetData(AdbDataFormats.AdbDrop, adbDrag.Bytes);
    }

    private void SetSelfFileGroup(FileGroup group)
    {
        lock (selfDataLock)
        {
            if (selfDataReleased)
                return;

            selfFileGroup = group;
            SelfFileGroup = group;
        }
    }

    private void ReleaseDragData()
    {
        try
        {
            preparationCancellation.Cancel();
        }
        catch (ObjectDisposedException)
        { }

        lock (selfDataLock)
        {
            if (selfDataReleased)
                return;

            selfDataReleased = true;
            if (ReferenceEquals(SelfFiles, selfFiles))
                SelfFiles = null;
            if (ReferenceEquals(SelfFileGroup, selfFileGroup))
                SelfFileGroup = null;

            selfFiles = null;
            selfFileGroup = null;
        }

        Interlocked.CompareExchange(ref clipboardOwner, null, this);
    }

    internal static void ReleaseClipboardData()
        => Volatile.Read(ref clipboardOwner)?.ReleaseDragData();

    internal void AcquireClipboardOwnership()
    {
        var previousOwner = Interlocked.Exchange(ref clipboardOwner, this);
        if (previousOwner is not null && !ReferenceEquals(previousOwner, this))
            previousOwner.ReleaseDragData();
    }

    public void SetFileDrop(params IEnumerable<string> files)
        => SetData(AdbDataFormats.FileDrop, new NativeMethods.CFHDROP(files).Bytes);

    private List<FileSyncOperation> Operations { get; set; } = [];

    internal bool AreOperationsCompleted =>
        Operations.All(operation => operation.Status is FileOperation.OperationStatus.Completed);

    internal Task EnsureOperationsQueued()
    {
        lock (operationsQueueLock)
            return operationsQueueTask ??= QueuePreparedOperationsAsync();
    }

    private async Task QueuePreparedOperationsAsync()
    {
        try
        {
            await preparationCompletion.Task.WaitAsync(preparationCancellation.Token).ConfigureAwait(false);
            if (Operations.Count == 0)
            {
                Volatile.Write(ref operationsQueueSucceeded, 1);
                return;
            }

            if (Application.Current is not App)
            {
                Volatile.Write(ref operationsQueueSucceeded, -1);
                return;
            }

            await App.ActiveFileOperations.AddOperationsAsync(
                Operations,
                preparationCancellation.Token).ConfigureAwait(false);
            Volatile.Write(ref operationsQueueSucceeded, 1);
        }
        catch (OperationCanceledException) when (preparationCancellation.IsCancellationRequested)
        {
            Volatile.Write(ref operationsQueueSucceeded, -1);
        }
        catch (Exception ex)
        {
            Volatile.Write(ref operationsQueueSucceeded, -1);
            App.ReportBackgroundFailure(ex, "virtual-files.queue");
        }
    }

    internal bool DidOperationsQueueSuccessfully =>
        Volatile.Read(ref operationsQueueSucceeded) == 1;

    private async Task CompletePreparationAsync(
        Task<(List<FileSyncOperation> Operations, FileDescriptor[] Descriptors, bool IncludeContent)> preparation,
        IReadOnlyList<FileClass> files,
        bool updateCursor)
    {
        try
        {
            var result = await preparation.ConfigureAwait(false);
            preparationCancellation.Token.ThrowIfCancellationRequested();
            Operations = result.Operations;
            SetFileDescriptors(result.Descriptors, result.IncludeContent);
            preparationCompletion.TrySetResult(true);
        }
        catch (OperationCanceledException) when (preparationCancellation.IsCancellationRequested)
        {
            preparationCompletion.TrySetCanceled(preparationCancellation.Token);
        }
        catch (Exception ex)
        {
            preparationCompletion.TrySetException(ex);
            App.ReportBackgroundFailure(ex, "virtual-files.prepare");
        }
        finally
        {
            files.ForEach(file => file.ClearDescriptors());
            if (updateCursor && Application.Current is App app)
            {
                app.EnqueueUiLatest(
                    "virtual-files.cursor",
                    "virtual-files.cursor",
                    () => App.RuntimeSettings.MainCursor = Cursors.Arrow);
            }
        }
    }

    private DragDropEffects currentEffect = DragDropEffects.None;
    public DragDropEffects CurrentEffect
    {
        get => currentEffect;
        set
        {
            if (Set(ref currentEffect, value))
                App.CopyPaste.CurrentDropEffect = value & ~DragDropEffects.Scroll;
        }
    }

    public DragDropEffects? PasteSucceeded
    {
        get => GetDropEffect(AdbDataFormats.PasteSucceeded);
        set => SetData(AdbDataFormats.PasteSucceeded, BitConverter.GetBytes((UInt32)value));
    }

    public DragDropEffects? PerformedDropEffect
    {
        get => GetDropEffect(AdbDataFormats.PerformedDropEffect);
        set => SetData(AdbDataFormats.PerformedDropEffect, BitConverter.GetBytes((UInt32)value));
    }

    public DragDropEffects? PreferredDropEffect
    {
        get => GetDropEffect(AdbDataFormats.PreferredDropEffect);
        set => UpdateData(AdbDataFormats.PreferredDropEffect, BitConverter.GetBytes((UInt32)value));
    }

    public static DragDropEffects GetPreferredDropEffect(System.Windows.IDataObject dataObject)
    {
        if (!dataObject.GetDataPresent(AdbDataFormats.PreferredDropEffect)
            || dataObject.GetData(AdbDataFormats.PreferredDropEffect) is not MemoryStream stream)
            return DragDropEffects.None;

        return (DragDropEffects)BitConverter.ToInt32(stream.ToArray());
    }

    public static void SetPreferredDropEffect(System.Windows.IDataObject dataObject, DragDropEffects dropEffects)
        => dataObject.SetData(AdbDataFormats.PreferredDropEffect, BitConverter.GetBytes((UInt32)dropEffects));

    /// <summary>
    /// Gets the DragDropEffects value (if any) previously set on the object.
    /// </summary>
    /// <param name="format">Clipboard format.</param>
    /// <returns>DragDropEffects value or null.</returns>
    private DragDropEffects? GetDropEffect(short format)
    {
        // Get the most recent setting
        DataObject dataObject;
        lock (dataObjectsLock)
        {
            dataObject = dataObjects.LastOrDefault(d =>
                format == d.FORMATETC.cfFormat
                && d.FORMATETC is {
                    dwAspect: DVASPECT.DVASPECT_CONTENT,
                    tymed: TYMED.TYMED_HGLOBAL
                });
        }

        if (dataObject is not null)
        {
            // Read the value and return it
            var result = dataObject.GetData();
            try
            {
                if (result.Item2 is NativeMethods.HResult.Ok)
                {
                    var ptr = NativeMethods.MGlobalLock(result.Item1);
                    if (IntPtr.Zero != ptr)
                    {
                        try
                        {
                            var length = NativeMethods.MGlobalSize(result.Item1).ToInt32();
                            if (4 == length)
                            {
                                var data = new byte[length];
                                Marshal.Copy(ptr, data, 0, length);
                                return (DragDropEffects)(BitConverter.ToUInt32(data, 0));
                            }
                        }
                        finally
                        {
                            NativeMethods.MGlobalUnlock(result.Item1);
                        }
                    }
                }
            }
            finally
            {
                if (result.Item1 != IntPtr.Zero)
                    Marshal.FreeHGlobal(result.Item1);
            }
        }
        return null;
    }

    #region IAsyncOperation Members
    // Explicit interface implementation hides the technical details from users of VirtualFileDataObject.

    /// <summary>
    /// Called by a drop source to specify whether the data object supports asynchronous data extraction.
    /// </summary>
    /// <param name="fDoOpAsync">A Boolean value that is set to VARIANT_TRUE to indicate that an asynchronous operation is supported, or VARIANT_FALSE otherwise.</param>
    void IAsyncOperation.SetAsyncMode(int fDoOpAsync)
    {
        // Synchronous mode is no longer supported
    }

    /// <summary>
    /// Called by a drop target to determine whether the data object supports asynchronous data extraction.
    /// </summary>
    /// <param name="pfIsOpAsync">A Boolean value that is set to VARIANT_TRUE to indicate that an asynchronous operation is supported, or VARIANT_FALSE otherwise.</param>
    void IAsyncOperation.GetAsyncMode(out int pfIsOpAsync)
    {
        // Synchronous mode is no longer supported
        pfIsOpAsync = NativeMethods.VARIANT_TRUE;
    }

    /// <summary>
    /// Called by a drop target to indicate that asynchronous data extraction is starting.
    /// </summary>
    /// <param name="pbcReserved">Reserved. Set this value to NULL.</param>
    void IAsyncOperation.StartOperation(IBindCtx pbcReserved)
    {
        inOperation = true;
        _ = EnsureOperationsQueued();
    }

    /// <summary>
    /// Called by the drop source to determine whether the target is extracting data asynchronously.
    /// </summary>
    /// <param name="pfInAsyncOp">Set to VARIANT_TRUE if data extraction is being handled asynchronously, or VARIANT_FALSE otherwise.</param>
    void IAsyncOperation.InOperation(out int pfInAsyncOp)
    {
        pfInAsyncOp = inOperation ? NativeMethods.VARIANT_TRUE : NativeMethods.VARIANT_FALSE;
    }

    /// <summary>
    /// Notifies the data object that that asynchronous data extraction has ended.
    /// </summary>
    /// <param name="hResult">An HRESULT value that indicates the outcome of the data extraction. Set to S_OK if successful, or a COM error code otherwise.</param>
    /// <param name="pbcReserved">Reserved. Set to NULL.</param>
    /// <param name="dwEffects">A DROPEFFECT value that indicates the result of an optimized move. This should be the same value that would be passed to the data object as a CFSTR_PERFORMEDDROPEFFECT format with a normal data extraction operation.</param>
    void IAsyncOperation.EndOperation(int hResult, IBindCtx pbcReserved, uint dwEffects)
    {
        inOperation = false;
        ReleaseDragData();
    }

    #endregion

    /// <summary>
    /// Class representing the result of a SetData call.
    /// </summary>
    private class DataObject
    {
        /// <summary>
        /// FORMATETC structure for the data.
        /// </summary>
        public FORMATETC FORMATETC { get; init; }

        /// <summary>
        /// Func returning the data as an IntPtr and an HRESULT success code.
        /// </summary>
        public Func<(HANDLE, NativeMethods.HResult)> GetData { get; set; }
    }

    public static VirtualFileDataObject PrepareTransfer(IEnumerable<Package> packages,
                                                        DataObjectMethod method = DataObjectMethod.DragDrop)
    {
        var packageList = packages.ToList();
        var device = App.ActiveAdbDevice;
        App.FileActions.IsSelectionIllegalOnWindows =
        App.FileActions.IsSelectionConflictingOnFuse = false;

        var tempDragPath = App.RuntimeSettings.ResetTempDragPath();
        VirtualFileDataObject vfdo = new(DragDropEffects.Copy, method);

        var files = packageList
            .Select(package => new FileClass(
                FileHelper.GetFullName(package.Path),
                package.Path,
                AbstractFile.FileType.File,
                loadIcon: false))
            .ToList();
        vfdo.SetAdbDrag(files, device);
        vfdo.SetData(AdbDataFormats.FileDescriptor, []);
        vfdo.SetData(AdbDataFormats.FileContents, []);
        vfdo.SetSelfFileGroup(new([]));

        App.RuntimeSettings.MainCursor = Cursors.AppStarting;
        var preparation = Task.Run(() =>
        {
            var cancellationToken = vfdo.preparationCancellation.Token;
            Directory.CreateDirectory(tempDragPath);
            var operations = files.Zip(packageList).Select(item =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return item.First.PrepareDescriptors(
                    vfdo,
                    tempDragPath,
                    device,
                    item.Second.Name + ".apk");
            }).ToList();
            return (
                Operations: operations,
                Descriptors: files.SelectMany(file => file.Descriptors).ToArray(),
                IncludeContent: true);
        }, vfdo.preparationCancellation.Token);
        _ = vfdo.CompletePreparationAsync(preparation, files, updateCursor: true);

        return vfdo;
    }

    public static VirtualFileDataObject PrepareTransfer(IEnumerable<FileClass> files,
                                                        DragDropEffects preferredEffect = DragDropEffects.Copy,
                                                        DataObjectMethod method = DataObjectMethod.DragDrop)
    {
        var fileList = files.ToList();
        var device = App.ActiveAdbDevice;
        var tempDragPath = App.RuntimeSettings.ResetTempDragPath();

        App.FileActions.IsSelectionIllegalOnWindows = !FileHelper.FileNameLegal(App.ExplorerState.SelectedFiles, FileHelper.RenameTarget.Windows);
        App.FileActions.IsSelectionConflictingOnFuse = App.ExplorerState.SelectedFiles.Select(f => f.FullName)
            .Distinct(StringComparer.InvariantCultureIgnoreCase)
            .Count() != App.ExplorerState.SelectedFiles.Count();

        VirtualFileDataObject vfdo = new(preferredEffect, method);
        vfdo.SetAdbDrag(fileList, device);

        var includeContent =
            !App.FileActions.IsSelectionIllegalOnWindows
            && !App.FileActions.IsSelectionConflictingOnFuse
            && !App.FileActions.IsRecycleBin;

        if (includeContent)
        {
            // Add placeholders before starting preparation so a fast background task cannot be overwritten by empty data.
            vfdo.SetData(AdbDataFormats.FileDescriptor, []);
            vfdo.SetData(AdbDataFormats.FileContents, []);
            vfdo.SetSelfFileGroup(new([]));

            App.RuntimeSettings.MainCursor = Cursors.AppStarting;
            var preparation = Task.Run(() =>
            {
                // Prepare file ops recursively for folders
                var cancellationToken = vfdo.preparationCancellation.Token;
                Directory.CreateDirectory(tempDragPath);
                var treesBySource = FileHelper.GetFolderTrees(
                    fileList.Where(file => file.IsDirectory).Select(file => file.FullPath),
                    device,
                    cancellationToken);
                var operations = fileList.Select(file =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return file.PrepareDescriptors(
                        vfdo,
                        tempDragPath,
                        device,
                        file.FullName,
                        children: file.IsDirectory && treesBySource.TryGetValue(file.FullPath, out var tree)
                            ? tree.Skip(1)
                            : null);
                }).ToList();
                return (
                    Operations: operations,
                    Descriptors: fileList.SelectMany(file => file.Descriptors).ToArray(),
                    IncludeContent: true);
            }, vfdo.preparationCancellation.Token);
            _ = vfdo.CompletePreparationAsync(preparation, fileList, updateCursor: true);

        }
        else // When the selection is illegal for Windows
        {
            vfdo.SetData(AdbDataFormats.FileDescriptor, []);
            vfdo.SetSelfFileGroup(new([]));

            var preparation = Task.Run(() =>
            {
                var cancellationToken = vfdo.preparationCancellation.Token;
                foreach (var file in fileList)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    file.PrepareDescriptors(vfdo, tempDragPath, device, file.FullName, false);
                }

                return (
                    Operations: new List<FileSyncOperation>(),
                    Descriptors: fileList.SelectMany(file => file.Descriptors).ToArray(),
                    IncludeContent: false);
            }, vfdo.preparationCancellation.Token);
            _ = vfdo.CompletePreparationAsync(preparation, fileList, updateCursor: false);

        }

        return vfdo;
    }

    public enum DataObjectMethod
    {
        DragDrop,
        Clipboard,
    }

    public void SendObjectToShell(DataObjectMethod method, DependencyObject dragSource = null, DragDropEffects allowedEffects = DragDropEffects.None)
    {
        Method = method;

        try
        {
            if (method is DataObjectMethod.DragDrop)
            {
                DoDragDrop(dragSource, allowedEffects);
            }
            else if (method is DataObjectMethod.Clipboard)
            {
                CurrentEffect = allowedEffects;
                PerformedDropEffect = allowedEffects;
                _ = SendToClipboardAsync();
            }
            else
                throw new NotSupportedException();
        }
        catch (COMException)
        {
            // Failure; no way to recover
            ReleaseDragData();
        }
    }

    /// <summary>
    /// Initiates a drag-and-drop operation.
    /// </summary>
    /// <param name="dragSource">A reference to the dependency object that is the source of the data being dragged.</param>
    /// <param name="allowedEffects">One of the DragDropEffects values that specifies permitted effects of the drag-and-drop operation.</param>
    /// <returns>One of the DragDropEffects values that specifies the final effect that was performed during the drag-and-drop operation.</returns>
    /// <remarks>
    /// Call this method instead of System.Windows.DragDrop.DoDragDrop because this method handles IDataObject better.
    /// </remarks>
    public void DoDragDrop(DependencyObject dragSource, DragDropEffects allowedEffects)
    {
        Action<DragDropEffects> dragFeedback = value =>
        {
            if (value.HasFlag(DragDropEffects.Move)
                && !App.RuntimeSettings.DragModifiers.HasFlag(DragDropKeyStates.ShiftKey))
            {
                // Override default since Windows gives Move as default
                value = DragDropEffects.Copy;
            }

            CurrentEffect = value;
        };

        try
        {
            NativeMethods.MDoDragDrop(this, new DropSource(dragFeedback), allowedEffects);
        }
        catch (Exception e)
        {
#if !DEPLOY
            DebugLog.PrintLine($"Exception in DoDragDrop: {e.Message}");
#endif
        }
        finally
        {
            if (!inOperation)
                ReleaseDragData();
        }
    }

    /// <summary>
    /// Contains the methods for generating visual feedback to the end user and for cancelling or completing the drag-and-drop operation.
    /// </summary>
    private class DropSource(Action<DragDropEffects> onFeedback = null) : NativeMethods.IDropSource
    {
        /// <summary>
        /// Determines whether a drag-and-drop operation should continue.
        /// </summary>
        /// <param name="fEscapePressed">Indicates whether the Esc key has been pressed since the previous call to QueryContinueDrag or to DoDragDrop if this is the first call to QueryContinueDrag. A TRUE value indicates the end user has pressed the escape key; a FALSE value indicates it has not been pressed.</param>
        /// <param name="grfKeyState">The current state of the keyboard modifier keys on the keyboard. Possible values can be a combination of any of the flags MK_CONTROL, MK_SHIFT, MK_ALT, MK_BUTTON, MK_LBUTTON, MK_MBUTTON, and MK_RBUTTON.</param>
        /// <returns>This method returns S_OK/DRAGDROP_S_DROP/DRAGDROP_S_CANCEL on success.</returns>
        public int QueryContinueDrag(int fEscapePressed, uint grfKeyState)
        {
            var escapePressed = (0 != fEscapePressed);
            App.RuntimeSettings.DragModifiers = (DragDropKeyStates)grfKeyState;
            
            var res = escapePressed switch
            {
                true => NativeMethods.HResult.DRAGDROP_S_CANCEL,
                false when App.RuntimeSettings.DragModifiers.HasFlag(DragDropKeyStates.RightMouseButton) => NativeMethods.HResult.DRAGDROP_S_CANCEL,
                false when !App.RuntimeSettings.DragModifiers.HasFlag(DragDropKeyStates.LeftMouseButton) => NativeMethods.HResult.DRAGDROP_S_DROP,
                _ => NativeMethods.HResult.Ok,
            };

            if (res is not NativeMethods.HResult.Ok)
            {
                App.RuntimeSettings.DragBitmap = null;
                App.CopyPaste.DragStatus = CopyPasteService.DragState.None;
                IpcService.NotifyDropCancel(res);
            }
            App.CopyPaste.DragResult = res;

            return (int)res;
        }

        /// <summary>
        /// Gives visual feedback to an end user during a drag-and-drop operation.
        /// </summary>
        /// <param name="dwEffect">The DROPEFFECT value returned by the most recent call to IDropTarget::DragEnter, IDropTarget::DragOver, or IDropTarget::DragLeave. </param>
        public int GiveFeedback(uint dwEffect)
        {
            var dragDropEffects = (DragDropEffects)dwEffect & ~DragDropEffects.Scroll;
            onFeedback?.Invoke(dragDropEffects);

            if (dragDropEffects is DragDropEffects.None)
                return (int)NativeMethods.HResult.DRAGDROP_S_USEDEFAULTCURSORS;

            // Set default cursor when cursor control is manual
            Mouse.SetCursor(Cursors.Arrow);
            return (int)NativeMethods.HResult.Ok;
        }
    }

    public static void SaveFileContents(System.Windows.IDataObject dataObject, int index, string filePath)
    {
        var fmtEtc = CreateFormat(AdbDataFormats.FileContents, index);

        ((System.Runtime.InteropServices.ComTypes.IDataObject)dataObject).GetData(ref fmtEtc, out STGMEDIUM medium);

        IStream stream = null;
        try
        {
            stream = (IStream)Marshal.GetObjectForIUnknown(medium.unionmember);
            NativeMethods.SaveComStreamToFile(stream, filePath);
        }
        finally
        {
            if (stream is not null && Marshal.IsComObject(stream))
                Marshal.ReleaseComObject(stream);

            Vanara.PInvoke.Ole32.ReleaseStgMedium(in medium);
        }
    }

    private async Task SendToClipboardAsync()
    {
        try
        {
            if (Application.Current is App app)
                await app.SetClipboardAsync(this).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ReleaseDragData();
            App.ReportBackgroundFailure(ex, "clipboard.set");
        }
    }
}
