using ADB_Explorer.Services;
using ADB_Explorer.ViewModels;
using AdvancedSharpAdbClient.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Linq;

namespace ADB_Test;

[TestClass]
public class DeviceRepositoryTests
{
    [TestMethod]
    public void RepositoryProducesStableIncrementalSnapshots()
    {
        var repository = new DeviceRepository();
        var first = new DeviceData
        {
            Serial = "device-1",
            State = DeviceState.Online,
            Model = "model-1",
        };

        var added = repository.Apply([first]);
        Assert.HasCount(1, added.Added);
        Assert.HasCount(0, added.Updated);
        Assert.HasCount(0, added.RemovedIds);
        Assert.AreEqual("device-1", added.Devices.Single().ID);
        long firstDeviceSequence = added.DeviceSequences["device-1"];

        Assert.IsNull(repository.Apply([first]));

        var changed = repository.Apply([new DeviceData
        {
            Serial = "device-1",
            State = DeviceState.Offline,
            Model = "model-1",
        }]);
        Assert.HasCount(0, changed.Added);
        Assert.HasCount(1, changed.Updated);
        Assert.AreEqual(ADB_Explorer.Models.AbstractDevice.DeviceStatus.Offline, changed.Updated.Single().Status);
        Assert.IsGreaterThan(firstDeviceSequence, changed.DeviceSequences["device-1"]);

        var removed = repository.Apply([]);
        Assert.HasCount(0, removed.Devices);
        Assert.HasCount(0, removed.DeviceSequences);
        CollectionAssert.AreEqual(new[] { "device-1" }, removed.RemovedIds.ToArray());
    }

    [STATestMethod]
    public void FullSnapshotReconcilesDevicesWhenIntermediateDeltaIsSkipped()
    {
        var repository = new DeviceRepository();
        var first = repository.Apply([new DeviceData
        {
            Serial = "device-1",
            State = DeviceState.Online,
        }]);
        var devices = new Devices();
        devices.ApplySnapshot(
            first,
            first.Devices.Select(device => new LogicalDeviceViewModel(device, false)));

        _ = repository.Apply(
        [
            new DeviceData { Serial = "device-1", State = DeviceState.Online },
            new DeviceData { Serial = "device-2", State = DeviceState.Online },
        ]);
        var latest = repository.Apply([new DeviceData
        {
            Serial = "device-2",
            State = DeviceState.Online,
        }]);
        var existingIds = devices.LogicalDeviceViewModels.Select(device => device.ID).ToHashSet();

        devices.ApplySnapshot(
            latest,
            latest.Devices
                .Where(device => !existingIds.Contains(device.ID))
                .Select(device => new LogicalDeviceViewModel(device, false)));

        Assert.HasCount(1, devices.LogicalDeviceViewModels);
        Assert.AreEqual("device-2", devices.LogicalDeviceViewModels.Single().ID);
    }

    [STATestMethod]
    public void TenConnectDisconnectCyclesNeverDuplicateCardsOrReuseStaleState()
    {
        var repository = new DeviceRepository();
        var devices = new Devices();
        long lastSequence = 0;

        for (int cycle = 0; cycle < 10; cycle++)
        {
            var online = repository.Apply([new DeviceData
            {
                Serial = "device-1",
                State = DeviceState.Online,
            }]);
            var added = online.Devices
                .Where(device => devices.LogicalDeviceViewModels.All(existing => existing.ID != device.ID))
                .Select(device => new LogicalDeviceViewModel(device, false));
            devices.ApplySnapshot(online, added);

            Assert.HasCount(1, devices.LogicalDeviceViewModels);
            Assert.AreEqual(ADB_Explorer.Models.AbstractDevice.DeviceStatus.Ok, devices.LogicalDeviceViewModels.Single().Status);
            Assert.IsGreaterThan(lastSequence, online.DeviceSequences["device-1"]);
            lastSequence = online.DeviceSequences["device-1"];

            var removed = repository.Apply([]);
            devices.ApplySnapshot(removed, []);

            Assert.IsEmpty(devices.LogicalDeviceViewModels);
            Assert.IsEmpty(removed.DeviceSequences);
        }
    }
}
