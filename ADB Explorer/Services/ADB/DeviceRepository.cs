using ADB_Explorer.Models;
using AdvancedSharpAdbClient.Models;

namespace ADB_Explorer.Services;

internal sealed record DeviceDelta(
    long Sequence,
    IReadOnlyList<LogicalDevice> Added,
    IReadOnlyList<LogicalDevice> Updated,
    IReadOnlyList<string> RemovedIds,
    IReadOnlyList<LogicalDevice> Devices,
    IReadOnlyDictionary<string, long> DeviceSequences,
    long CapturedTimestamp);

internal sealed class DeviceRepository
{
    private readonly object sync = new();
    private readonly Dictionary<string, LogicalDevice> devices = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> deviceSequences = new(StringComparer.Ordinal);
    private long sequence;
    private long deviceSequence;

    public DeviceDelta Apply(IEnumerable<DeviceData> deviceData)
    {
        var incoming = deviceData
            .Where(device => !string.IsNullOrWhiteSpace(device.Serial))
            .Select(LogicalDevice.New)
            .GroupBy(device => device.ID, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);

        lock (sync)
        {
            var added = incoming
                .Where(item => !devices.ContainsKey(item.Key))
                .Select(item => item.Value)
                .ToArray();
            var updated = incoming
                .Where(item => devices.TryGetValue(item.Key, out var current) && !DeviceEquals(current, item.Value))
                .Select(item => item.Value)
                .ToArray();
            var removed = devices.Keys.Except(incoming.Keys, StringComparer.Ordinal).ToArray();

            if (added.Length == 0 && updated.Length == 0 && removed.Length == 0)
                return null;

            devices.Clear();
            foreach (var item in incoming)
                devices.Add(item.Key, item.Value);

            foreach (var device in added.Concat(updated))
                deviceSequences[device.ID] = ++deviceSequence;
            foreach (var id in removed)
                deviceSequences.Remove(id);

            return new(
                ++sequence,
                added,
                updated,
                removed,
                devices.Values.OrderBy(device => device.ID, StringComparer.Ordinal).ToArray(),
                new Dictionary<string, long>(deviceSequences, StringComparer.Ordinal),
                Stopwatch.GetTimestamp());
        }
    }

    private static bool DeviceEquals(LogicalDevice current, LogicalDevice incoming)
    {
        var currentData = current.DeviceData;
        var incomingData = incoming.DeviceData;
        return current.Status == incoming.Status
            && current.Type == incoming.Type
            && current.Name == incoming.Name
            && currentData.Serial == incomingData.Serial
            && currentData.State == incomingData.State
            && currentData.Model == incomingData.Model
            && currentData.Product == incomingData.Product
            && currentData.Name == incomingData.Name
            && (currentData.Features ?? []).SequenceEqual(incomingData.Features ?? [], StringComparer.Ordinal)
            && currentData.Usb == incomingData.Usb
            && currentData.TransportId == incomingData.TransportId
            && currentData.Message == incomingData.Message;
    }
}
